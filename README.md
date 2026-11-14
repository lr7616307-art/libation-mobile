# Libation Mobile

> **PROJECT ABANDONED**
> 
> This project has been abandoned due to ChatGPT incompetency. Please do not visit this repository expecting ongoing development, support, fixes, or releases. You will just waste your time here.

An independent, unofficial Android community client project exploring a mobile interface for compatible Libation functionality. It is not affiliated with Audible or the upstream Libation maintainers.

## Project status

**Early-stage Android client with local playback and initial Audible integration.** The app has a JSON-backed local catalogue, search/filter/sort, metadata editing, backup/restore, a persistent metadata-only download queue, local audio import, and Android Media3 playback through a foreground service with saved playback position and resume controls. It also includes an Audible external-browser sign-in flow, an AudibleApi authentication adapter, Android Keystore-backed encrypted identity storage, and an Audible library metadata provider. Account disconnect clears both the provider's encrypted Audible identity and the generic encrypted session marker. The real account flow and library sync still require end-to-end testing on a physical Android device. A provider-agnostic HTTPS streaming downloader is connected to a persistent queue worker with byte-progress reporting, retryable failure states, cancellation, interrupted-download recovery, and serialized processing within a worker instance. The worker requires an authorized HTTPS URL from an `IAuthorizedAudioDownloadSource`; Audible-authorized media URL resolution, protected offline download/licensing, and complete user-facing download delivery remain unfinished. APKs are configured for ARMv7 and ARM64; CI compilation is not proof of runtime compatibility.

## Device targets

- Android 10 (API 29) minimum target, best-effort pending device testing.
- ARMv7 (`android-arm`) is a first-class build target for older 32-bit Android phones.
- ARM64 (`android-arm64`) is also configured.

## Cloud builds

GitHub Actions builds separate ARMv7 and ARM64 APKs on pushes to `main`, pull requests, and manual dispatch.

Open the [Actions page](https://github.com/lr7616307-art/libation-mobile/actions) to inspect build status and download APK artifacts when a run succeeds.

## Architecture

- `Libation.Mobile.Core`: mobile-facing models and interfaces.
- `Libation.Mobile.UI`: Avalonia UI prototype.
- `Libation.Mobile.Android`: Android host and platform target.
- `docs/`: compatibility, architecture and upstream integration notes.

See [architecture](docs/architecture.md), [compatibility](docs/compatibility.md), and [upstream audit](docs/upstream-audit.md).

## Legal and project boundaries

Libation is licensed under GPL-3.0. See [LICENSE](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.md). Any future distribution of a derived app must meet the applicable GPL source and notice requirements.

This project is intended for lawful access to content the user is authorized to access. It will not add DRM circumvention or audiobook redistribution features.

## Contributions

Issues and pull requests are welcome. Treat this as experimental software until authentication, library integration, storage, downloads, and device compatibility have been implemented and tested.
