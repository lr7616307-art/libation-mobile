# Android Audible offline download and playback path

## Decision

VØRATH will only play Audible-protected downloads through a provider-authorized Android DRM session. It will not extract content keys, decrypt protected streams outside the platform DRM stack, fabricate licences, or bypass DRM.

The intended Android path is Audible-authorized Widevine content, an offline licence held by Android `MediaDrm`, and playback through a DRM-capable Android media player. A downloaded encrypted file or a successful HTTP response is not a playable audiobook.

## Upstream API behavior verified

The pinned AudibleApi source exposes two distinct operations:

1. `Api.GetDownloadLicenseAsync(asin, quality, chapterTitlesType, DrmType.Widevine)` requests the content licence and media URL.
2. Audible's authenticated `/drmlicense` endpoint accepts the platform-generated Widevine challenge and returns the provider's licence response string. The pinned package also exposes `Api.AdHocAuthenticatedRequestAsync`, which can perform the authenticated request without the logging wrapper.

Important registration behavior: AudibleApi changes a requested Widevine content licence to ADRM when the saved identity's `DeviceType` does not match its Android Audible profile. Therefore, VØRATH must inspect the actual returned `DrmType`; it must never assume that asking for Widevine means Widevine was granted.

Security review note: the inspected upstream `AudibleApi.WidevineDrmLicense` implementation logs its `ApiErrorException` on failure, and that exception can retain the JSON request body containing `licenseChallenge`. The app must not enable this production call until the logging path is proven to redact the challenge or a safe upstream/API transport path is used. Do not assume that suppressing VØRATH's own exception logging also suppresses the dependency's Serilog call.\n\nThe upstream Libation desktop implementation uses a separate CDM library to parse a licence into content keys. That key-extraction/decryption path is not being ported. VØRATH's intended route is for Android `MediaDrm` to create the challenge and consume the licence response through its own `ProvideKeyResponse` lifecycle.

## Pass 8 implementation

- Added `AudibleAudioDownloadSource.ResolveWidevineAsync`, which explicitly requests Widevine, rejects a response that AudibleApi downgraded to ADRM, and returns the provider's HTTPS DASH manifest URL as a manifest reference (not as an ordinary audio-file download target).
- Preserved the existing ADRM download request path separately, so the new Widevine experiment does not silently change existing queue behavior.
- Added `RequestWidevineLicenseAsync` using `Api.AdHocAuthenticatedRequestAsync` and the restored identity authentication. The implementation intentionally omits request/response bodies from errors. `RequestWidevineLicenseBytesAsync` adds Base64 transport serialization for opaque Android challenge bytes and returns opaque licence bytes; no DRM message is parsed. End-to-end provider/device compatibility remains unverified.
- Added validation for the 10-character ASIN, non-empty challenge, cancellation before the provider request, and non-empty provider response.
- Added unit tests for challenge forwarding and safe validation behavior.

## Pass 9 implementation

- Refactored `AudibleApiSessionFactory` to return the validated account marketplace together with the authenticated API client.
- Replaced the unsafe `Api.WidevineDrmLicense` wrapper in the production constructor with the public authenticated request primitive. It preserves ADP/bearer authentication without placing challenge-bearing request bodies in diagnostics.
- Added Base64 transport encoding for Android's opaque challenge/response byte arrays, with bounded sizes and strict Base64 validation.
- Added `AudibleWidevineManifestClient` and `AudibleWidevineManifestParser` to fetch the provider MPD over HTTPS, enforce response-size limits, disable XML DTD/entity processing, and validate the Widevine PSSH and audio MIME type.
- Added `AudibleOfflineLicenseKeySetStore`, which persists only the opaque Android key-set identifier through the Keystore-backed `ISecretStore`.

## Pass 10 implementation — integration groundwork

