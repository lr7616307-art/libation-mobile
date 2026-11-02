# Mobile library provider boundary

The mobile application consumes a narrow `ILibraryCatalogProvider` interface that returns catalogue metadata for the authenticated user's own library. Provider-specific authentication, network protocol, marketplace selection, pagination and rate-limit handling belong in a separate adapter.

## Current implementation

- `Libation.Mobile.Core` defines `ILibraryCatalogProvider` and `LibrarySyncService`.
- `Libation.Mobile.Audible` references the official `AudibleApi 14.1.2` package and implements `AudibleLibraryCatalogProvider`.
- The provider accepts an already-authorized `AudibleApi.Api` instance. It requests the Contributors response group, reads pages of 50 with a maximum concurrency of two, and maps each DTO through `AudibleItemMapper`.
- The adapter maps author and narrator only from their respective DTO arrays. It never infers contributors from publisher metadata.
- The provider rejects a response exceeding the 5,000-item mobile safety limit and checks cancellation before starting, between pages, and between items.

## Sync behavior

`LibrarySyncService` maps provider IDs into stable local IDs prefixed with `provider:`, validates the complete provider response before writing, replaces only prior provider-synced entries, and preserves manually added entries. Demo entries are intentionally not retained during a successful real-library sync. The local repository replacement is performed only after all returned records pass validation.

The service handles metadata only. It does not retrieve audio, decrypt files, or bypass access controls.

## Important limitations

- The Android app does not yet construct an authorized AudibleApi instance or inject this provider into `MainViewModel`. The UI therefore continues to report that Audible sync is unavailable.
- Audible sign-in, identity refresh, encrypted identity persistence, disconnect, and process-recreation recovery are not implemented.
- The upstream paged library API does not accept a cancellation token. Cancellation can stop processing between pages/items, but may not abort a network request already in progress.
- CI compilation for Android ARMv7 and ARM64 is not equivalent to a runtime test on a physical Android device.

A production connection must use the supported external-browser login flow and app-owned, fail-closed encrypted identity persistence. See `docs/audibleapi-adapter-assessment.md` for the security and upstream API findings.
