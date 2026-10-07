// swift-tools-version: 6.0
import PackageDescription

let package = Package(
  name: "Myra",
  platforms: [.macOS(.v14)],
  products: [.executable(name: "Myra", targets: ["Myra"])],
  dependencies: [
    .package(url: "https://github.com/scinfu/SwiftSoup.git", from: "2.9.6")
  ],
  targets: [
    .binaryTarget(name: "VLCKit", path: "Vendor/VLCKit.xcframework"),
    .systemLibrary(name: "CSQLite", path: "Sources/CSQLite"),
    .executableTarget(
      name: "Myra",
      dependencies: ["SwiftSoup", "CSQLite", "VLCKit"],
      path: "Sources/Myra",
      exclude: ["Resources/TMDB.svg"],
      resources: [
        .copy("Resources/VLC.icns"), .copy("Resources/TMDB.png"), .copy("Resources/MyraIcons"),
      ]
    ),
    .testTarget(
      name: "MyraTests",
      dependencies: ["Myra", "CSQLite"],
      path: "Tests/MyraTests",
      exclude: ["Fixtures"]
    ),
  ]
)
