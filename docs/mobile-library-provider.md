# Mobile library provider boundary

The mobile application consumes a narrow `ILibraryCatalogProvider` interface that returns catalogue metadata for the authenticated user's own library. Provider-specific authentication, network protocol, marketplace selection, pagination and rate-limit handling belong in a separate adapter. This repository does not yet contain an Audible adapter.

`LibrarySyncService` maps provider IDs into stable local IDs prefixed with `provider:`, validates the complete provider response before writing, replaces only prior provider-synced entries, and preserves manually added entries. Demo entries are intentionally not retained during a successful real-library sync. The local repository replacement is performed only after all returned records pass validation.

The service handles metadata only. It does not retrieve audio, decrypt files, bypass access controls, or implement provider authentication. A production provider adapter must follow supported upstream Libation/Audible flows and be reviewed against the pinned upstream source before being connected.
