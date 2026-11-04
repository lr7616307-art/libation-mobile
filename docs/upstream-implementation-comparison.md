# Upstream implementation comparison audit

Last reviewed: 2026-11-04

This is a source-level comparison, not a claim of physical-device validation or Audible certification.

## Reference projects

- Android identity samples: https://github.com/android/identity-samples
- Android security samples: https://github.com/android/security-samples
- Libation: https://github.com/rmcrackan/Libation
- AudibleApi: https://github.com/rmcrackan/AudibleApi (the library consumed by Libation)

## Findings

### Android identity and browser sign-in

- Audible authentication is not Google sign-in. Google Credential Manager is therefore not a replacement for the Audible provider's external-browser/device-registration flow.
- The current app follows the browser handoff pattern: launch an HTTPS authorization URL, receive the final HTTPS response URL from the user, and pass it to the AudibleApi adapter. It does not collect the user's Amazon password.
- The handoff should remain isolated from Android saved state, logs, analytics and intents. Physical-device login remains unverified.
- Google Identity samples remain useful for general session UX and credential lifecycle patterns, but adopting Credential Manager for Audible credentials would be incorrect.

### Android secure storage

- Current implementation uses Android Keystore AES-256-GCM and stores only ciphertext in app-private SharedPreferences.
- The Android manifest sets `allowBackup="false"` and `usesCleartextTraffic="false"`, which avoids restoring ciphertext without its non-exportable Keystore key and blocks cleartext app traffic by default.
- Review finding: `AndroidSecretStore` currently limits each secret to 16 KiB. Audible identity snapshots include tokens, device data and cookies; the code does not yet demonstrate that every valid identity remains below this limit. Add a boundary/large-identity test and either size the limit from observed upstream payloads or use a bounded encrypted-file design.
- Review finding: the generic secret store creates a new Keystore key when the alias is missing. Existing ciphertext then cannot be decrypted. This is fail-closed, but callers need a clear re-authentication path and must not silently treat all storage failures as a signed-out account.

### Audible API and Libation comparison

- The mobile adapter uses the separate AudibleApi package for authentication, device registration, token refresh and paginated library metadata. It deliberately does not import Libation's desktop UI/database/service graph.
- The current library adapter requests contributor metadata and maps authors/narrators only from their corresponding upstream fields; publisher is not used as a substitute author.
- The upstream audit notes that Libation adds desktop-side retry, response-group, series/episode resolution, validation and sanitization. These are not yet equivalent in the mobile app and should be added selectively with tests where they matter.
- The app has no verified authorized Audible media URL resolver. The downloader accepts only an authorized HTTPS URL supplied by a provider adapter. Do not infer a media endpoint from catalogue metadata or invent one.
- The separate AudibleApi dependency is AGPL-3.0; distribution notices and corresponding-source obligations require review before release. Libation itself is GPL-3.0.

### Download and playback gap

- The app currently has a provider-neutral HTTPS streaming downloader and persistent queue worker foundation.
- Still missing: verified Audible media entitlement/URL resolution, Android background execution and notification UX, UI queue controls, and audiobook playback.
- No DRM circumvention or protected-media decryption is part of the planned implementation. If Audible does not expose an authorized media path suitable for this app, the product must use an official Audible handoff or limit its local player to DRM-free/imported audio.

## Priority actions

1. Add tests around the maximum encrypted identity payload and Keystore key-loss/re-authentication behaviour.
2. Audit the account state UI so a corrupted/unavailable encrypted identity is distinguishable from a clean signed-out state.
3. Continue comparing library pagination, response groups, retries and metadata mapping with the current upstream Libation/AudibleApi sources.
4. Determine a lawful, provider-authorized audio acquisition path before connecting any download button.
5. Implement local DRM-free audio import and playback as a separately testable user workflow if no supported Audible media handoff exists.
6. Keep Android 10/API 29 and `android-arm` builds as required compatibility targets; CI compilation is not a substitute for a real ARMv7 device test.

## Validation status

GitHub Actions runs #272, #273 and #274 passed on 2026-11-04, including the configured ARMv7 and ARM64 build jobs. Run #271 failed due to stale mapper test expectations and was followed by passing runs. A passing build does not validate Audible account login, library sync, download entitlement or playback on a physical device.
