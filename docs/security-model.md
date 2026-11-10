# Mobile credential security model

## Encrypted secret storage

The Android host provides `AndroidSecretStore`, implementing `ISecretStore`. It uses an AES-256-GCM key generated inside Android Keystore and stores only authenticated ciphertext in app-private SharedPreferences. The key is non-exportable through the app API.

New writes use a version-2 envelope with a fresh 96-bit IV. The logical preference key is authenticated as AES-GCM additional authenticated data (AAD), so copying ciphertext to another secret key fails authentication. The store can read the previous unversioned envelope and lazily migrates it after successful decryption. A migration write failure is surfaced; it is not reported as a successful migration.

Reading existing ciphertext never creates a replacement Keystore key. Missing or invalidated keys, malformed envelopes, unsupported versions, and authentication failures fail closed. The encrypted data is preserved for explicit recovery rather than silently overwritten. The versioned envelope and AAD framing are covered by deterministic core tests.

## Audible identity and account lifecycle

The Android host wires the pinned AudibleApi 14.1.2 integration through `AudibleAccountAuthenticator`, `AccountConnectionService`, an Android external-browser launcher, and an `AudibleIdentitySnapshotStore`. The complete Audible identity is stored as one validated, versioned encrypted snapshot through Android Keystore-backed secret storage. The authenticated API is restored through the app-owned identity maintainer; the file-oriented convenience persister is not used.

The sign-in flow is user-assisted and browser-based:
- The app opens the generated Audible marketplace login URL in the user's external browser.
- The user completes Amazon/Audible sign-in and pastes the final address-bar URL into the app.
- The app does not collect the Amazon password in an embedded form.
- The response URL is treated as a short-lived credential-bearing value and is not logged, persisted, exported, or placed in Android intents.
- The pending challenge is one-time and expires. Cancellation and failed registration/persistence leave the app disconnected.

Before the pinned upstream OAuth parser is called, the mobile authenticator validates that the response is an absolute HTTPS URL with no embedded user information, uses the default port, and has the exact host associated with the selected marketplace login attempt. This rejects unrelated and deceptive suffix hosts. The upstream parser extracts the authorization-code query parameter but does not itself validate the host; marketplace host binding is enforced by the mobile layer.

The Audible identity lifecycle coordinator serializes identity creation/replacement, token refresh commits, and clearing. New sign-in or disconnect operations invalidate stale in-flight work before it can commit. Token refresh is persisted as a complete encrypted identity snapshot before the refreshed credential is returned. Disconnect clears the snapshot and makes that maintainer instance reject later credential requests.

The generic `AccountSessionStore` and provider-neutral `AccountConnectionService` remain separate abstractions; a generic account-session pair is not a substitute for the full Audible identity.

## Security rules

- Store access tokens, refresh tokens, device keys, cookies, and other account secrets only through the encrypted secret/identity stores. Never put them in ordinary preferences, databases, files, UI state, backups, logs, analytics, or diagnostic exports.
- Never log raw sign-in response URLs, authorization headers, cookies, activation bytes, private keys, or provider response bodies that may contain credentials.
- Keep response URLs in memory only for the active sign-in operation. Do not automatically read the clipboard.
- Do not silently replace a missing Keystore key or discard ciphertext to recover a session.
- Treat audiobook catalogue metadata separately from credentials.
- Do not implement authentication bypass, DRM circumvention, fabricated licenses, or unauthorized redistribution. A catalogue entry or ASIN is not proof of download entitlement or a media URL.

## Limitations and required validation

Automated tests and successful ARMv7/ARM64 CI builds do not prove real-account operation on a physical handset. Physical Android 10 / ARMv7 validation remains outstanding for:
- external browser switching and return to the app;
- copy/paste of the final response URL;
- MFA/CAPTCHA completion and app resume;
- encrypted identity persistence and restoration after process restart;
- successful authenticated Audible library synchronization;
- Keystore persistence across force-stop/relaunch and explicit failure behavior after key invalidation.

Do not claim the sign-in or library-sync feature is fully validated until these device/account-owner checks succeed. Keystore invalidation checks should use a dedicated test device and non-production test secrets, never a real Audible credential.
