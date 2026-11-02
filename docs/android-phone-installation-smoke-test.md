# Installing and smoke-testing Libation Mobile on Android

This guide is written for installation directly from an Android phone. A computer is not required.

## 1. Download the correct CI artifact

1. Open the repository's [GitHub Actions page](https://github.com/lr7616307-art/libation-mobile/actions).
2. Open the latest successful **Android APK** workflow run. Confirm its conclusion is **Success** before downloading anything.
3. Scroll to **Artifacts** and choose the APK for your device:
   - **android-arm / armeabi-v7a**: 32-bit ARM devices. This is the appropriate first choice for older 32-bit Android phones.
   - **android-arm64 / arm64-v8a**: 64-bit ARM devices.
4. Download the artifact ZIP in your phone browser and open it with the Android Files app or another ZIP extractor.
5. Locate the APK inside the extracted folder.

If the workflow has no APK artifacts, or the run failed, do not use an APK from that run.

## 2. Install

1. Tap the extracted APK.
2. If Android blocks the install, open the system prompt for **Install unknown apps** and allow your browser or file manager to install this file.
3. Return to the APK and tap **Install**.
4. After installation, turn the unknown-app permission back off if you do not routinely use it.
5. Launch **Libation Mobile**.

Android 10 is within the project's declared minimum Android API target, but a successful CI build is not proof that installation and runtime behavior work on every device.

## 3. Smoke-test the prototype

- [ ] App opens without immediately closing.
- [ ] The prototype-mode banner is visible.
- [ ] Demo catalogue entries appear.
- [ ] Search finds a title, author, or narrator.
- [ ] Sort and catalogue filters respond.
- [ ] A manually added entry remains after closing and reopening the app.
- [ ] The local queue accepts an entry and displays its status.
- [ ] Export backup creates a JSON file using Android's file picker.
- [ ] Import backup previews the entry count and requires confirmation before replacement.

## 4. Current limitations

This build is a local-catalogue prototype. Audible sign-in, live library sync, and actual audio downloads are not connected. Queue entries are metadata/status records only. Do not interpret a queued item as a downloaded audiobook.

Catalogue backups contain metadata only. They do not contain credentials, identity tokens, cookies, audio, or queue items. Keep backup files private because they may reveal audiobook titles and listening interests.

## 5. Reporting a problem safely

When reporting an issue, include:
- Phone model and Android version.
- Whether you installed the ARMv7 or ARM64 artifact.
- The CI run number and artifact name.
- The screen/action immediately before the problem.
- A screenshot of the error, after checking it for personal information.

Never share passwords, access/refresh tokens, cookies, browser callback URLs, identity files, private keys, or unredacted account data in an issue or screenshot.
