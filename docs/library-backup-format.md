# Local catalogue backup format

The mobile app's catalogue can be exported as a versioned JSON backup and restored later. The backup contains catalogue metadata only: title, author, narrator, duration, entry ID and local status. It does not contain account credentials, access tokens, Audible cookies, audio files or download queue state.

Format version 1 is intentionally explicit so future releases can reject unsupported formats rather than silently corrupting a catalogue. Imports must contain 1–5,000 entries, fit within 10 MB, have unique non-empty IDs, and include title and author for every entry. The import service validates the complete document before returning entries to the caller. The caller should ask for confirmation before replacing the current catalogue.

Backups are ordinary JSON and are not encrypted. Store them somewhere private. Do not share a backup publicly if catalogue metadata is sensitive.
