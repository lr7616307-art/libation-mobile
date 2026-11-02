# Architecture

## Goal
Build an independent Android client that reuses compatible Libation domain and service code while replacing desktop-specific UI and operating-system assumptions.

## Layers
- **Core**: mobile-facing domain models and interfaces.
- **UI**: Avalonia single-view interface shared by supported Android targets.
- **Android**: Android application host, permissions, lifecycle, storage and download integration.
- **Upstream adapter (planned)**: narrowly selected Libation services behind mobile interfaces.

## Integration principles
1. Audit upstream project references before adding dependencies.
2. Do not pull the complete desktop application or PostgreSQL stack into Android.
3. Keep Android storage and long-running downloads behind platform interfaces.
4. Keep credentials in Android-secure storage; never log tokens.
5. Preserve GPL-3.0 notices and publish corresponding source when distributing derived binaries.

## Current state
The UI now loads and searches a JSON-backed local catalogue in the app-private data directory. On first launch only, it seeds three clearly labelled demo entries; subsequent launches reload persisted entries. Authentication, real library sync, download queue, file management and upstream engine integration are not implemented.
