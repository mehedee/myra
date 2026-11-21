import SwiftUI

/// The sparkle prompt row from the preview (`.ai .prompt`): one line that grows for longer
/// requests, with a return-key hint. The text is the user's own request.
struct MyraAIPromptField: View {
  @Binding var text: String
  let label: String
  var placeholder = "Ask Myra"
  var submit: () -> Void = {}

  @Environment(\.myraAccent) private var accent

  var body: some View {
    HStack(alignment: .firstTextBaseline, spacing: 8) {
      Image(systemName: "sparkles")
        .font(.system(size: 14, weight: .medium))
        .foregroundStyle(accent.color)
        .frame(width: 16)
        .accessibilityHidden(true)
      TextField(placeholder, text: $text, axis: .vertical)
        .textFieldStyle(.plain)
        .font(.system(size: 13))
        .lineLimit(1...4)
        .onSubmit(submit)
        .accessibilityLabel(label)
      Text("↩")
        .font(.system(size: 13))
        .foregroundStyle(MyraColor.text3)
        .accessibilityHidden(true)
    }
    .padding(.horizontal, 12)
    .padding(.vertical, 10)
    .background(MyraColor.content, in: RoundedRectangle(cornerRadius: 10))
    .overlay(RoundedRectangle(cornerRadius: 10).stroke(MyraColor.lineStrong, lineWidth: 1))
  }
}

/// The streamed answer. While `streaming` is true a block cursor blinks at the end of the text,
/// as in the preview's `.answer .cursor`.
struct MyraAIAnswerText: View {
  let text: String
  var streaming = false

  @Environment(\.myraAccent) private var accent

  var body: some View {
    Group {
      if streaming {
        TimelineView(.periodic(from: .now, by: 0.5)) { context in
          Text(composed(cursorVisible: Self.cursorVisible(at: context.date)))
        }
      } else {
        Text(text)
      }
    }
    .font(.system(size: 13))
    .lineSpacing(4)
    .frame(maxWidth: .infinity, alignment: .leading)
    .textSelection(.enabled)
  }

  /// Blinks once per second, matching the preview's one-step animation.
  static func cursorVisible(at date: Date) -> Bool {
    Int(date.timeIntervalSinceReferenceDate * 2) % 2 == 0
  }

  private func composed(cursorVisible: Bool) -> AttributedString {
    var result = AttributedString(text)
    var cursor = AttributedString(cursorVisible ? "▍" : " ")
    cursor.foregroundColor = accent.color
    result.append(cursor)
    return result
  }
}

/// One recommendation, as the preview's `.rec`: a 38pt mini poster, the title and a status line.
/// Clicking the card plays it; the context menu also offers the watchlist. The model's reason is
/// the card's tooltip. Cards fade in one after another, 120 ms apart.
struct MyraAIRecommendationCard: View {
  let title: EntertainmentTitle
  let statusLine: String
  let reason: String
  let playLabel: String
  let inWatchlist: Bool
  let index: Int
  let play: () -> Void
  let toggleWatchlist: () -> Void

  @State private var shown = false
  @State private var hovering = false

  @Environment(\.myraAccent) private var accent

  var body: some View {
    Button(action: play) {
      HStack(alignment: .top, spacing: 10) {
        FallbackArtworkView(title: title.displayName, candidates: posterCandidates)
          .frame(width: 38, height: 57)
          .clipShape(RoundedRectangle(cornerRadius: 5))
        VStack(alignment: .leading, spacing: 2) {
          Text(title.displayName)
            .font(.system(size: 12, weight: .bold))
            .foregroundStyle(MyraColor.text)
            .lineLimit(2)
          Text(statusLine)
            .font(.system(size: 11))
            .foregroundStyle(MyraColor.text2)
            .lineLimit(2)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
      }
      .padding(8)
      .frame(maxWidth: .infinity, alignment: .leading)
      .background(MyraColor.content, in: RoundedRectangle(cornerRadius: 10))
      .overlay(
        RoundedRectangle(cornerRadius: 10)
          .stroke(hovering ? accent.color : MyraColor.line, lineWidth: 1))
      .contentShape(RoundedRectangle(cornerRadius: 10))
    }
    .buttonStyle(.plain)
    .onHover { hovering = $0 }
    .help(reason)
    .contextMenu {
      Button(playLabel, action: play)
      Button(inWatchlist ? "Remove from Watchlist" : "Add to Watchlist", action: toggleWatchlist)
    }
    .accessibilityLabel("\(playLabel) \(title.displayName)")
    .accessibilityHint(reason)
    .accessibilityAction(
      named: Text(inWatchlist ? "Remove from Watchlist" : "Add to Watchlist"), toggleWatchlist)
    .opacity(shown ? 1 : 0)
    .offset(y: shown ? 0 : 4)
    .onAppear {
      withAnimation(.easeOut(duration: 0.25).delay(Double(index) * 0.12)) { shown = true }
    }
  }

  private var posterCandidates: [ArtworkCandidate] {
    guard let posterURL = title.posterURL else { return [.titleCard] }
    return [.image(.tmdbPoster, posterURL)]
  }
}

/// The grounding footer: provider, pick count, timing and privacy note, in one wrapped line.
struct MyraAIGroundingFooter: View {
  let items: [String]

  var body: some View {
    FlowLayout(spacing: 12) {
      ForEach(items, id: \.self) { item in
        Text(item)
      }
    }
    .font(.system(size: 11))
    .foregroundStyle(MyraColor.text2)
  }
}

/// Plain text for the grounding footer and recommendation status lines. No model or network calls.
enum MyraAIGroundingLine {
  /// "Thinking…" while a run is open, then the time to the first words and the total time.
  static func timingLabel(
    running: Bool, secondsToFirstWords: Double?, totalSeconds: Double?
  ) -> String? {
    if let totalSeconds { return String(format: "Done in %.1f s", max(0, totalSeconds)) }
    if let secondsToFirstWords {
      return String(format: "First words in %.1f s", max(0, secondsToFirstWords))
    }
    return running ? "Thinking…" : nil
  }

  static func privacyNote(provider: MyraAIProvider) -> String {
    provider.isCloud ? "Sent to \(provider.title) under your AI settings" : "Nothing left this \(MyraDevice.name)"
  }

  static func footerItems(
    providerName: String, pickCount: Int, timing: String?, provider: MyraAIProvider
  ) -> [String] {
    let picks = pickCount == 1 ? "1 pick from your library" : "\(pickCount) picks from your library"
    var items = [providerName, picks]
    if let timing { items.append(timing) }
    items.append(privacyNote(provider: provider))
    return items
  }

  /// "10 min · Movie · unwatched", with the episode label when one is attached.
  static func recommendationStatus(
    runtimeMinutes: Int?, kind: String, episodeLabel: String?, inWatchlist: Bool, watched: Bool
  ) -> String {
    var parts: [String] = []
    if let runtimeMinutes { parts.append("\(runtimeMinutes) min") }
    parts.append(kind)
    if let episodeLabel { parts.append(episodeLabel) }
    parts.append(inWatchlist ? "in watchlist" : (watched ? "watched" : "unwatched"))
    return parts.joined(separator: " · ")
  }
}
