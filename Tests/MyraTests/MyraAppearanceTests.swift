import AppKit
import XCTest

@testable import Myra

@MainActor
final class MyraAppearanceTests: XCTestCase {
  func testEverySelectableFamilyHasAllDockRenditions() {
    for family in MyraIconFamily.allCases {
      for appearance in [MyraIconAppearance.light, .dark, .glass] {
        let image = MyraAppearance.image(family: family, appearance: appearance, dark: false)
        XCTAssertNotNil(image, "Missing \(family.rawValue)/\(appearance.rawValue)")
        XCTAssertEqual(image?.size.width, 512)
        XCTAssertEqual(image?.size.height, 512)
      }
    }
  }

  func testFollowSystemResolvesBothAppearances() {
    for family in MyraIconFamily.allCases {
      XCTAssertNotNil(MyraAppearance.image(family: family, appearance: .automatic, dark: false))
      XCTAssertNotNil(MyraAppearance.image(family: family, appearance: .automatic, dark: true))
    }
  }
}