- Added `AndroidWidevineOfflinePlaybackCoordinator` to join the provider content grant, MPD/PSSH parsing, Android `MediaDrm` offline challenge, Audible licence exchange, `ProvideKeyResponse`, and secure key-set persistence.
- Added cleanup for a just-issued persistent offline key set when secure persistence fails. Explicit local deletion uses Android's persistent `removeOfflineLicense(keySetId)` API; this does not notify Audible's licence server.
- Registered a platform factory that creates the coordinator from the already-saved Audible identity and its account identifier; it does not initiate login or silently change device registration.
- Added a dedicated `AndroidLocalAudioPlayer.StartWidevineDash` entry point and service action. It passes the HTTPS MPD and offline key-set ID to Media3, sets the Widevine UUID and offline key-set ID on the media item's DRM configuration, and uses the Media3 DASH module.
- Protected playback uses a dedicated Media3 cache-only DASH source. The selected encrypted audio representation, MPD and referenced segments are cached under app-private storage; cache keys are title-scoped hashes, not signed provider URLs. The player refuses network fallback on cache misses.
- Protected playback recovery stores only ASIN, title, position and play/pause state in app-private preferences. It never stores account IDs, manifest URLs, stream keys, DRM challenges, licence responses or key-set IDs. On app restart, the saved Audible identity is restored through the existing account factory and Media3 is reopened against the existing encrypted cache and offline key-set ID.

## Pass 11 implementation — encrypted DASH offline media groundwork

- Added an Android app-private Media3 `SimpleCache` and a separate cache-only playback data source. Protected content is never routed through the ordinary clear-audio player.
- Added `DashDownloader` integration using the selected audio representations only. DASH stream selection rejects manifests with ambiguous/multiple adaptation sets rather than caching video or an unverified track.
- Added a hashed account/ASIN cache scope and hashed resource cache keys. A small app-private index records cache keys by title so cache availability and title deletion survive process death without retaining signed Audible URLs as cache identifiers.
- Added an 8 GiB protected-cache ceiling and cancellation support. Partial encrypted segments remain retryable; a separate encrypted completion marker is set only after the downloader returns successfully.
- Stored the short-lived manifest reference and selected stream indexes in the Keystore-backed secret store, separate from catalogue and ordinary download queue files.
- Changed the Audible action to use the Widevine DASH route directly rather than queueing a separate generic AAXC/ADRM protected file. The UI marks a title offline only after the protected media download completes.

## Pass 12 — protected playback recovery

- Persist only the ASIN, title, playback position and play/pause state in app-private recovery preferences.
- On app startup, restore the saved Audible identity through the existing account factory, reload the encrypted offline key-set ID and DASH manifest reference from protected storage, and reopen the cache-only Media3 source.
- Preserve paused state when restoring a session; do not autoplay a title that was paused before process death.
- Clear protected recovery metadata when playback completes or a different clear-audio title is started.
- Recovery metadata intentionally excludes account IDs, signed URLs, stream indexes, DRM challenges, licence responses and key-set IDs.

## Pass 13 — offline title removal

- Add a **Remove offline copy** action to Audible title details, shown only when the catalogue marks a Widevine title as offline.
- Before cleanup, stop the matching protected player on Android's main looper so its Media3 session is no longer reading the cache or using the offline licence.
- Stop matching protected playback and remove the persistent key set with Android `MediaDrm.removeOfflineLicense(keySetId)` before deleting its encrypted identifier. This is local device cleanup, not a server-confirmed licence release.
- Remove the title-scoped DASH cache resources and protected manifest metadata, then clear the local catalogue's offline-available flag.
- Keep the offline copy separate from ordinary imported audio; this action does not touch clear local audiobooks or other Audible titles.

## Pass 14 — cancellation and cache integrity audit

