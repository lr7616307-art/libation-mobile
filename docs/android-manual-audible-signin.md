# Android Audible sign-in: LibriSync-compatible PKCE flow

## Status

The Pass 5 branch implements a dedicated Audible PKCE sign-in flow using an in-app Android WebView, a marketplace-bound callback, Audible device registration, and the app's Android Keystore-backed encrypted identity snapshot. This is an implementation status, **not proof of a successful live account login on a physical handset**. Amazon MFA/CAPTCHA, real-account registration, encrypted restore after process restart, and library sync still require device/account-owner validation.

## Why there are two reference implementations

### Libation desktop

The pinned AudibleApi 14.1.2 integration exposes:

- `ExternalLogin.GetLoginUrl()` to generate the provider URL.
- `ExternalLogin.GetSignInCookies()` for the initial sign-in page.
- `ExternalLogin.Login(responseUrl)` to parse the final return URL into an identity.

The inspected Libation desktop CLI implementation does not inject the returned cookie collection into its browser. Instead, it opens the login URL, asks the user to complete sign-in in a browser, and asks the user to paste the final address-bar URL. AudibleApi 14.1.2 does not expose a verified PKCE verifier/challenge injection point for that `ExternalLogin` flow.

### LibriSync

LibriSync uses a separate PKCE implementation: it generates a verifier/challenge, opens the provider sign-in URL in an in-app WebView, captures the provider callback, and exchanges the authorization code plus verifier at Amazon's registration endpoint.

LibriSync's current documented registration profile (v0.0.34 source inspected 2026-11-11) is its updated iPhone profile. The profile uses device type `A2CZJZGLK2JJVM`, a 40-character alphanumeric serial, iOS app/OS/software values `4.60`, `26.2`, and `46000856`, and the iPhone login surface. It does not send the Android-specific `device_metadata` or `user_context_map.frc` fields in this profile. LibriSync's own device-registration notes say the old iPhone profile had to be updated in October 2026.

VØRATH's PKCE implementation follows that current LibriSync profile. It is a provider registration choice; it intentionally does not claim that the physical Android handset is an iPhone. This differs from the separate Android registration profile and must not be mixed with its 20-hex-character serial, Android callback, Android page ID, or Android registration payload.

## Implemented mobile flow

1. The user selects an Audible marketplace (India is the default).
2. The app creates a short-lived, one-time challenge containing a random PKCE verifier, its S256 challenge, and a 40-character alphanumeric device serial.
3. Android opens the generated HTTPS authorization URL in the app's dedicated Audible WebView.
4. The WebView watches for the exact marketplace callback host and `/ap/maplanding` path. It accepts the Audible OpenID authorization-code parameter or the standard `code` parameter and also detects provider-side OAuth errors.
5. The app passes the callback URL to the authenticator. The authenticator independently validates HTTPS, exact host, path, default port, challenge expiry, and one-time use before extracting the authorization code.
6. The registration client posts the authorization code and PKCE verifier using the current LibriSync iPhone profile. The request body is never logged.
7. The complete access token, refresh token, device private key, ADP token, cookies, and account/device metadata are validated and saved as one encrypted identity snapshot.
8. The UI reports the account as connected only after encrypted persistence succeeds.

The challenge and verifier are kept in memory. Do not log the callback URL, authorization code, PKCE verifier, tokens, cookies, private key, or ADP token. Do not persist a partial login draft or accept arbitrary callback URLs from Android intents.

## Profile details that must stay together

- Device type: `A2CZJZGLK2JJVM`
- Serial: 40 random alphanumeric characters
- Callback: `https://www.amazon.{marketplace}/ap/maplanding`
- Login association handle: `amzn_audible_ios_{countryCode}`
- Login page ID: `amzn_audible_ios`
- Login layout parameter: `forceMobileLayout=true`
- Registration domain: `Device`
- App / OS / software values: `4.60` / `26.2` / `46000856`
- Registration model and app: `iPhone` / `Audible`
- Registration client domain: `DeviceLegacy`
- Cookie domain: full Amazon marketplace domain, including its leading dot

Do not copy the Android profile's 20-hex serial, Android device type, Android page ID, Android callback host, device metadata block, FRC context, or Android user-agent into this flow.

## Remaining validation

- Build and unit-test the updated branch.
- Test the full flow on the Redmi 9i / Android 10 / ARMv7, including browser/WebView handoff, MFA/CAPTCHA, callback capture, app background/resume, registration, encrypted save, force-stop/restart restore, and library sync.
- Verify token refresh against the same current profile. The current refresh maintainer still uses the pinned AudibleApi `Authorize.RefreshAccessTokenAsync` path; it has not yet been proven equivalent to LibriSync's updated refresh request.
- Verify actual account access and library sync with the account owner. A CI build is not proof that Amazon accepts a live registration.

## Security and scope boundaries

Only use this flow for an Audible account the user is authorized to access. Keep credentials and callback material out of logs and backups. No DRM circumvention or audiobook redistribution is implemented by this login change.

## References

- LibriSync source: `Promises/LibriSync`, current inspected commit `d3c1b09993ab78def6d541b191ebd15390da6aae` (v0.0.34 changelog, 2026-11-05).
- LibriSync `src/screens/LoginScreen.tsx`: WebView callback handling.
- LibriSync `native/rust-core/src/api/auth.rs`: PKCE URL generation, current registration payload, and refresh request.
- LibriSync `AUDIBLE_DEVICE_REGISTRATION.md`: current profile and October 2026 profile update.
- Libation desktop implementation at commit `7f37f3a62e5269ec3f5ccf4906fc30ecccec088d`: `Source/LibationCli/Options/LoginExternalOptions.cs`.
