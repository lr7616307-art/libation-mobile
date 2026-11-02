# Upstream integration plan

1. Pin an upstream Libation commit and record it in this document.
2. Map the authentication and Audible API call graph.
3. Map library retrieval and metadata models.
4. Map download orchestration and file-handling boundaries.
5. Identify desktop-only dependencies and isolate compatible services.
6. Implement mobile adapters for persistence, file access, lifecycle and notifications.
7. Add tests for serialization, repository behavior and download state transitions.
8. Validate legal notices and GPL-3.0 corresponding-source obligations before public binary releases.

No upstream engine code is currently included in this repository.

## Account lifecycle boundary (implemented)

The core now contains `IAccountAuthenticator`, `AccountConnectionService` and a
credential-free `AccountConnectionState`. The service persists sign-in results through
`AccountSessionStore`, delegates refresh to a provider adapter, reports disconnected /
connected / expired states, and clears stored sessions on explicit disconnect.

The provider adapter is deliberately not implemented yet. Before connecting to Audible,
the next audit must map the pinned upstream authentication flow, device identity handling,
token lifecycle, marketplace selection, and current provider requirements. Do not invent
endpoints or treat a generic username/password form as a supported authentication flow.
## External browser sign-in design

The upstream user guide documents an alternate login where Libation opens or displays a
sign-in URL, the user completes Amazon/Audible authentication in a browser (including any
CAPTCHA or additional verification), then returns the final browser URL to Libation. The
mobile contract now models this handoff using `AccountSignInRequest`,
`ExternalSignInChallenge`, and `IAccountAuthenticator`. The app must not ask users to type
their Amazon password into a custom form.

This is a design seam, not a working Audible integration. The provider adapter still needs
to be mapped from the pinned upstream implementation and reviewed against current provider
requirements. Do not guess endpoints, redirect formats, client identifiers, or device
registration values.