- Expose a **Cancel Audible operation** control in both the Audible Library and audiobook details while an Audible licence/download operation is running.
- Carry one cancellation token through identity restoration, offline-licence acquisition, manifest lookup, encrypted DASH download, catalogue update and playback startup.
- When cancelled, ask Media3's `DashDownloader` to stop; incomplete encrypted segments remain available for a later retry and are never marked complete.
- Record the requested `DataSpec` byte ranges for each hashed cache key. The range index contains only title-scoped hashed keys and numeric offsets/lengths; signed provider URLs are not stored.
- Before setting or trusting the offline-complete marker, audit every recorded range against Media3's cache byte counts. Unknown-length resources are accepted only when Media3 has recorded a final content length and the full resource is cached.
- Reconcile stale index entries against the actual cache and recover orphan cache keys for safe later deletion. Incomplete/missing ranges invalidate the completion marker and block offline playback until retried.
- Add automated tests for bounded-range coverage, unknown-length full-resource coverage, invalid range metadata and per-title completion-marker invalidation.
## Pass 15 — startup and library-refresh offline-state audit

- Preserve `IsWidevineOfflineAvailable` when provider metadata is merged into an existing local Audible catalogue entry. A catalogue refresh must not erase the app's locally verified offline state.
- After the saved Audible account status is restored at startup, inspect provider titles currently marked offline-available.
- For each title, verify that its opaque offline key-set identifier remains in the encrypted secret store and that the protected DASH completion marker, manifest reference and title-scoped cached byte ranges are still present.
- Run the same audit after a successful or attempted manual library refresh so a stale offline label is corrected before the user opens the title.
- Clear only the local offline-available flag when the account identity is available and the audit definitively finds a missing key-set identifier or incomplete cache. The next title action can request the provider-authorized licence/media again.
- If the account identity is unavailable or an audit fails transiently, keep the existing catalogue state; an inconclusive check must not label an offline title as missing.
- Add a regression test proving library metadata sync preserves the local Widevine offline flag.

## Pass 16 — interrupted-download cache-index recovery

- Extracted cache-index reconciliation into a platform-neutral helper used by the Android cache auditor and covered by unit tests.
- Remove stale index keys that no longer exist in Media3's cache.
- Recover live title-scoped cache keys committed immediately before an interrupted index write so those resources remain discoverable for later deletion.
- Do not treat recovered orphan keys as complete: their expected byte ranges are unknown until a retry observes the matching `DataSpec` again and records trusted range requirements.
- Preserve partial encrypted segments across cancellation and retry; starting a new manifest/stream selection invalidates the prior completion marker before transfer.
- Added regression coverage for stale-key removal, orphan-key recovery and completion-marker invalidation on retry.

## Pass 17 — offline licence state and account disconnect cleanup

- Check Android's persistent offline-licence state before protected playback. A usable key set can be reused; a released or missing key set is replaced only after VØRATH obtains a fresh provider-issued Widevine grant. An unknown state fails closed and keeps the stored identifier.
- Remove an inactive old key set before storing its replacement. If the stored key-set ID is no longer present in MediaDrm, remove its local metadata and title cache before reacquiring.
- Add an encrypted per-account ASIN index alongside each saved key set. The index key contains a hash of the account identifier and the index value is stored through the encrypted secret store.
- Before Audible credentials are cleared on disconnect, enumerate the account's indexed titles, stop matching protected playback, remove each persistent Android offline key set, remove its DASH cache and encrypted metadata, then clear the catalogue's offline flags.
- If local cleanup fails, keep the Audible identity and session so the cleanup can be retried; do not strand key sets behind a deleted account.
- The current removal method is device-local. Android documents a separate KEY_TYPE_RELEASE request/response flow that lets the licence server confirm release and update its offline allocation. That provider-facing release exchange, explicit server revocation, same-key-set licence renewal and real provider/device compatibility are **not yet implemented**. Re-acquisition after an inactive key set is a fresh provider grant, not a claimed renewal of the previous key set.

## Pass 18 — Widevine offline expiry preflight

