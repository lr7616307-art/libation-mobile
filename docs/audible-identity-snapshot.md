# Audible identity snapshot format

**Status:** The app-owned snapshot DTO, strict serializer, restore mapper, and single-value secret-store wrapper are implemented. Token-refresh persistence is covered by separate tests. The core suite now exercises an AES-GCM test-store round-trip and corruption handling. Android Keystore behavior and physical-device recovery still require device testing.

## Scope and pinned dependency

This format targets the pinned `AudibleApi` package version **14.1.2**. It stores one complete identity envelope through `ISecretStore`; it is not a generic account-session record. The envelope must never be written as plaintext JSON, an ordinary preference, a backup, a log entry, or an exported file.

## Identity fields

The mapper captures the identity values exposed by the pinned AudibleApi contract and restores them through the public validating value-object constructors and `Identity.Update(...)` API.

| Snapshot field | AudibleApi identity source | Handling |
|---|---|---|
| `LocaleName` | `Identity.Locale.Name` | Required; resolve through AudibleApi on restore |
| `AccessToken` | `Identity.ExistingAccessToken` | Required; raw token |
| `AccessTokenExpiresAtUtc` | `Identity.ExistingAccessToken.Expires` | Required; preserve exact UTC expiry |
| `RefreshToken` | `Identity.RefreshToken` | Required; raw token |
| `PrivateKey` | `Identity.PrivateKey` | Required; reconstruct through validating `PrivateKey` constructor |
| `AdpToken` | `Identity.AdpToken` | Required; reconstruct through validating `AdpToken` constructor |
| `Cookies` | `Identity.Cookies` | Required collection; preserve order, values, and duplicate names |
| `DeviceSerialNumber` | `Identity.DeviceSerialNumber` | Nullable |
| `DeviceType` | `Identity.DeviceType` | Nullable |
| `AmazonAccountId` | `Identity.AmazonAccountId` | Nullable |
| `DeviceName` | `Identity.DeviceName` | Nullable |
| `StoreAuthenticationCookie` | `Identity.StoreAuthenticationCookie` | Required field; empty value allowed |

The browser-registration `OAuth2` authorization object is transient and is not part of a completed identity snapshot. If the process dies before registration produces a complete identity, restart sign-in rather than restoring a partial authenticated session.

The app does not serialize AudibleApi's object graph or call its convenience file persister. The explicit DTO avoids depending on upstream JSON converters or per-field persistence behavior.

## Envelope and validation

The encrypted plaintext is a single UTF-8 JSON object containing:
- `schemaVersion` (currently 1)
- `provider` (currently `audible`)
- `audibleApiVersion` (currently `14.1.2`)
- The identity fields listed above

The serializer rejects unknown schema/provider/package versions, unknown JSON members, missing required fields, malformed token prefixes, empty key/token values, invalid ADP envelope shape, missing expiry, null cookie entries, excessive JSON depth, and payloads larger than 16 KiB. Restore then resolves the locale and reconstructs the strongly typed AudibleApi credentials before calling `Identity.Update(...)`.

Unknown versions are rejected; never silently downgrade, guess a migration, truncate, or fall back to plaintext. A future schema migration must be explicit and separately tested.

## Persistence and failure behavior

`AudibleIdentitySnapshotStore.SaveAsync` serializes the complete snapshot in memory and makes one `ISecretStore.SetAsync` call under the dedicated key `audible_identity_v1`. It does not write files or ordinary preferences itself. On Android, `AndroidSecretStore` encrypts the value using AES-GCM with a non-exportable AES key held by Android Keystore and stores the encoded ciphertext in app-private SharedPreferences.

Load returns no identity for absent data, invalid JSON/envelope data, unsupported versions, malformed Base64 storage encoding, or authenticated-decryption failure. Other storage I/O failures propagate so the app does not mistake an unavailable store for sign-out. Save failures propagate. The abstraction has no transaction or compare-and-swap contract: one SetAsync call is made, but cross-implementation atomic replacement must not be assumed.

Token refresh is serialized by the identity maintainer. A refreshed access token is persisted before being returned as usable; if persistence fails, the previous in-memory access token is restored. The maintainer does not perform deregister/register recovery after refresh failure.

## Tests and remaining verification

Core tests use an AES-GCM test double to verify a complete encrypted round-trip, that credential strings do not appear in the stored representation, authenticated ciphertext tampering, malformed storage encoding, unsupported schema rejection, and storage failure. Separate refresh tests cover persist-before-return, rollback on persistence failure, concurrent refresh, and refresh failure behavior.

These tests verify the snapshot-store contract; they do **not** execute Android Keystore APIs. Before production use, perform physical Android tests for key creation/reuse, decrypt after process restart, key invalidation, app-data restore behavior, and recovery from corrupted preferences. The Android Keystore implementation and backup exclusion also need device-level verification.

## Lifecycle rules still required

1. A lifecycle coordinator must await snapshot persistence for every identity update; do not use async-void persistence from a synchronous event handler.
2. Serialize refresh, login, and disconnect. Disconnect must prevent a late refresh from recreating a deleted identity.
3. A persistence failure must fail the login/refresh operation and surface only a safe, non-secret error.
4. Exclude the identity record from Android backup and all user-visible library backup/export operations.
5. Keep browser callback URLs, response parameters, and sign-in cookies in process memory only; never place them in an Android intent/deep link or the snapshot.
6. Never log tokens, cookies, ADP tokens, private keys, or serialized identity payloads.

## Explicit non-goals

This format does not implement browser login, device registration, library synchronization, audiobook downloads, or DRM handling. Format tests must use synthetic credentials only.
