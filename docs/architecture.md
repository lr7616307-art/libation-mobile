# Architecture

## Product structure

The Android app is being developed incrementally around four primary destinations:

1. **Home** — downloaded audiobooks and playback.
2. **Library** — Audible-synced catalogue, search, sorting and download selection.
3. **Account** — Audible sign-in, marketplace and identity/token lifecycle.
4. **Settings** — storage, naming, download behavior, network policy, backup and diagnostics.

Audiobook details and the chapter-aware player are shared flows reached from Home and Library. Torrent/magnet support is explicitly out of scope for the current delivery.

## Layers

- **Core**: mobile domain models, repositories, queue, download abstractions and platform-neutral interfaces.
- **UI**: Avalonia views and view models shared across supported targets.
- **Android**: Android application host, foreground playback service, secure storage and Android-specific lifecycle integration.
- **Audible adapter**: AudibleApi integration for account identity, token refresh and library catalogue retrieval.

## Integration and security principles

1. Audit upstream project references before adding dependencies; avoid importing the complete desktop application or its database stack into Android.
2. Keep Android storage, foreground work, media playback and secure key handling behind narrow platform interfaces.
3. Treat Audible identity snapshots, tokens, cookies, private keys and device metadata as secrets. Never log them or include them in catalogue backups.
4. Use provider-issued download URLs and the official Widevine licensing flow for protected Audible media. Do not implement AAX decryption or DRM circumvention.
5. Verify the requested and returned DRM type; do not silently treat protected downloads as ordinary playable audio.
6. Preserve applicable GPL-3.0 and dependency licensing notices and publish corresponding source when distributing derived binaries.

## Implemented today

- A JSON-backed local audiobook catalogue with search, sorting/filtering, editing, backup export/import and local audio import.
- A persistent download queue abstraction with retry/removal and a generic HTTPS streaming downloader. The generic downloader does not authenticate to Audible, discover Audible media URLs or decrypt protected content.
- Audible account authentication and identity refresh adapters using AudibleApi, plus a library catalogue provider.
- Android local-file playback through Media3 ExoPlayer hosted by an Android foreground service. The service owns playback independently of the Avalonia view. Playback position is checkpointed periodically and when the view unloads.
- CI builds for ARMv7 and ARM64 targets. ARMv7 remains essential for the current Android 10 device compatibility target.

## In progress / not yet complete

- The four-destination navigation shell and separation of Home, Library, Account and Settings.
- Reconnecting the UI to an already-running playback service after view recreation, including restoring the selected audiobook identity.
- Audible account UI and secure Android-backed persistence validation.
- The provider-authorized Audible download and offline Widevine license pipeline, followed by protected local playback.
- Player chapters, richer metadata, lock-screen/media-session controls and robust process-restart recovery.
- Download quality, multi-select/batch queueing, storage layout controls, Wi-Fi-only policy and diagnostics.

## Release discipline

A successful CI build is necessary but does not replace final-device verification. Do not label a feature complete until its real Android behavior and failure paths have been verified. Intermediate builds are for engineering validation; user acceptance is intended for a release candidate rather than disposable prototypes.
