# AudibleApi mobile adapter assessment

## Scope and source versions

Reviewed the Libation source snapshot pinned in this repository's upstream audit:
`7f37f3a62e5269ec3f5ccf4906fc30ecccec088d`.

Also reviewed the separate upstream `rmcrackan/AudibleApi` source at the version corresponding to NuGet package `AudibleApi 14.1.2` (the version referenced by Libation's `LibationFileManager.csproj`).

This is an integration assessment, not a claim that Audible sign-in or sync works in this app.

## What is reusable

- The `AudibleApi` project targets `net10.0` and its NuGet package declares a `net10.0` asset. NuGet computes `net10.0-android` as a compatible target framework. This is encouraging for a mobile adapter, but it is not proof of runtime compatibility on Android 10 or either ARM ABI; an actual Android build and device test remain required.
- The package has a narrow dependency on `Dinah.Core` (the NuGet package page lists it as its dependency), rather than the full Libation desktop service graph.
- `AudibleApi.EzApiCreator.GetApiAsync` is the supported entry point. The external-browser overload accepts an `ILoginExternal` callback, a `Locale`, an identity-file path, optional JSON path and optional device-registration profile.
- `AudibleApi.ExternalLogin` generates the login URL, supplies initial sign-in cookies, and parses the final browser response URL into an in-memory `Identity`.
- `Api.GetLibraryItemsPagesAsync` / `Api.GetAllLibraryItemsAsync` provide paginated library retrieval. The library model exposes the catalogue metadata needed by the mobile provider boundary.

## Important blockers before wiring it in

1. **Authentication callback is synchronous.** `ILoginExternal.GetResponseUrl(loginUrl, signInCookies)` is synchronous and returns a final URL. The current app's challenge flow is asynchronous and expects a provider-created HTTPS authorization URI. A mobile adapter needs a carefully designed handoff that does not block the UI thread, preserves the callback's cookies only as required by the supported upstream flow, and safely resumes the waiting operation after the user returns from the browser. Do not pass cookies in an intent, deep link, log, or clipboard.
2. **The final URL is sensitive.** It is a short-lived authentication response. It must remain in process memory, must not be logged, exported, backed up, or sent to analytics. The upstream parser should receive it directly; the app should not parse or rewrite its query parameters.
3. **Identity persistence is file-oriented.** The `EzApiCreator` overload reads/writes identity tokens using a file path and optional JSON path. The existing mobile `ISecretStore` / `AccountSessionStore` model stores generic access and refresh token strings, which is not yet proven equivalent to Audible's serialized `Identity`. Do not serialize an Audible identity into ordinary JSON preferences or force it into the generic session shape without inspecting its fields and designing encrypted, atomic persistence.
4. **Device registration is security- and licensing-sensitive.** The upstream CLI passes a configured `DeviceRegistrationProfile`; upstream documentation says the registration profile can affect license behavior. The app must not invent a profile or silently switch one. The supported default and any user choice must be confirmed against the current upstream release before implementation.
5. **Marketplace is account identity, not just a query parameter.** Upstream `Account` binds tokens to one registration marketplace and can scan additional marketplaces using the same identity. The mobile data model currently has a single marketplace string. Multi-marketplace support should be explicit rather than pretending each store is a separately authenticated account.
6. **The package is not the whole Libation importer.** Libation's `ApiExtended` adds retry behavior, response-group selection, series/episode relationship resolution, validation and sanitization. A first mobile adapter should deliberately implement a conservative catalogue-only mapping, not claim feature parity with Libation's importer.
7. **License obligations need review.** The separate `rmcrackan/AudibleApi` repository identifies its license as AGPL-3.0, while Libation is GPL-3.0. Before shipping a combined binary, preserve both projects' notices and have the distribution/source obligations reviewed. Do not assume the dependency inherits this repository's license.

## Proposed implementation sequence

1. Add a separate `Libation.Mobile.Audible` adapter project referencing only the official `AudibleApi` NuGet package (and its transitive dependencies), not `AudibleUtilities`, `LibationFileManager`, desktop UI, database, or decryption projects.
2. First add compile-time integration tests for locale resolution, library page retrieval, mapping to `RemoteAudiobook`, cancellation and duplicate/invalid metadata handling, using a fake or injected API boundary. No real account is required for these tests.
3. Design an asynchronous external-browser handoff around the upstream synchronous `ILoginExternal` callback. Ensure cancellation, timeout, process recreation, cookie handling, and one-login-at-a-time behavior are specified before implementing it.
4. Inspect the exact `Identity` serialization and token refresh APIs, then implement encrypted persistence using Android Keystore-backed storage. Keep raw identity JSON out of backups and logs.
5. Confirm the current device-registration profile with upstream documentation and test a new login with an account owner. Do not change registration on an existing account as an experiment.
6. Build both `android-arm` and `android-arm64`, then test browser return, sign-in, token refresh, library scan and disconnect on a physical Android device. CI compilation alone is not runtime validation.

## Current status

- The provider-neutral sync contract and validation service exist in `Libation.Mobile.Core`.
- The Android external-browser launcher exists and accepts only absolute HTTPS URLs.
- There is **no Audible authenticator, Audible library provider, working Audible sign-in, or live library sync** in the app yet.
- No audio downloading or decryption is part of this assessment.

## Primary references

- [AudibleApi source](https://github.com/rmcrackan/AudibleApi)
- [AudibleApi NuGet package 14.1.2](https://www.nuget.org/packages/AudibleApi/14.1.2)
- [Libation external login adapter](https://github.com/rmcrackan/Libation/blob/7f37f3a62e5269ec3f5ccf4906fc30ecccec088d/Source/LibationCli/Options/LoginExternalOptions.cs)
- [Libation API wrapper](https://github.com/rmcrackan/Libation/blob/7f37f3a62e5269ec3f5ccf4906fc30ecccec088d/Source/AudibleUtilities/ApiExtended.cs)
- [AudibleApi external login implementation](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/EzApiCreator/EzApiCreator.LoginExternal.cs)
- [AudibleApi external login callback](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/EzApiCreator/ILoginExternal.cs)
- [AudibleApi library API](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Api.Library.cs)
- [Libation device-registration guide](https://github.com/rmcrackan/Libation/blob/master/docs/advanced/device-registration.md)

## Follow-up source inspection: catalogue fields and identity storage

A direct inspection of the upstream `AudibleApi.Common.Item` and `Api.Library.cs` source confirms:

- `Item.ProductId` aliases `Asin`; `LengthInMinutes` aliases `RuntimeLengthMin`; `TitleWithSubtitle` is derived from `Title` and `Subtitle`; `Publisher` aliases `PublisherName`.
- `LibraryOptions.ResponseGroupOptions.Contributors` exists and serializes to the `contributors` response group. The generated contributor DTO shape and the exact author/narrator role mapping still need to be inspected before mapping. **Do not map Publisher to Author.** Until verified, author/narrator must remain empty rather than mislabelled.
- `GetAllLibraryItemsAsync` has overloads for response groups, page size and concurrency, but no `CancellationToken` parameter. The paged API is an async enumerable, but the inspected overloads likewise expose no cancellation-token parameter. A mobile adapter must document that cancellation can prevent starting or accepting a result but cannot necessarily abort an in-flight upstream page request.

Identity persistence is a further integration blocker:

- `EzApiCreator.GetApiAsync` takes a filesystem identity path and uses `IdentityPersister` / `JsonFilePersister`; the public flow does not expose a stream or a caller-supplied persistence abstraction.
- Identity JSON contains access/refresh tokens, private key, ADP token, cookies and device/account identifiers. The library's serializer marks encrypted fields and delegates protection to `IdentityTokenStorage`.
- Critically, the serializer's `TryProtect` catches protection/store-unavailable failures and **continues by writing plaintext**, while logging that encryption failed. This behavior is not acceptable for a mobile app that promises Keystore-backed storage. Do not assume the library's JSON identity file is encrypted just because the app has an Android Keystore secret store.
- Before using the convenience `EzApiCreator` file-based flow, the adapter must prove that the upstream secret protector is available and fails closed on Android, or use a supported lower-level API arrangement that keeps identity persistence behind the app's encrypted store. Do not patch or fork upstream cryptographic behavior casually.

### Verified upstream source files

- [Item model](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi.Common/Item.cs)
- [Library options and paging API](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Api.Library.cs)
- [Identity persistence wrapper](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Authorization/IdentityPersister.cs)
- [Identity JSON serializer and protection fallback](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Authorization/Identity.TokenJson.cs)
- [Identity contract](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Authorization/IIdentity.cs)

## Revised secure integration path: bypass the file-based convenience creator

Further inspection found a viable public extension seam that avoids the unsafe file-based identity persistence path:

- `Api` has public constructors accepting an `IIdentityMaintainer` (and optionally an `IHttpClientSharer`).
- `IIdentityMaintainer` is a small public contract: locale, system clock, device/account identifiers, and async access-token, ADP-token and private-key getters.
- `IdentityMaintainer.CreateAsync(identity, authorize, systemDateTime)` is public and accepts injected authorization and clock implementations. The normal maintainer updates the in-memory `Identity` during registration/refresh.
- `Identity` exposes its `Updated` event and token/key/cookie properties through `IIdentity`. A mobile-owned maintainer can therefore own the identity lifecycle and persist snapshots through the app's Keystore-backed secret store, without handing the identity to `IdentityPersister` or calling `EzApiCreator.GetApiAsync`.

This is a **candidate design**, not implemented or runtime-proven. Before coding it, verify the exact public constructors and event behavior against the pinned 14.1.2 package, then implement a mobile maintainer with these requirements:

1. Keep the complete Audible identity snapshot (including private key, ADP token, refresh/access tokens, cookies and device metadata) encrypted as one versioned payload using the app's Android Keystore-backed storage. Never persist a partial generic access/refresh-token pair.
2. Subscribe to `Identity.Updated` and persist atomically. If persistence fails, fail the login/refresh operation rather than continuing with an identity that will be lost on process death.
3. Inject the authorization service and clock into the upstream `IdentityMaintainer.CreateAsync(identity, authorize, systemDateTime)` path where possible; inspect `Authorize` construction and dependencies before choosing the concrete implementation.
4. Expose only the minimum getters required by `IIdentityMaintainer`; never log or return raw secrets to UI code.
5. Serialize access-token refresh and identity updates; protect the snapshot against concurrent writes and process recreation.
6. Keep the initial browser callback and final response URL in memory only. Do not route credentials through app links, clipboard, analytics, or logs.
7. Add tests for encrypted round-trip, persistence failure, interrupted writes, refresh updates, and concurrent refresh attempts before wiring sign-in into the UI.

### Additional verified upstream extension points

- [Authenticated API constructor](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Api%20%5Bpartial%5D.cs)
- [Identity maintainer contract](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Authorization/IIdentityMaintainer.cs)
- [Identity maintainer factory overloads](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Authorization/IdentityMaintainer.cs)
- [Authorization contract](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi/Authorization/IAuthorize.cs)

The custom maintainer seam is preferable to trying to configure the process-global `IdentityTokenStorage` for a multi-account mobile app: that global helper is not itself an app-owned persistence abstraction, and the serializer's encryption failure path still falls back to plaintext.