- After restoring a saved Android offline key set, query `MediaDrm.queryKeyStatus` for Widevine's documented `LicenseDurationRemaining` and `PlaybackDurationRemaining` values.
- Parse durations as non-negative seconds. A reported zero is treated as definitely expired; absent, negative or malformed fields remain unknown and do not invalidate a licence.
- Check this metadata before starting offline playback or another download attempt. If Widevine confirms expiry, stop before playback/download and retain the existing key-set record and encrypted cache.
- The Android API's offline-license state and the Widevine duration fields answer different questions: the preflight checks both rather than assuming that a key set reported as usable has unlimited time remaining.
- Do not automatically replace or delete an expired key set. Audible's documented-but-unofficial `content-license-refresh` flow requires the original ACR, file version and a renewal challenge; provider-side renewal and server-confirmed release remain separate work. A direct Android `removeOfflineLicense` call is local-only and can leave the server's offline allocation unchanged.
- Added unit tests for positive durations, zero-duration expiry, absent fields and malformed/negative values. Physical-device behavior remains unverified.

## Pass 19 — preserve provider renewal references

- Read Audible's `ContentReference.Acr` and numeric `ContentReference.FileVersion` from the actual Widevine content grant. The pinned AudibleApi DTO exposes `FileVersion` as a numeric value, so the mobile layer keeps it numeric and serializes it only when forming a provider request.
- Persist the ACR and file version as a separate per-account/per-ASIN record through the encrypted `ISecretStore`. The storage key uses the existing hashed account/title scope; these identifiers are not written to catalogue records or ordinary playback recovery preferences.
- Save the renewal reference only after the Android offline key-set ID has been stored and indexed. A download retry refreshes the reference from the new provider grant; missing grant metadata clears stale values rather than leaving an old content version attached to the title.
- Delete the renewal reference with the corresponding offline licence/cache metadata during title removal or account disconnect.
- Add tests for ACR/file-version extraction, per-account/title storage isolation, missing/incomplete metadata, and removal cleanup.
- This is renewal groundwork only. It does not yet create a Widevine renewal challenge, call `/1.0/content-license-refresh`, interpret provider renewal denials, or replace an existing key set. The endpoint is community-documented, not an official public API contract; production renewal remains disabled pending account/title-specific validation.
## Pass 20 — provider refresh and Android DRM exchange primitives

- Add a typed Audible refresh-envelope builder for the observed `POST /1.0/content-license-refresh` contract. It sends one title's ASIN, ACR, string-serialized file version, `drm_type=Widevine`, and the Base64 opaque MediaDrm challenge under `license_refresh_requests`, with `tenant_id=Audible`.
- Add an authenticated request method through AudibleApi's ADP/bearer-authenticated transport. The challenge-bearing body is not included in diagnostics; HTTP errors expose only the status code.
- Parse the per-title `license_refresh_responses` element, preserving status code, error text, opaque licence value and metadata strings verbatim. HTTP 200 is not interpreted as a successful per-title grant; duplicate/missing ASIN matches, malformed JSON and oversized responses fail closed.
- Add tests for exact request fields, Base64 challenge transport, granted and denied per-title responses, duplicate/missing title responses, invalid metadata and cancellation.
- Add Android MediaDrm primitives to create a renewal challenge by restoring the saved key set and requesting an offline key update, and to accept the returned offline response into the active session.
- Add Android MediaDrm release primitives using `KEY_TYPE_RELEASE`, the stored key-set ID as request/response scope, and the provider response. Android marks the key set RELEASED when the release challenge is generated; it is unusable until the provider response is accepted.
- These primitives are deliberately not wired to automatic renewal, playback fallback, title removal or account disconnect. The renewal response has not yet been proven to create a replacement key-set ID for Audible, and the community-observed refresh endpoint has not been proven to accept Android's release challenge. Never remove the old key-set record/cache or clear account state merely because an HTTP 200 response was received.
- Android's release flow requires a provider response to `provideKeyResponse(keySetId, response)`; direct `removeOfflineLicense` remains only the exceptional local cleanup path. See the Android API reference and the community-observed Audible refresh schema.
## Pass 29 — transient transfer retry and cache reuse

