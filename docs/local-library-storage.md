# Local library storage

## Current implementation

JsonLibraryRepository stores the mobile catalogue as JSON at a caller-supplied path. The Android host must supply an app-private path; callers must not point it at shared storage for credentials or private account data.

The repository supports:
- Reading the catalogue (an absent file means an empty catalogue).
- Replacing the complete catalogue.
- Inserting or updating one audiobook by stable ID.
- Removing an audiobook by ID.
- Serializing concurrent operations within one repository instance.
- Writing to a temporary file in the same directory and replacing the old file after serialization completes.

The repository validates non-empty IDs and rejects duplicate IDs in a replacement set. It does not store account tokens, audio files, download state transitions, or Audible credentials.

## Deliberate limitations

- This is a local persistence foundation, not Audible library synchronization.
- The current prototype UI still uses demo entries and is not wired to this repository.
- The JSON file is not encrypted. Keep only non-secret catalogue metadata here; account secrets belong in Android Keystore-backed storage.
- Atomic replacement and concurrent access still need device-level validation on Android 10.
- A future SQLite implementation may replace this adapter if catalogue size or query needs justify it, while preserving the ILibraryRepository contract.
