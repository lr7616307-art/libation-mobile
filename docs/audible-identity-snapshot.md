# Audible identity snapshot format

**Status:** The snapshot DTO, strict serializer, secret-store persistence wrapper, AudibleApi restore mapper, serialized refresh maintainer, and an opt-in identity lifecycle coordinator are implemented. Live Audible sign-in integration, application-wide coordinator wiring, and Android runtime validation remain incomplete.

## Scope

The snapshot is for the pinned AudibleApi 14.1.2 integration. It is one complete, versioned record encrypted by the app's Android Keystore-backed ISecretStore. It is not the generic AccountSession record and must never be written as plaintext JSON, a normal preference, a backup, a log entry, or an exported file.

## Verified identity contract

The upstream IIdentity contract and concrete Identity implementation expose these values:

| Field | Required handling |
|---|---|
| Locale | Persist the stable locale name; resolve it through AudibleApi on restore |
| ExistingAccessToken | Persist the raw token and exact expiry timestamp |
| RefreshToken | Persist the raw token |
| PrivateKey | Persist the complete private-key material; reconstruct through AudibleApi's validating PrivateKey constructor |
| AdpToken | Persist the complete ADP token; reconstruct through AudibleApi's validating AdpToken constructor |
| Cookies | Preserve every name/value pair and original order; duplicates are explicitly supported |
| DeviceSerialNumber | Preserve nullable/string value |
| DeviceType | Preserve nullable/string value |
| AmazonAccountId | Preserve nullable/string value |
| DeviceName | Preserve nullable/string value |
| StoreAuthenticationCookie | Preserve the secret value |
| Authorization (OAuth2) | Transient browser-registration state; do not persist as a completed identity. If the process dies before registration produces a complete identity, restart sign-in rather than restoring a partial authenticated session. |

Identity.FromJson is an upstream deserializer, not an app-owned encrypted-storage boundary. Do not pass arbitrary files or plaintext JSON to the convenience file persister. Do not assume its per-field encryption behavior is equivalent to the app's single-payload Keystore encryption.

## Proposed envelope

The encrypted plaintext is a single UTF-8 JSON envelope with an explicit schema version:

~~~json
{
  "schemaVersion": 1,
  "provider": "audible",
  "audibleApiVersion": "14.1.2",
  "localeName": "…",
  "accessToken": { "value": "…", "expiresUtc": "…" },
  "refreshToken": "…",
  "privateKey": "…",
  "adpToken": "…",
  "cookies": [{ "name": "…", "value": "…" }],
  "deviceSerialNumber": "…",
  "deviceType": "…",
  "amazonAccountId": "…",
  "deviceName": "…",
  "storeAuthenticationCookie": "…"
}
~~~

The ellipses are explanatory placeholders, not valid credential values. The serialized payload must only exist in memory until ISecretStore.SetAsync succeeds. `AudibleIdentitySnapshotStore` now uses the dedicated `audible_identity_v1` key and stores the whole JSON payload in one ISecretStore value. It returns no identity for absent, invalid, unsupported, or cryptographically unreadable payloads; ordinary storage I/O failures propagate. Use a dedicated key such as audible_identity_v1; the secret store's AES-GCM authentication protects ciphertext integrity. Reject unknown schema versions, missing required fields, malformed token/key material, invalid timestamps, and oversized payloads. Do not silently downgrade or fall back to plaintext.

The envelope is intentionally app-owned rather than serializing the upstream Identity object directly. This avoids relying on upstream JSON converters or their persistence/protection fallback behavior. A restore mapper reconstructs validated AudibleApi value objects and calls the supported `Identity.Update(...)` method only after the envelope and every strongly typed credential have been validated. This only proves that the in-memory object was populated; it does not prove that credentials are currently accepted by Audible.

## Persistence and lifecycle rules

1. `AudibleIdentitySnapshotStore` saves one complete identity snapshot through `ISecretStore`; never save access and refresh tokens independently.
2. `AudibleIdentityMaintainer` serializes token refresh and waits for snapshot persistence before returning a refreshed token. If persistence fails, it restores the previous in-memory access token and reports a safe error.
3. `AudibleIdentityLifecycleCoordinator` serializes identity creation/replacement, refresh commits, and clearing. A newer login/replacement or clear increments a generation so older in-flight results cannot be committed as current.
4. Disconnect invalidates pending work immediately, waits for the active operation, clears the snapshot, and makes that maintainer instance reject later credential requests. If clearing fails, the instance is not marked disconnected.
5. The coordinator is opt-in. Application composition must inject the same coordinator instance into the login/replacement flow and every maintainer for the same account. The app-wide sign-in/replacement composition is not wired to it yet; separate coordinator instances do not coordinate.
6. The pinned AudibleApi refresh call has no cancellation-token overload, so an already-running remote refresh cannot be cancelled by this adapter. A disconnect still invalidates its result before the snapshot can be committed.
7. Never use async-void persistence from a synchronous identity event. Own an explicit awaited update/persist boundary.
8. On restore, invalid encrypted data or unsupported schema requires re-authentication. Never log exception payloads, tokens, cookies, keys, or serialized envelopes.
9. Exclude the identity record from Android backup and user-visible library backup/export operations; verify this on a physical device.
10. Keep browser callback URLs and transient authorization data in process memory only. The 16 KiB snapshot limit must be validated against real-world complete snapshots before live account testing.

## Implementation sequence

- [x] Add an app-owned immutable snapshot DTO and strict versioned serializer/deserializer.
- [ ] Add mapping tests for every field, duplicate cookies, exact access-token expiry, missing required values, and invalid key/token material.
- [x] Add secret-store wrapper tests for serialized round-trip, invalid/unsupported payload, cryptographic failure, storage failure, and clear. Tests use a fake ISecretStore; Android Keystore encryption is not exercised by these unit tests. Atomic replacement guarantees are not established by the ISecretStore interface and must not be assumed.
- [x] Add lifecycle tests for superseded login, clear racing with login, refresh invalidation during commit, cancellation, save failure, and disconnect racing with refresh.
- [ ] Wire one shared lifecycle coordinator into the app's sign-in/replacement composition and all maintainers for that account.
- [x] Verify the actual AudibleApi 14.1.2 public constructors and update/event semantics before writing the adapter.
- [x] Add a restore mapper that validates the snapshot, resolves the locale, reconstructs AudibleApi value objects, and calls `Identity.Update(...)` without network access.
- [x] Add an opt-in lifecycle coordinator and route refresh/disconnect through it; application-wide login/replacement wiring remains outstanding.
- [ ] Build ARMv7 and ARM64; then perform physical Android 10 tests before claiming runtime support.

## Current implementation boundary\n\n`ISecretStore` documents encryption at rest. The Android implementation uses Android Keystore AES-256-GCM and commits ciphertext to app-private SharedPreferences. The abstraction exposes no transaction or compare-and-swap contract; this wrapper makes one SetAsync call for replacement but does not claim cross-implementation atomicity. A future lifecycle coordinator must await SaveAsync explicitly; this class does not subscribe to synchronous identity events.\n\n## Explicit non-goals

This format does not implement browser login, token refresh, device registration, library sync, audio downloading, or DRM handling. No live account credentials are required for format tests.
