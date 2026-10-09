# Myra release updates

Myra uses Sparkle 2.10.0 with one release stream. There is no Stable/Beta selector.
Manual checks are available in Settings and the Myra menu. Automatic checks are
enabled initially, can be disabled, and are scheduled by Sparkle at daily intervals
while Myra is open. The preference and last check survive app restarts. Quiet check
failures are visible in Settings; manual checks show Sparkle's error dialog.

The feed is `https://github.com/mehedee/h5ai-streamer/releases/latest/download/appcast.xml`.
Each release downloads from its immutable version tag, rather than the latest DMG.
The app embeds a public Ed25519 key; its private counterpart stays in the login
Keychain under account `com.mehedee.Myra`. Never export that private key into the
repository. Both the feed and archive must be signed. Archives are verified before
extraction. Anonymous system profiling and unattended installation are disabled.
Sparkle presents notes, version/compatibility information, download progress and
installation/relaunch confirmation. It owns application replacement and failure
recovery; Myra does not rewrite profile data as part of an update.

## Prepare a release locally

1. Increase the marketing version and the monotonically increasing build number.
2. Add `docs/release-notes-VERSION.md`.
3. Run `zsh Scripts/verify-update.sh` in the foreground.
4. Run `zsh Scripts/prepare-update-feed.sh` in the foreground. It checks that the
   app's trusted public key matches Keychain and generates signed artifacts under
   ignored `dist/update/` without uploading anything.
5. Inspect the appcast, archive signature, checksum, notes and architecture/OS
   requirements. Exercise old-to-new updates using disposable app copies before
   publishing; never replace the user's active installation as a test.
6. Only after explicit publication authorization: create the matching `vVERSION`
   GitHub release and attach `Myra.dmg`, `appcast.xml` and checksum. Keep each
   version's DMG available. Do not edit a signed feed after generation.

Until an appcast is published, update checks will report that the feed is unavailable;
this does not mean the current app is up to date. Existing installations without
Sparkle must manually install the first updater-enabled version.

## Recovery and distribution limits

The build retains `dist/Myra.previous.dmg` before rebuilding. Keep versioned DMGs
on the release host. If an update fails, retain its error details and reinstall
the previous app from its DMG. Do not remove Application Support, download folders,
Keychain or preferences. Before a future schema migration, add compatible backup
and recovery coverage. A downgrade may need its matching index backup.

Personal builds remain ad-hoc signed and not notarized. EdDSA archive signatures
provide release authenticity but are not a replacement for Apple Developer ID /
notarization and do not bypass Gatekeeper. Keep the signing Keychain safe: losing
the only signing key can require a new manually installed bootstrap release.
See the [official Sparkle setup](https://sparkle-project.org/documentation/).
