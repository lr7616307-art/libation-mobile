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

## Upstream authentication audit — pinned source review

Reviewed upstream commit `7f37f3a62e5269ec3f5ccf4906fc30ecccec088d`:

- `Source/LibationCli/Options/LoginExternalOptions.cs` resolves a canonical Audible locale, creates/updates an account record, checks for an already-valid identity, selects a device-registration profile, then delegates sign-in to `EzApiCreator.GetApiAsync`.
- Its `CliLoginExternal` implementation receives the generated login URL and sign-in cookies, asks the user to open the URL in a normal browser, then accepts the final address-bar URL. The CLI validates only that the returned value is an absolute URI before handing it to the Audible API library.
- `Source/AudibleUtilities/ApiExtended.cs` first attempts API creation from saved identity tokens. If that fails and interactive login is enabled, it serializes interactive login through a semaphore and calls `EzApiCreator.GetApiAsync` with the configured device-registration profile.
- `Source/AudibleUtilities/Account.cs` distinguishes the account's registration marketplace from additional marketplaces. Additional marketplaces reuse the account's registered identity; they are not separate sign-in credentials.
- `docs/advanced/device-registration.md` documents `CurrentAndroid` as the current default registration and `Mkb79IPhone` as experimental without Widevine. Device registration affects license behavior, so it must not be guessed or silently changed by the mobile app.

The actual URL generation, cookie handling, response parsing, identity-token exchange, and device registration implementation are in the upstream AudibleApi / AudibleUtilities dependency graph, not in the CLI dialog itself. This mobile repository has not yet imported or invoked those components. Reusing only the CLI wrapper would pull in desktop configuration and file-based account storage, which is not an acceptable Android adapter.

Upstream source locations:
- [LoginExternalOptions.cs](https://github.com/rmcrackan/Libation/blob/7f37f3a62e5269ec3f5ccf4906fc30ecccec088d/Source/LibationCli/Options/LoginExternalOptions.cs)
- [ApiExtended.cs](https://github.com/rmcrackan/Libation/blob/7f37f3a62e5269ec3f5ccf4906fc30ecccec088d/Source/AudibleUtilities/ApiExtended.cs)
- [Account.cs](https://github.com/rmcrackan/Libation/blob/7f37f3a62e5269ec3f5ccf4906fc30ecccec088d/Source/AudibleUtilities/Account.cs)
- [Device registration guide](https://github.com/rmcrackan/Libation/blob/7f37f3a62e5269ec3f5ccf4906fc30ecccec088d/docs/advanced/device-registration.md)

## Android browser adapter (implemented)

The Android project now has `AndroidExternalBrowserLauncher`, which opens an authorization URI
through Android's external activity resolver. Both the core challenge validator and Android
adapter require an absolute HTTPS URI. The challenge validator also rejects empty IDs and
expired challenges. Tests cover accepted HTTPS challenges and rejection of HTTP, file,
javascript, expired and empty-ID cases.

This is only the platform launch adapter. No Audible provider-generated authorization URI is
currently available because the provider authenticator has not yet been implemented.
