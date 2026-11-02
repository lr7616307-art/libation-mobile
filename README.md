# Libation Mobile

An independent, unofficial Android community client project exploring a mobile interface for compatible Libation functionality. It is not affiliated with Audible or the upstream Libation maintainers.

## Project status

**Early prototype.** The current app is a sample-data UI scaffold. A JSON-backed local catalogue repository is now connected to the UI. The app seeds clearly labelled demo entries on first launch and reloads the saved catalogue on subsequent launches. A provider-neutral external-browser sign-in contract and session lifecycle coordinator are now in place, but no Audible authentication adapter or login UI exists yet. The app does not yet sign in to Audible, sync a real library, download audiobooks, or integrate the upstream Libation engine. APKs compile in CI for ARMv7 and ARM64; installation and runtime compatibility are not yet verified on a physical device.

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