- Add two bounded automatic retries for transient HTTP transport failures and Media3 Java I/O failures during the DASH media stage, with 1-second then 2-second backoff.
- Retry only the media/manifest operation after the offline licence has been acquired. The coordinator checks the saved Android key set and Media3 cache again; it does not deliberately request a second offline licence just because a network transfer failed.
- Each retry resolves a fresh provider manifest while retaining successfully cached encrypted DASH resources. The normal cache-range audit remains the gate before an offline-complete marker can be written.
- Do not automatically retry cancellation, invalid media, unsupported manifests, local file-access failures, or permanent HTTP statuses such as 401, 403 and 404. The notification continues to expose Pause during the backoff; cancelling the delay follows the same pause/resume path as cancelling an active transfer.
- Add regression tests for transient/permanent HTTP status classification, wrapped transport exceptions, cancellation and the bounded backoff schedule.
- This is source/CI validation only. It does not establish that Audible grants Widevine for a particular title/device or that the phone can play the result offline.

## Pass 30 — version-aware DASH cache identity

- Cache resource identities now include a hash of Audible's ACR and numeric file version. The raw ACR is not written into cache keys or ordinary catalogue metadata.
- When both stable content-reference fields are present, known short-lived authorization query parameters are excluded from the resource fingerprint. The URI path and non-authorization query parameters remain part of the identity; query order is canonicalized.
- This allows a refreshed signed URL for the same Audible content version to address already-downloaded encrypted Media3 segments. A different ACR or file version produces a different cache scope, so bytes from different content versions cannot be appended or reused as though they were identical.
- If Audible omits either member of the content-reference pair, VØRATH uses an unversioned cache identity and retains the complete query string. It deliberately gives up token-rotation reuse rather than guessing which query parameters are safe to discard.
- The same opaque version fingerprint is used for transfer, completion auditing and cache-only playback. Title removal enumerates all version scopes and removes every associated cache entry.
- Unit tests cover rotating signature parameters, stable track-selection parameters, ACR/file-version changes, missing metadata and unsafe URLs.
- This is a cache-consistency improvement only. It cannot make Audible grant a Widevine licence, and device-level offline playback still requires validation with an entitled title.

## Pass 31 — cache-quota failure isolation

- A quota failure during a new DASH transfer now removes only the cache for the exact attempted Audible content-version scope.
- Previously completed cache versions for the same account/title, and all other offline titles, remain untouched.
- Explicit title removal still enumerates and removes every version scope, including legacy unversioned title scopes and cache resources written before an index update completed.
- Version-scope creation and title-prefix matching are moved into a pure helper and covered by tests for the 128-character scope limit, delimiter safety, version separation and cross-title isolation.
- The preflight quota check remains fail-closed: when the app-private cache is already at its 8 GiB ceiling, it refuses a new transfer without deleting existing content.
- This protects already cached data from a failed or oversized new version. It does not change Audible licence authorization or prove a physical-device download/playback.

## Remaining implementation — not yet verified

1. Implement and validate Audible's server-confirmed KEY_TYPE_RELEASE exchange and documented licence renewal/revocation semantics before claiming provider-side licence release.
2. Verify account cleanup and interrupted-download retry behavior with multiple entitled titles on a physical Android 10 handset.
3. Verify with an authorized Audible title on a physical Android 10 handset: Widevine grant, challenge/response compatibility, persistent key-set validity, DASH segment completion, offline playback with network disabled, cancellation/retry, app restart, removal/release and reinstall behavior.

## Android 10 / device constraints

- Android API 29 exposes offline key request and persistent key-set identifier APIs, matching the app's minimum OS version.
- Widevine scheme support and a reported security level are diagnostics only. They do not prove that the Audible account's device registration can obtain Widevine, that a title is offered in Widevine, or that offline playback works.
- AudibleApi's Android profile match is a provider registration requirement, not something VØRATH may spoof by editing identity metadata. Existing account registration must not be silently changed as an experiment.
- ARMv7/ARM64 build success is not DRM playback validation. Physical-device testing is mandatory.

