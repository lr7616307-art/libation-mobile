# Upstream audit notes

Repository: https://github.com/rmcrackan/Libation  
License: GPL-3.0  
Official development guide: https://getlibation.com/docs/development/getting-started

## Findings
- Upstream is a .NET 10 / Avalonia desktop solution.
- Major areas include ApplicationServices, AudibleUtilities, DataLayer, FileManager and LibationAvalonia.
- The desktop application and its service graph are not a suitable direct Android project reference.
- ApplicationServices brings in persistence and database dependencies, including PostgreSQL and SQLite-related projects.
- Download functionality crosses multiple upstream projects and filesystem-oriented services.

## Mobile strategy
- Identify the minimum set of source projects and classes needed for account access, library metadata and download orchestration.
- Replace desktop dialogs, filesystem paths, settings assumptions and background execution with Android implementations.
- Keep upstream code changes isolated and document the exact upstream commit and modified files.

## Important
This is an initial architecture audit, not a completed source-level port. Re-check project references and APIs against the exact upstream commit before integrating.
