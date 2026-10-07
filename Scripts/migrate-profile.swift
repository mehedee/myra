#!/usr/bin/env swift
import AppKit
import Foundation
import Security

// One-time explicit migration; original data and credentials are retained for rollback.
// Usage: swift Scripts/migrate-profile.swift SOURCE_BUNDLE_ID SOURCE_APP_NAME
let arguments = CommandLine.arguments
func fail(_ message: String) -> Never {
  fputs(message + "\n", stderr)
  exit(1)
}
guard arguments.count == 3 else {
  fail("Supply the previous bundle identifier and application name.")
}
let sourceID = arguments[1]
let sourceName = arguments[2]
guard sourceID != "com.mehedee.Myra",
  sourceID.range(of: "^[A-Za-z0-9.-]+$", options: .regularExpression) != nil,
  sourceName.range(of: "^[A-Za-z0-9_-]+$", options: .regularExpression) != nil
else { fail("Invalid migration source.") }
guard
  !NSWorkspace.shared.runningApplications.contains(where: {
    $0.bundleIdentifier == sourceID || $0.bundleIdentifier == "com.mehedee.Myra"
  })
else { fail("Close the application before migrating its profile.") }
let manager = FileManager.default
let support = manager.homeDirectoryForCurrentUser.appendingPathComponent(
  "Library/Application Support")
let source = support.appendingPathComponent(sourceID)
let destination = support.appendingPathComponent("com.mehedee.Myra")
let stage = support.appendingPathComponent(".myra-migration-" + UUID().uuidString)
guard manager.fileExists(atPath: source.path) else { fail("The source profile does not exist.") }
guard !manager.fileExists(atPath: destination.path) else {
  fail("The Myra profile already exists; nothing was overwritten.")
}
var published = false
defer { if !published { try? manager.removeItem(at: stage) } }
do {
  // Reject redirected profile trees before copying private data.
  let roots =
    [source]
    + (manager.enumerator(at: source, includingPropertiesForKeys: [.isSymbolicLinkKey])?.allObjects
      as? [URL] ?? [])
  for url in roots {
    guard try url.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink != true else {
      fail("Profile contains a symbolic link; migrate it manually.")
    }
  }
  try manager.copyItem(at: source, to: stage)
  try manager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: stage.path)
  for suffix in ["", "-wal", "-shm"] {
    let old = stage.appendingPathComponent(sourceName + ".store" + suffix)
    if manager.fileExists(atPath: old.path) {
      try manager.moveItem(at: old, to: stage.appendingPathComponent("Myra.store" + suffix))
    }
  }
  let preferences = UserDefaults.standard.persistentDomain(forName: sourceID) ?? [:]
  let backup = try PropertyListSerialization.data(
    fromPropertyList: preferences, format: .binary, options: 0)
  let backupURL = stage.appendingPathComponent("ProfilePreferences.backup.plist")
  try backup.write(to: backupURL, options: .atomic)
  try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: backupURL.path)
  let entries = [
    ("tmdb", "read-token"), ("omdb", "api-key"), ("opensubtitles", "api-key"),
    ("opensubtitles", "username"), ("opensubtitles", "password"),
    ("ai.openAI", "personal-api-key"), ("ai.claude", "personal-api-key"),
  ]
  var copiedKeys = 0
  for (suffix, account) in entries {
    let base: [String: Any] = [
      kSecClass as String: kSecClassGenericPassword,
      kSecAttrService as String: sourceID + "." + suffix, kSecAttrAccount as String: account,
    ]
    var request = base
    request[kSecReturnData as String] = true
    var result: CFTypeRef?
    let status = SecItemCopyMatching(request as CFDictionary, &result)
    if status == errSecItemNotFound { continue }
    guard status == errSecSuccess, let data = result as? Data else {
      fail(
        "Credential access was declined; original profile remains untouched. Re-run after allowing access."
      )
    }
    var target = base
    target[kSecAttrService as String] = "com.mehedee.Myra." + suffix
    var probe = target
    probe[kSecReturnData as String] = true
    var existing: CFTypeRef?
    let found = SecItemCopyMatching(probe as CFDictionary, &existing)
    if found == errSecSuccess { continue }  // Never replace an existing Myra credential.
    guard found == errSecItemNotFound else { fail("Could not verify destination credentials.") }
    target[kSecValueData as String] = data
    target[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
    guard SecItemAdd(target as CFDictionary, nil) == errSecSuccess else {
      fail("Could not migrate a credential; originals remain intact.")
    }
    copiedKeys += 1
  }
  var renamed: [String: Any] =
    UserDefaults.standard.persistentDomain(forName: "com.mehedee.Myra") ?? [:]
  for (key, value) in preferences {
    let name = key.replacingOccurrences(of: sourceName, with: "Myra")
    if renamed[name] == nil { renamed[name] = value }
  }
  try manager.moveItem(at: stage, to: destination)
  published = true
  UserDefaults.standard.setPersistentDomain(renamed, forName: "com.mehedee.Myra")
  print("Profile migrated. Original profile retained. Credentials copied: \(copiedKeys).")
} catch {
  fail("Migration failed without modifying the original profile: \(error.localizedDescription)")
}