## Stop conditions

Return a clear unsupported/error state if the device lacks Widevine, the identity is not registered under Audible's supported Android profile, Audible denies Widevine or returns ADRM, the challenge/response cannot be used by Android `MediaDrm`, the title is not Widevine-delivered, or the DRM-capable player cannot open it. Never fall back to key extraction or protected-stream decryption.

## References

- [AudibleApi download and Widevine licence methods](https://github.com/rmcrackan/AudibleApi/blob/95e1989e7d64264ccc9a5f7eca8b3b4a27c7616e/AudibleApi/Api.Download.cs)
- [AudibleApi Android device profile](https://github.com/rmcrackan/AudibleApi/blob/95e1989e7d64264ccc9a5f7eca8b3b4a27c7616e/AudibleApi/Resources.cs)
- [Libation's DRM download implementation](https://github.com/rmcrackan/Libation/blob/503904e6f4088d78f11e751fc7deb2d819de51f3/Source/FileLiberator/DownloadOptions.Factory.cs) — reference for API behavior only; its CDM key parsing/decryption path is not part of VØRATH.
- [Android MediaDrm reference](https://developer.android.com/reference/android/media/MediaDrm)
- [Android offline key request constant](https://developer.android.com/reference/android/media/MediaDrm#KEY_TYPE_OFFLINE)
- [Media3 ExoPlayer DRM documentation](https://developer.android.com/media/media3/exoplayer/drm)


## Pass 32 — persistent protected-download recovery

The Android foreground service records a small, atomic recovery record in app-private storage:
ASIN, display title, state, byte progress, optional content length, and update time. It does
not store signed media URLs, DASH manifest URLs, DRM licence material, or keys.

An active download is reconciled when the app next resumes. The service then obtains a fresh
provider-authorized manifest and continues through Media3's existing encrypted DASH cache;
completed encrypted segments remain reusable. An explicit Pause is persisted separately and
is not auto-resumed. The notification's Resume action starts that same title again. Completed
and failed states are not restarted automatically.

The recovery record is metadata only. It does not claim the device has received a usable
Audible licence or that offline playback is available; the service must still verify both the
offline licence and the complete cached media before reporting success. If the record is
malformed, recovery is skipped and the app remains usable.


## Pass 33 — synchronized Audible download queue

The Android foreground Widevine service and the visible application share one
`JsonDownloadQueue` instance. Audible entries are inserted into that same durable
queue before the service begins authorization. The service updates status to Downloading,
Paused, Failed, or Downloaded (protected) and stores byte progress in the queue record.
The app reflects these values in each row with a progress bar and a per-title Pause/Resume
control; the notification actions use the same service entry points.

Audible `provider:` queue entries are deliberately excluded from the ordinary HTTP
download worker and its interrupted-job recovery. This prevents a protected Audible title
from accidentally being routed through the generic downloader. Android's foreground
Widevine service remains responsible for Audible authorization, encrypted DASH segment
recovery, and offline licence verification.

Progress writes are throttled and monotonic, including when a provider later reports a smaller content length. Late callbacks received after Pause or
completion are ignored. A paused row retains its latest persisted byte count and transitions
back to Downloading when resumed. Queue presentation failures do not cancel the protected
media transfer.


## Pass 34 — notification and recovery progress reconciliation

The Widevine recovery record is the durable source of truth for the active Audible
transfer. When the foreground service resumes after process death or an explicit
Pause, it copies the recovered byte count and content length back into the queue
mirror before continuing. The foreground notification is rebuilt with the restored
percentage when a reliable content length is available, or the saved byte count
when it is not.

A later smaller content-length report cannot shrink the previously persisted
content length or byte progress. A same-title retry from Downloading, Paused or
Failed retains partial encrypted progress; a fresh download after a Completed
state begins with a clean progress record.
