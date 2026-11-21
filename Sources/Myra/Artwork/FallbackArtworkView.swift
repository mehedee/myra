#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// Shows the first candidate that loads, moving on after a failure or a missing image.
/// The title card is shown while loading and whenever no candidate loads.
struct FallbackArtworkView: View {
  let title: String
  let candidates: [ArtworkCandidate]
  var loader: ArtworkLoader = .shared

  @State private var image: PlatformImage?

  var body: some View {
    ZStack {
      if let image {
        Image(platformImage: image)
          .resizable()
          .scaledToFill()
      } else {
        TitleCardView(title: title)
      }
    }
    .clipped()
    .task(id: candidates) {
      image = nil
      image = await firstLoadedImage()
    }
  }

  private func firstLoadedImage() async -> PlatformImage? {
    for candidate in candidates {
      guard case .image(_, let url) = candidate else { return nil }
      if Task.isCancelled { return nil }
      if let data = try? await loader.data(for: url), let loaded = PlatformImage(data: data) {
        return loaded
      }
    }
    return nil
  }
}
