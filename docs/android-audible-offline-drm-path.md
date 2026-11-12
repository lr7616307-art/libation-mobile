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
- Added cleanup for a just-issued offline key set when secure persistence fails, plus a device-side offline-key release operation before deleting the encrypted identifier.
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
- Restore the device-side MediaDrm key set and request its removal before deleting the encrypted key-set identifier.
- Remove the title-scoped DASH cache resources and protected manifest metadata, then clear the local catalogue's offline-available flag.
- Keep the offline copy separate from ordinary imported audio; this action does not touch clear local audiobooks or other Audible titles.

## Pass 14 — cancellation and cache integrity audit

- Expose a **Cancel Audible operation** control in both the Audible Library and audiobook details while an Audible licence/download operation is running.
- Carry one cancellation token through identity restoration, offline-licence acquisition, manifest lookup, encrypted DASH download, catalogue update and playback startup.
- When cancelled, ask Media3's `DashDownloader` to stop; incomplete encrypted segments remain available for a later retry and are never marked complete.
- Record the requested `DataSpec` byte ranges for each hashed cache key. The range index contains only title-scoped hashed keys and numeric offsets/lengths; signed provider URLs are not stored.
- Before setting or trusting the offline-complete marker, audit every recorded range against Media3's cache byte counts. Unknown-length resources are accepted only when Media3 has recorded a final content length and the full resource is cached.
- Reconcile stale index entries against the actual cache and recover orphan cache keys for safe later deletion. Incomplete/missing ranges invalidate the completion marker and block offline playback until retried.
## Remaining implementation — not yet verified

1. Add an explicit user-facing cancel action and progress state for an in-flight Audible licence/download operation. The underlying downloader already observes cancellation, but the current detail-page flow does not yet expose a cancel control.
2. Add a startup cache-integrity audit that validates the selected DASH representation's initialization/index/media resources and reconciles stale index entries after interrupted downloads.
3. Add licence expiry/renewal and provider release/revocation handling, plus cleanup when an Audible account is disconnected or loses access.
4. Verify with an authorized Audible title on a physical Android 10 handset: Widevine grant, challenge/response compatibility, persistent key-set validity, DASH segment completion, offline playback with network disabled, app restart, removal/release and reinstall behavior.

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
