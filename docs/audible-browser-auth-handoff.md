# Audible browser authentication handoff audit

## Status

Design investigation only. This document does not claim that Audible sign-in works.

The app must not ship a login button that opens a URL unless the complete upstream handoff can be completed securely on Android.

## Verified upstream API shape

The mobile adapter references `AudibleApi 14.1.2`. The external-login API surface reviewed for this integration is:

- `ExternalLogin(Locale, DeviceRegistrationProfile?)`
- `GetLoginUrl()` returns the provider-generated sign-in URL.
- `GetSignInCookies()` returns a `.NET CookieCollection` of initial cookies required at the sign-in page.
- `Login(responseUrl)` parses the final response URL into an in-memory `Identity`.
- The `ILoginExternal.GetResponseUrl(loginUrl, signInCookies)` callback is synchronous and returns the final URL.

The convenience `EzApiCreator.GetApiAsync(ILoginExternal, ...)` also uses a file-oriented identity persister. Do not use that convenience overload in the mobile app because the app must persist the full identity only through its Keystore-backed encrypted snapshot store.

## Android handoff constraint

The existing `AndroidExternalBrowserLauncher` starts an `ACTION_VIEW` intent with an HTTPS URL. This launches a browser but does not install the supplied `CookieCollection` into that browser's cookie jar. Therefore, simply opening `GetLoginUrl()` with this launcher is not a complete implementation of `ILoginExternal`.

The sign-in cookies are generated for the login domain and include registration-flow state. They must not be:
- placed in an Android intent or app/deep link;
- copied to clipboard;
- written to ordinary preferences, files, backups, logs or analytics;
- exposed to the UI.

The final response URL is also sensitive. Keep it in process memory only and pass it directly to `ExternalLogin.Login`; do not log it, persist it, or route it through an exported deep link.

## Candidate approaches to evaluate before implementation

1. **Supported system-browser handoff:** Determine whether the upstream login flow can be completed in the user's normal browser while correctly applying the initial cookies through a documented, safe mechanism. Do not assume Android Custom Tabs can import an arbitrary .NET CookieCollection.
2. **Embedded WebView:** Do not adopt by default. First verify Amazon's current sign-in requirements and restrictions for embedded user agents, CAPTCHA/MFA behavior, cookie isolation, and whether the supported flow permits it. If unsupported or blocked, reject this approach rather than attempting to evade provider controls.
3. **User-assisted final URL transfer:** The upstream desktop CLI accepts a user-provided final address-bar URL, but this is not automatically a secure mobile UX. Do not accept a URL from an untrusted external intent. If a manual transfer is ever considered, require an explicit in-app confirmation, strict one-time challenge binding, expiry, and no logging; first assess whether the URL can be safely handled without clipboard leakage.

No option is approved yet. A successful browser launch is not authentication success.

## Identity lifecycle after a valid callback

The repository already has these components:
- `AudibleIdentitySnapshot` and strict serializer;
- `AudibleIdentitySnapshotStore`, which writes one serialized identity through `ISecretStore`;
- Android `AndroidSecretStore`, which encrypts values with an Android Keystore AES-GCM key;
- `AudibleIdentitySnapshotMapper.Restore`;
- `AudibleIdentityMaintainer`, which serializes refresh and persists refreshed tokens;
- `AudibleLibraryCatalogProvider`, which accepts an already-authorized AudibleApi `Api`.

After a valid callback:
1. Call `ExternalLogin.Login(responseUrl)` in memory.
2. Perform the upstream registration exchange using the verified upstream `IAuthorize.RegisterAsync` flow and the profile selected explicitly by the user/product policy. Do not deregister an existing device as a recovery shortcut.
3. Parse registration into the in-memory identity using the upstream registration parser and clock.
4. Capture every required identity field into one validated `AudibleIdentitySnapshot`.
5. Save the snapshot through `AudibleIdentitySnapshotStore`; report connected only after the encrypted save completes.
6. Construct the app-owned identity maintainer and `AudibleApi.Api` from the restored identity. Never call the file-based identity persister.
7. Keep refresh, save, and disconnect serialized. If persistence fails, fail closed and do not claim a durable session.

## Device registration policy

Upstream source defines `DeviceRegistrationProfile.Default` as `CurrentAndroid`; the profile is used only for a new registration and cannot convert existing tokens. Device registration may affect license behavior. The app must use the exact pinned-package default or an explicitly reviewed choice; it must not fabricate device identifiers, silently change profiles, or experiment on an existing account.

## Required tests before UI wiring

- Login attempts are single-flight and have bounded timeout/cancellation.
- Invalid, expired, replayed, or mismatched callback responses are rejected.
- Initial cookies never leave the in-process adapter.
- Final response URLs and tokens never appear in logs, intents, analytics, or backups.
- Registration failure leaves no saved identity.
- Snapshot persistence failure prevents a connected state.
- Process restart restores the full identity from encrypted storage.
- Refresh and disconnect cannot race into a resurrected session.
- A physical Android 10 ARMv7 test covers the selected browser mechanism, CAPTCHA/MFA, return-to-app, encrypted save, restart, and library read.

## Decision

Do not implement a bare external-browser launch as the Audible authenticator. First prove a provider-compatible cookie-aware handoff on Android, then wire the verified callback to the existing identity snapshot and API-provider components.
