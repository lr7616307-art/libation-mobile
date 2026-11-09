# Installing and smoke-testing Libation Mobile on Android

This guide is designed for installation and testing directly from an Android phone. A computer is not required.

## 1. Download a successful APK

1. Open the repository's [GitHub Actions page](https://github.com/lr7616307-art/libation-mobile/actions).
2. Open the latest **successful** Android APK workflow run. Confirm its conclusion is **Success**.
3. Download the artifact matching the device:
   - **android-arm / armeabi-v7a**: 32-bit ARM.
   - **android-arm64 / arm64-v8a**: 64-bit ARM.
4. Extract the artifact ZIP with the Android Files app or another ZIP extractor.
5. Install the APK inside it.

If the workflow failed or has no APK artifacts, do not install an APK from that run. A successful CI build confirms compilation and packaging, not real-device compatibility.

## 2. Install and launch

1. Tap the APK and allow your file manager or browser to install unknown apps if Android asks.
2. Install and launch **VØRATH** (the Android app title may still appear as Libation Mobile in launcher metadata).
3. If Android prompts for notification permission, allow it so foreground playback controls can be displayed.
4. If Android prompts for media/audio or notification access, review the permission and grant only what the app actually requests.

After installation, turn off the install-unknown-apps permission if you do not routinely need it.

## 3. Catalogue and account checks

- [ ] App opens without immediately closing.
- [ ] Catalogue loads and search, sorting, and filters respond.
- [ ] Add a local catalogue entry and confirm it remains after reopening.
- [ ] Open the Account tab and complete Audible sign-in using the supported browser flow.
- [ ] Confirm the app reports sign-in success without exposing credentials or tokens.
- [ ] Run library sync and confirm catalogue entries appear.
- [ ] Sign out or clear app data only when intentionally testing account reset; doing so may require signing in again.

## 4. Download checks

Use titles available in the signed-in account. Do not test with content you are not authorized to access.

- [ ] Enqueue one audiobook and confirm the queue shows its changing status.
- [ ] Pause and resume the queue.
- [ ] Close and reopen the app; confirm persisted queue items are recovered.
- [ ] Confirm a completed download appears in the local catalogue with a usable audio file.
- [ ] Disable network access and play the downloaded file to verify offline playback.
- [ ] If a transfer is interrupted, reconnect and use the queue's retry behavior.
- [ ] Check that a failed or incomplete transfer is not presented as a completed audiobook.

Protected media can be processed only when a compatible, authorized post-download processor is available. The app must not bypass DRM, extract protected plaintext through unsupported means, or imply that every protected title is currently playable.

## 5. Playback and recovery checks

Use a downloaded or manually imported audio file.

- [ ] Start playback and confirm the Android foreground notification appears.
- [ ] Pause, resume, seek, rewind, and fast-forward.
- [ ] Change playback speed and confirm it survives a service/app recreation.
- [ ] Background the app; confirm audio continues while the foreground service owns playback.
- [ ] Pause at a known position, force-stop the app from Android Settings, then reopen it. Confirm the same audiobook and saved position are restored in the paused state.
- [ ] Start playback, force-stop the app, and reopen it. Confirm the last saved session is restored with its previous play intent.
- [ ] Repeat after a device restart if practical.
- [ ] Verify the sleep timer pauses playback and that its state is restored when the app is reopened.

Recovery checkpoints are written periodically and immediately after important playback actions. A sudden power loss between periodic checkpoints can still lose the most recent few seconds; this is not a guarantee of sample-perfect recovery.

## 6. Backup checks

- [ ] Export a catalogue backup through the Android file picker.
- [ ] Import the backup and verify the preview before confirming replacement.
- [ ] Keep backup files private: catalogue metadata can reveal listening interests.

Backups are not a substitute for downloaded audio files and do not include account passwords, access tokens, cookies, or DRM licences.

## 7. Known boundaries

- CI builds for ARMv7 and ARM64 do not replace testing on physical devices and Android versions.
- Audible sign-in and download behavior depend on account eligibility, network availability, service responses, and the current supported authentication flow.
- DRM-protected playback is conditional on a compatible, authorized processor and applicable licence. No DRM circumvention is implemented or promised.
- Force-stop, battery restrictions, vendor background limits, and Android service policy can affect when playback can restart. Test on the target phone.
- This is an evolving community Android client, not an official Audible application.

## 8. Reporting a problem safely

Include:
- Phone model and Android version.
- APK architecture (ARMv7 or ARM64) and CI run number.
- The screen/action immediately before the issue.
- A screenshot or redacted log excerpt, after checking it for personal information.

Never share passwords, access/refresh tokens, cookies, browser callback URLs, identity files, private keys, DRM licences, or unredacted account data in an issue or screenshot.
