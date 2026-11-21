import SwiftUI

/// Moves keyboard focus to a page's search field when the Find menu command runs.
/// `searchFocused` needs macOS 15; on macOS 14 the command is accepted and does nothing.
struct SearchFocusIfAvailable: ViewModifier {
  var isFocused: FocusState<Bool>.Binding

  func body(content: Content) -> some View {
    if #available(macOS 15.0, iOS 18.0, *) {
      content.searchFocused(isFocused)
    } else {
      content
    }
  }
}
