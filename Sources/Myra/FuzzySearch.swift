import Foundation

/// Bounded, deterministic spelling tolerance after SQLite retrieves candidate filenames.
enum FuzzySearch {
  static func normalize(_ text: String) -> String {
    let folded = text.folding(
      options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "en_US_POSIX"))
    return folded.components(separatedBy: CharacterSet.alphanumerics.inverted).filter {
      !$0.isEmpty
    }.joined(separator: " ")
  }

  static func matchExpression(_ query: String) -> String? {
    var fragments: [String] = []
    var seen = Set<String>()
    for token in normalize(query).split(separator: " ") {
      let letters = Array(token)
      guard letters.count >= 3 else { continue }
      for position in 0...(letters.count - 3) {
        let fragment = String(letters[position..<(position + 3)])
        if seen.insert(fragment).inserted { fragments.append("\"\(fragment)\"") }
        if fragments.count == 64 { break }
      }
      if fragments.count == 64 { break }
    }
    return fragments.isEmpty ? nil : fragments.joined(separator: " OR ")
  }

  static func indexGrams(_ text: String) -> String {
    var seen = Set<String>()
    for word in normalize(text).split(separator: " ") {
      let letters = Array(word)
      if letters.count >= 3 {
        for position in 0...(letters.count - 3) {
          seen.insert(String(letters[position..<(position + 3)]))
        }
      }
    }
    return seen.sorted().joined(separator: " ")
  }

  /// Nil rejects a candidate; exact phrases precede prefix and approximate token matches.
  static func score(query: String, name: String) -> Int? {
    let needle = normalize(query)
    let haystack = normalize(name)
    guard !needle.isEmpty, needle.count <= 256 else { return nil }
    if haystack == needle { return 0 }
    if haystack.contains(needle) { return 10 }
    let requested = needle.split(separator: " ").map(String.init)
    let available = haystack.split(separator: " ").map(String.init)
    var total = 20
    for word in requested {
      var best: Int?
      for candidate in available {
        let cost: Int?
        if candidate == word {
          cost = 0
        } else if candidate.hasPrefix(word) {
          cost = 1
        } else if word.allSatisfy(\.isNumber) || word.count < 4 {
          cost = nil
        } else {
          let threshold = word.count >= 8 ? 2 : 1
          let distance = editDistance(word, candidate, maximum: threshold)
          cost = distance <= threshold ? 10 + distance : nil
        }
        if let cost { best = min(best ?? cost, cost) }
      }
      guard let best else { return nil }
      total += best
    }
    return total
  }

  static func editDistance(_ lhs: String, _ rhs: String, maximum: Int) -> Int {
    let a = Array(lhs)
    let b = Array(rhs)
    guard abs(a.count - b.count) <= maximum else { return maximum + 1 }
    var previous = Array(0...b.count)
    for (i, letter) in a.enumerated() {
      var current = [i + 1]
      for (j, other) in b.enumerated() {
        current.append(
          min(previous[j + 1] + 1, current[j] + 1, previous[j] + (letter == other ? 0 : 1)))
      }
      if current.min()! > maximum { return maximum + 1 }
      previous = current
    }
    return previous.last!
  }
}
