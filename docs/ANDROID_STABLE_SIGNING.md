# VØRATH: one-time Android signing setup

Android only accepts an in-place app update when the new APK is signed by the same private key as the installed APK. Earlier CI builds used temporary runner-generated debug keys, so those APKs cannot be updated in place by a later build.

This repository now requires one persistent signing key before it publishes installable APKs. The private keystore and passwords must never be committed to Git.

## 1. Create the key on a device you control

On your Redmi 9i, you can use **Termux** instead of a computer. Termux supports Android 7 and later, and its current package repository provides OpenJDK 21 (which includes `keytool`). On Android 10 / ARMv7, check that Termux is up to date and that `termux-info` reports the `arm` package architecture. Install Termux and its packages from one source only; do not mix the F-Droid, GitHub and Google Play builds.

Run these commands in Termux:

```sh
pkg update
pkg install openjdk-21
java -version
keytool -help
mkdir -p "$HOME/vorath-signing"
umask 077
```

Then generate the permanent key in Termux's private app storage:

```sh
keytool -genkeypair -v \
  -keystore "$HOME/vorath-signing/vorath-release.jks" \
  -storetype JKS \
  -alias vorath \
  -keyalg RSA \
  -keysize 3072 \
  -validity 10000
```

When prompted, create a strong password. Press Return at the key-password prompt to reuse the keystore password, or set a separate strong password. Verify the alias and certificate:

```sh
keytool -list -v \
  -storetype JKS \
  -keystore "$HOME/vorath-signing/vorath-release.jks" \
  -alias vorath
```

The first `pkg install` may download a large JDK package. If Termux says `openjdk-21` is unavailable, check the Termux source and architecture before trying random APKs or repositories. Official references: [Termux installation](https://github.com/termux/termux-app#installation), [Termux OpenJDK 21 package](https://github.com/termux/termux-packages/tree/master/packages/openjdk-21).

Use a trusted computer or a local Android terminal with Java's `keytool` installed. Run:

```sh
keytool -genkeypair -v \
  -keystore vorath-release.jks \
  -storetype JKS \
  -alias vorath \
  -keyalg RSA \
  -keysize 3072 \
  -validity 10000
```

Choose a strong keystore password and key password. For simpler setup, you may use the same strong password for both. Keep the alias exactly `vorath`.

**Back up `vorath-release.jks` securely.** This is the app's permanent signing identity. Losing it means future updates under the same Android package ID may become impossible. Do not send the keystore or passwords in chat, email, or an issue, and do not upload the keystore to this repository.

## 2. Encode the keystore for GitHub Actions

On Termux, run:

```sh
base64 "$HOME/vorath-signing/vorath-release.jks" | tr -d '\n'
```

On a computer where step 1 created the file in the current directory, run:

```sh
base64 vorath-release.jks | tr -d '\n'
```

Copy the full output. Keep the .jks file inside Termux's private storage and make a separate secure backup. Do not put an unencrypted copy in Downloads or ordinary shared storage. Treat the Base64 output as a private key: anyone who gets it together with the password can sign VØRATH APKs.

## 3. Add four repository Actions secrets

Open the repository on GitHub, then go to **Settings → Secrets and variables → Actions → New repository secret**. Add these exact names:

| Secret name | Value |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | Full one-line Base64 output from step 2 |
| `ANDROID_KEYSTORE_PASSWORD` | Keystore password |
| `ANDROID_KEY_ALIAS` | `vorath` |
| `ANDROID_KEY_PASSWORD` | Private-key password |

Do not add these as ordinary repository variables; they must be Actions **secrets**.

## 4. Build a persistent-signed APK

This workflow listens for pushes to `fix/pass-5-current-librisync-iphone-profile`. On a feature-branch push it always runs core tests and both ABI compile checks. The release job also starts, but it checks for the four signing secrets first and safely skips APK publishing when they are not configured. This keeps ordinary PR/feature-branch checks green without creating installable APKs signed by temporary keys.

After all four secrets are saved, a new push to that feature branch triggers stable APK publishing automatically. The release workflow validates the keystore, builds ARMv7 and ARM64 APKs with the same key, checks their signing-certificate fingerprints, and assigns an increasing Android version code. Download the artifact named `VORATH-StableSigned-ABI-specific`.

Manual `workflow_dispatch` is an alternative once this workflow exists on the repository's default branch; GitHub does not offer manual dispatch for a workflow file that exists only on a non-default branch.

For the Redmi 9i, use the **ARMv7 / armeabi-v7a** APK. Keep the keystore backup safe for the lifetime of this app.

## Important migration note

The old CI signing private keys were generated on temporary runners and are no longer available. Therefore, the first APK signed with this new persistent key cannot normally update an installation signed by one of those old keys. Do not uninstall the currently installed app yet. First preserve any local data/downloads you need and make sure the stable-signed APK is ready. One final reinstall may then be necessary; future APKs signed with this same key should install as updates.
