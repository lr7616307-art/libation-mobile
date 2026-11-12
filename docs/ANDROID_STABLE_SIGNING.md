# VØRATH: one-time Android signing setup

Android only accepts an in-place app update when the new APK is signed by the same private key as the installed APK. Earlier CI builds used temporary runner-generated debug keys, so those APKs cannot be updated in place by a later build.

This repository now requires one persistent signing key before it publishes installable APKs. The private keystore and passwords must never be committed to Git.

## 1. Create the key on a device you control

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

In the same local terminal, run:

```sh
base64 vorath-release.jks | tr -d '\n'
```

Copy the full output. Treat it as a private key: anyone who gets it together with the password can sign VØRATH APKs.

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

After all four secrets are saved, open **Actions → Android APK → Run workflow** and select branch `fix/pass-5-current-librisync-iphone-profile`.

The workflow validates the keystore, builds ARMv7 and ARM64 APKs with the same key, checks their signing-certificate fingerprints, and assigns an increasing Android version code. Download the artifact named `VORATH-StableSigned-ABI-specific`.

For the Redmi 9i, use the **ARMv7 / armeabi-v7a** APK. Keep the keystore backup safe for the lifetime of this app.

## Important migration note

The old CI signing private keys were generated on temporary runners and are no longer available. Therefore, the first APK signed with this new persistent key cannot normally update an installation signed by one of those old keys. Do not uninstall the currently installed app yet. First preserve any local data/downloads you need and make sure the stable-signed APK is ready. One final reinstall may then be necessary; future APKs signed with this same key should install as updates.
