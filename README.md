# Libation Mobile

An independent, unofficial Android community client project exploring a mobile interface for compatible Libation functionality. It is not affiliated with Audible or the upstream Libation maintainers.

## Project status

**Early prototype with initial Audible integration.** The app has a JSON-backed local catalogue, search/filter/sort, metadata editing, backup/restore, a persistent metadata-only download queue, an Audible external-browser sign-in panel, an AudibleApi authentication adapter, Android Keystore-backed encrypted identity storage, and an Audible library metadata provider. Account disconnect now clears both the provider's complete encrypted Audible identity and the generic encrypted session marker. The real account flow and library sync still require end-to-end testing on a physical Android device; a provider-agnostic HTTPS streaming downloader is connected to a persistent queue worker with byte-progress reporting, retryable failure states, and cancellation that returns work to the queue. The worker requires an authorized HTTPS URL from an `IAuthorizedAudioDownloadSource`; it now exposes interrupted-download recovery so items left in `Downloading` after a process stop can be returned to the queue, and serializes processing within a worker instance. An Audible URL resolver, Android background scheduling, user-facing download controls, and playback are not yet implemented. APKs are configured for ARMv7 and ARM64; CI compilation is not proof of runtime compatibility.

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
