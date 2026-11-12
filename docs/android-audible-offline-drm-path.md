# Android Audible offline download and playback path

## Decision

VØRATH will only play Audible-protected downloads through a provider-authorized Android DRM session. It will not extract content keys, decrypt protected streams outside the platform DRM stack, fabricate licences, or bypass DRM.

The intended Android path is Audible-authorized Widevine content, an offline licence held by Android `MediaDrm`, and playback through a DRM-capable Android media player. A downloaded encrypted file or a successful HTTP response is not a playable audiobook.

## Upstream API behavior verified

The pinned AudibleApi source exposes two distinct operations:

1. `Api.GetDownloadLicenseAsync(asin, quality, chapterTitlesType, DrmType.Widevine)` requests the content licence and media URL.
2. `Api.WidevineDrmLicense(asin, licenseChallenge)` posts the platform-generated Widevine challenge to Audible's authenticated `/drmlicense` endpoint and returns the provider's licence response string.

Important registration behavior: AudibleApi changes a requested Widevine content licence to ADRM when the saved identity's `DeviceType` does not match its Android Audible profile. Therefore, VØRATH must inspect the actual returned `DrmType`; it must never assume that asking for Widevine means Widevine was granted.

Security review note: the inspected upstream `AudibleApi.WidevineDrmLicense` implementation logs its `ApiErrorException` on failure, and that exception can retain the JSON request body containing `licenseChallenge`. The app must not enable this production call until the logging path is proven to redact the challenge or a safe upstream/API transport path is used. Do not assume that suppressing VØRATH's own exception logging also suppresses the dependency's Serilog call.\n\nThe upstream Libation desktop implementation uses a separate CDM library to parse a licence into content keys. That key-extraction/decryption path is not being ported. VØRATH's intended route is for Android `MediaDrm` to create the challenge and consume the licence response through its own `ProvideKeyResponse` lifecycle.

## Pass 8 implementation

- Added `AudibleAudioDownloadSource.ResolveWidevineAsync`, which explicitly requests Widevine, rejects a response that AudibleApi downgraded to ADRM, and returns the provider's HTTPS DASH manifest URL as a manifest reference (not as an ordinary audio-file download target).
- Preserved the existing ADRM download request path separately, so the new Widevine experiment does not silently change existing queue behavior.
- Added `RequestWidevineLicenseAsync`, which forwards the exact platform-generated challenge to AudibleApi's authenticated `WidevineDrmLicense` operation. The challenge/response encoding contract still needs verification on Android `MediaDrm`; the upstream API's error logging behavior also needs a safe redaction solution before production use.
- Added validation for the 10-character ASIN, non-empty challenge, cancellation before the provider request, and non-empty provider response.
- Added unit tests for challenge forwarding and safe validation behavior.

## Remaining implementation — not yet complete

1. Implement an Android `MediaDrm` session adapter. It must call `GetKeyRequest` with `KEY_TYPE_OFFLINE`, pass the exact challenge to `RequestWidevineLicenseAsync`, and pass the returned licence bytes to `ProvideKeyResponse`. The exact encoding contract must be verified against Audible's response and Android's API; do not guess or transform the challenge/response.
2. Store only the resulting offline key-set identifier in app-private, encrypted storage backed by Android Keystore. Do not store the challenge, licence response, content keys, or DRM session secrets in queue JSON, backups, logs, or analytics.
3. Implement a DRM-aware Media3 playback path that opens the protected media using the offline licence held by `MediaDrm`. Do not route protected files to the existing unprotected local-file player.
4. Add licence renewal, expiry, release/revocation, title deletion and account disconnect handling. Ensure deletion releases provider/device licence state where supported.
5. Verify on a physical Android 10 device with an authorized title: provider grant, downloaded media, online playback, app restart, offline playback, expired/revoked licence handling, and uninstall/reinstall behavior.

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
