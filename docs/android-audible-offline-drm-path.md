# Android Audible offline download and playback path

## Decision

Do not treat an Audible download-license response or a downloaded encrypted media payload as a completed audiobook. On Android, the only candidate path to investigate is Audible-authorized Widevine content with an offline license managed by Android `MediaDrm`, followed by playback through a DRM-capable Android media player.

No DRM extraction, key export, license fabrication, or bypass is part of this design.

## Evidence

- The pinned AudibleApi adapter exposes a content download-license request and a Widevine DRM-license request. The content-license response and the DRM-license exchange are distinct operations; neither should be represented as a playable file by itself.
- Android `MediaDrm` supports offline key requests and persistent offline license identifiers. The offline-license APIs are available from API 29, matching this app's Android 10 minimum. See [Android MediaDrm reference](https://developer.android.com/reference/android/media/MediaDrm) and [offline key request constants](https://developer.android.com/reference/android/media/MediaDrm#KEY_TYPE_OFFLINE).
- Libation's documentation distinguishes its AAX(C) path from its Widevine path and states that Widevine content requires an Android device registration. See [Audio formats](https://getlibation.com/docs/features/audio-file-formats) and [device registration](https://getlibation.com/docs/advanced/device-registration).
- The upstream Libation desktop workflow downloads protected content and then performs a separate decryption stage. That desktop decryption workflow is not a suitable Android playback implementation and is not being ported as a DRM-bypass mechanism. See [Getting started](https://getlibation.com/docs/getting-started).

## Required Android flow (not implemented yet)

1. Restore the signed-in account through `AudibleApiSessionFactory`; do not persist the API client or identity material in a queue record.
2. Request the provider's download license for an owned/otherwise authorized title and verify the provider explicitly grants it.
3. Confirm the returned content reference identifies a Widevine-supported delivery path. Reject unsupported DRM/content types instead of silently falling back to an undocumented path.
4. Create a Widevine `MediaDrm` session on the actual device, generate the provider-required license challenge, and exchange it only through the documented AudibleApi adapter method.
5. Use the Android `MediaDrm` offline key request/response lifecycle only if Audible's response is compatible with that documented Android API. Keep the resulting key-set identifier in protected app-private storage; never log it or put it in backups or queue JSON.
6. Download the provider-authorized encrypted media to a temporary file, validate length/container metadata, then atomically publish it only after the DRM and media stages succeed.
7. Play through an Android DRM-capable player with the correct Widevine session and verify playback while offline. Do not label the item complete until this succeeds.

## Android 10 / device constraints

- API 29 exposes offline-license state and key-set enumeration APIs. Device support still has to be queried at runtime.
- The existing Widevine probe is diagnostic only. A positive result or a reported security level does not prove that Audible grants a license, that the title is delivered as Widevine, or that the app can play it offline.
- ARMv7/ARM64 build success does not establish Widevine security level or playback capability.
- Physical-device testing on the user's Android 10 handset is required. Test an authorized title, online license acquisition, playback, app restart, and offline playback.

## Stop conditions

Return an actionable unsupported/error state if the device lacks Widevine support, Audible denies the license, the title requires an unsupported DRM mode, the license exchange is incompatible, or the media player cannot open the protected content. Never fall back to key extraction, decryption of protected streams, or any attempt to defeat access controls.

## Current implementation status

- Authenticated API session factory: merged in PR #152.
- Android Widevine capability probe: merged in PR #153 and surfaced as a user-triggered diagnostic in PR #155. It reports platform scheme support and an optional security-level property only; it has not been validated on the user's handset.
- Download worker, SAF destination handling, persistent-license lifecycle, DRM playback integration, and offline playback verification: not implemented.
