# Android APK smoke check (phone-only)

This guide is for installing a CI-built prototype APK directly on an Android phone. No desktop computer is required.

## Before installing

- Use an APK from a successful **Android APK** workflow run in this repository.
- Choose the device ABI correctly:
  - **ARMv7**: artifact `libation-mobile-ARMv7`, runtime `android-arm`. This is the correct starting choice for a 32-bit ARM phone.
  - **ARM64**: artifact `libation-mobile-ARM64`, runtime `android-arm64`. Use this only when the phone supports 64-bit ARM apps.
- The project targets Android 10 (API 29) as its minimum OS, but installation and runtime behavior still need verification on physical devices.
- The app is an early prototype. It does **not** currently sign in to Audible, sync a real Audible library, download audiobooks, or run the upstream Libation engine.

## Download the APK on Android

1. Open the repository's [Actions page](https://github.com/lr7616307-art/libation-mobile/actions) in Chrome or another browser and sign in to GitHub.
2. Open the latest successful **Android APK** run. Confirm the run is for the commit or pull request you intend to test.
3. Scroll to **Artifacts**. Download **libation-mobile-ARMv7** for a 32-bit ARM phone, or **libation-mobile-ARM64** for a 64-bit ARM phone.
4. The download is a ZIP archive. Open it from the phone's Downloads folder and extract it using the phone's file manager or archive app.
5. Locate the APK inside the extracted folder. Do not try to install the ZIP itself.

If GitHub's mobile page hides the Artifacts section, use the browser menu and choose **Desktop site**, then reopen the workflow run.

## Install on Android 10

1. Tap the extracted APK.
2. If Android blocks the installation, open the prompt's settings link and allow **Install unknown apps** for the app you used to open the APK (for example, Files or Chrome).
3. Return to the APK and install it.
4. After installation, turn off the **Allow from this source** permission again for that app.
5. Launch **Libation Mobile** from the app drawer.

Only install APKs obtained from this repository's expected GitHub Actions run. Do not share APKs or project files that contain account data.

## Smoke-check checklist

Record each result as Pass, Fail, or Not available. Include the phone model, Android version, selected artifact (ARMv7/ARM64), workflow run link, and app version when reporting a problem.

- [ ] APK installs without an Android package/ABI error.
- [ ] App launches and remains open for at least one minute.
- [ ] Demo catalogue appears on first launch.
- [ ] Force-close and relaunch the app; confirm the saved demo catalogue remains.
- [ ] If catalogue search is exposed in this build, search for a visible demo title and confirm the result is sensible.
- [ ] If queue controls are exposed in this build, add and remove a demo item and confirm the UI updates.
- [ ] Backup/restore: mark **Not available** unless the build exposes an implemented backup and restore flow. Do not treat copying app-private files as a supported backup.
- [ ] Confirm the prototype does not request Audible credentials or claim that a real Audible library is connected.

## Reporting a crash or failure safely

When reporting a problem, include:
- Android phone model and Android version.
- Whether the phone is 32-bit ARM (ARMv7) or 64-bit ARM (ARM64), if known.
- Artifact name and workflow run URL.
- Exact step that failed and the visible error text.
- A screenshot with personal notifications and account details hidden, if useful.

**Never attach or paste** Audible passwords, access/refresh tokens, cookies, ADP/identity files, private keys, or unredacted authentication logs. A crash report should contain only non-sensitive diagnostics.

## Current limitations

A successful CI build proves that the project compiled for the selected Android runtime; it does not prove that the APK installs or runs correctly on every phone. Real-device testing is still required. Audible sign-in, library synchronization, audiobook downloads, and backup/restore are not yet implemented as complete user features.
