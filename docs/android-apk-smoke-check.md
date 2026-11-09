# Android APK smoke check (phone-only)

Use this guide to install and verify a CI-built VØRATH APK on an Android phone. A desktop computer is not required.

## Choose the correct APK

- **ARMv7 / `android-arm`**: use this for a 32-bit ARM phone.
- **ARM64 / `android-arm64`**: use this only when the phone supports 64-bit ARM apps.
- The Android project targets API 29 (Android 10) or later. CI builds both ABIs, but compilation is not proof of installation or runtime compatibility on a particular phone.

## Download from GitHub Actions

1. Open the repository [Actions page](https://github.com/lr7616307-art/libation-mobile/actions) in your phone browser and sign in to GitHub.
2. Open a successful **Android APK** workflow run for the commit you intend to test.
3. In **Artifacts**, download `libation-mobile-ARMv7` or `libation-mobile-ARM64` as appropriate.
4. Open the ZIP in the phone's Downloads folder and extract it.
5. Install the APK inside the extracted folder; do not try to install the ZIP.

If the mobile GitHub page hides Artifacts, use the browser menu's **Desktop site** option.

## Install

1. Tap the extracted APK.
2. If Android blocks installation, follow the prompt to allow **Install unknown apps** for the app opening the APK.
3. Install and launch **VØRATH**.
4. Turn off **Allow from this source** again after installation.

Only install APKs from the expected repository workflow run.

## Smoke-check checklist

Record each item as **Pass**, **Fail**, or **Not available**.

- [ ] APK installs without package or ABI errors.
- [ ] VØRATH launches and remains open for at least one minute.
- [ ] The catalogue screen loads without a crash.
- [ ] Search and catalogue filtering behave sensibly.
- [ ] Force-close and relaunch; confirm the local catalogue remains available.
- [ ] Open Audible sign-in and verify that it launches the external browser. Do not enter credentials into an unofficial page; verify the domain before signing in.
- [ ] If already signed in, attempt a library sync and record whether it succeeds. Do not share account identifiers or authentication data.
- [ ] For a synced Audible catalogue title, confirm its queue action is disabled and reads **Audible download unavailable**. Attempting to enqueue it through the UI must not add a job or imply that media was downloaded.
- [ ] Confirm generic provider-neutral queue entries still expose **Add to queue** when such entries are available; do not mistake this for Audible media support.
- [ ] Import a user-owned DRM-free audio file, play it, pause/resume, seek, change speed, and confirm playback position is restored after relaunch.
- [ ] Test notification controls and background playback while switching away from the app.
- [ ] Mark backup/restore **Not available** unless the current build exposes a completed, verified user-facing flow.

## Current feature boundaries

The app contains Audible account sign-in and library-catalogue integration, local imported-audio playback, and a provider-neutral HTTPS download queue. These are separate capabilities.

Provider-backed Audible catalogue entries are deliberately blocked from the generic download queue until a verified, authorized Audible media-source contract is available. The app directs users to the official Audible app for protected titles. A successful catalogue sync does **not** mean audiobook media can be downloaded.

The Audible-specific authorized media resolver and protected-media playback/licensing path are not implemented. Do not attempt to bypass DRM or use unofficial download endpoints. Local imported DRM-free audio is the supported playback test path.

CI success means the project compiled for the selected ABI. It does not prove real-device behavior, Audible sign-in acceptance, Android Keystore persistence, background playback reliability, or download functionality. Those require physical-device testing.

## Report a problem safely

Include:
- Phone model and Android version.
- Artifact name (ARMv7 or ARM64), app version, and workflow run URL.
- Exact step that failed and visible error text.
- A screenshot only after hiding personal notifications and account details.

**Never share** Audible passwords, access/refresh tokens, cookies, ADP tokens, identity files, private keys, or unredacted authentication logs. Do not attach app-private storage or backups.
