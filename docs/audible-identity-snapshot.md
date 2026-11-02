# Audible identity snapshot format

**Status:** snapshot DTO, strict serializer, and secret-store persistence wrapper are implemented. Live Audible sign-in, identity reconstruction, refresh integration, and Android runtime validation are not implemented.

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

The envelope is intentionally app-owned rather than serializing the upstream Identity object directly. This avoids relying on upstream JSON converters or their persistence/protection fallback behavior. A restore mapper must reconstruct validated AudibleApi value objects and call the supported IIdentity.Update(...) method only after every field has been validated.

## Persistence and lifecycle rules

1. Implemented: `AudibleIdentitySnapshotStore` saves a complete identity snapshot as one ISecretStore value. Never save access/refresh tokens independently.
2. Subscribe to Identity.Updated and serialize persistence through one async gate. Identity updates must not race and overwrite a newer snapshot with an older one.
3. An update is not considered durable until encrypted storage has committed. Persistence failure must fail the login/refresh operation and surface a safe, non-secret error.
4. Do not allow a refresh to return success to the app until its resulting identity has been durably saved.
5. If the upstream Updated event is synchronous, do not perform async-void persistence from the event handler. The integration must own an explicit async update/persist boundary or use a carefully designed queue whose completion is awaited by the operation that caused the update.
6. Serialize refresh/login/disconnect operations. Disconnect must prevent a late refresh completion from recreating a deleted identity.
7. On restore, treat missing/corrupt/unreadable ciphertext or an unsupported schema as disconnected and require re-authentication. Never log the exception payload, token, cookie, key, or serialized envelope.
8. Exclude this record from Android backup and all user-visible library backup/export operations.
9. Keep the browser response URL and sign-in cookies in process memory only; never place them in the snapshot or an Android intent/deep link.
10. The snapshot serializer and Android secret store both enforce a 16 KiB plaintext limit. Real-world complete snapshots have not yet been measured; do not truncate, and revisit this limit before live account testing.

## Implementation sequence

- [x] Add an app-owned immutable snapshot DTO and strict versioned serializer/deserializer.
- [ ] Add mapping tests for every field, duplicate cookies, exact access-token expiry, missing required values, and invalid key/token material.
- [x] Add secret-store wrapper tests for serialized round-trip, invalid/unsupported payload, cryptographic failure, storage failure, and clear. Tests use a fake ISecretStore; Android Keystore encryption is not exercised by these unit tests. Atomic replacement guarantees are not established by the ISecretStore interface and must not be assumed.
- [ ] Add lifecycle tests for identity updates, failed persistence, concurrent refresh/update, interrupted writes, and disconnect racing with refresh.
- [ ] Verify the actual AudibleApi 14.1.2 public constructors and update/event semantics before writing the adapter.
- [ ] Build ARMv7 and ARM64; then perform physical Android 10 tests before claiming runtime support.

## Current implementation boundary\n\n`ISecretStore` documents encryption at rest. The Android implementation uses Android Keystore AES-256-GCM and commits ciphertext to app-private SharedPreferences. The abstraction exposes no transaction or compare-and-swap contract; this wrapper makes one SetAsync call for replacement but does not claim cross-implementation atomicity. A future lifecycle coordinator must await SaveAsync explicitly; this class does not subscribe to synchronous identity events.\n\n## Explicit non-goals

This format does not implement browser login, token refresh, device registration, library sync, audio downloading, or DRM handling. No live account credentials are required for format tests.
