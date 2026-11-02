# Upstream audit notes

Repository: https://github.com/rmcrackan/Libation  
License: GPL-3.0  
Official development guide: https://getlibation.com/docs/development/getting-started

## Project graph findings

- Upstream targets .NET 10.
- `AudibleUtilities` references `LibationFileManager` and Google.Protobuf; it contains account, identity-token, marketplace and authentication models.
- `LibationFileManager` references the AudibleApi NuGet package, AAX decrypter, FileManager and configuration/logging infrastructure.
- `ApplicationServices` references `DtoImporterService`, `LibationSearchEngine`, `DataLayer.Postgres` and `DataLayer.Sqlite`.
- `LibationSearchEngine` references `DataLayer`, `FileManager` and `LibationFileManager`.
- `DataLayer` references both Entity Framework SQLite and PostgreSQL providers.
- `FileLiberator` references AaxDecrypter, ApplicationServices, DataLayer and AudibleUtilities.
- `LibationAvalonia` is a desktop WinExe and references `LibationUiBase`; it is not a mobile entry point.

## Implications for Android

- Do not add `ApplicationServices`, `FileLiberator` or `LibationAvalonia` as a direct Android project reference.
- A narrow mobile integration likely needs a separately designed persistence adapter and selected authentication/API models.
- `AudibleUtilities` is not dependency-free: its project reference to `LibationFileManager` brings in AudibleApi, AAX-related and configuration dependencies.
- Account data contains refresh/access tokens, device private keys, cookies and activation bytes. Any mobile implementation must use platform-backed secure storage, redact logs, and avoid exporting secrets.
- Authentication and marketplace registration should be explicitly user-initiated and must be tested against current upstream APIs.
- Download orchestration is coupled to file paths, database entities, retry services and decryption components; it needs a deliberate mobile port rather than a project-reference shortcut.

## Mobile strategy

1. Pin an upstream Libation commit and record it in this document.
2. Build a call graph from supported account import/login to API library retrieval.
3. Define a narrow mobile repository contract around real library metadata.
4. Selectively port or adapt compatible code, preserving GPL-3.0 notices.
5. Replace desktop dialogs, filesystem paths, settings assumptions and background execution with Android implementations.
6. Add tests for credential redaction, account serialization, library mapping and download state transitions.

## Scope boundary

This is a project-file and selected-source audit, not a completed authentication or download port. No upstream engine code is currently included in this repository. Re-check APIs against the pinned upstream commit before integration.
