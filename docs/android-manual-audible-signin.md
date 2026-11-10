# Android external-browser Audible sign-in: supported manual return flow

## Status

The Android app now contains an implemented user-assisted external-browser sign-in flow backed by the pinned AudibleApi 14.1.2 integration, encrypted identity snapshot storage, and an account status UI. This documents the intended user flow and security boundaries; it does **not** claim successful real-account authentication on a physical handset. Browser return, MFA/CAPTCHA, encrypted persistence, process restart, and library sync still require device/account-owner validation.

## Upstream behavior verified

In AudibleApi 14.1.2, `ExternalLogin` exposes:
- `GetLoginUrl()`: creates the provider login URL.
- `GetSignInCookies()`: returns a CookieCollection documented for the initial sign-in page.
- `Login(responseUrl)`: calls `OAuth2.Parse(responseUrl)` and creates an in-memory `Identity`.

The `ILoginExternal.GetResponseUrl(loginUrl, signInCookies)` callback is synchronous. Importantly, the pinned Libation desktop `LoginExternalOptions.CliLoginExternal` implementation does **not** install or forward `signInCookies` to its browser. It prints the login URL, asks the user to complete sign-in in their browser, and asks the user to paste the final address-bar URL. It then validates that the response is an absolute URL and returns it to AudibleApi.

Therefore, cookie injection into Android's external browser is not a prerequisite for reproducing the existing supported Libation desktop workflow. The earlier handoff audit overstated this requirement. The cookie collection must still be treated as sensitive and must never be leaked; mobile should not invent a new cookie transport.

## Implemented mobile UX

1. User selects their Audible marketplace and starts sign-in.
2. App creates one in-memory `ExternalLogin` instance and obtains its generated login URL.
3. App opens that URL in the device's normal browser, outside the app.
4. User completes password, OTP, CAPTCHA and any security checks in that browser.
5. User copies the complete final address-bar URL, returns to the VØRATH sign-in screen, pastes it into the response field and explicitly submits it.
6. The app validates the HTTPS scheme, exact marketplace host, default port, expected landing path and presence of a non-empty authorization-code query parameter. It then passes the unchanged URL directly to the same in-memory `ExternalLogin.Login(responseUrl)` instance.
7. The app performs the upstream registration exchange and persists the resulting complete identity through the mobile-owned encrypted snapshot store. The UI reports connected only after registration and encrypted persistence both succeed.

This is a user-assisted flow matching the desktop CLI, not an OAuth redirect/deep-link callback. Do not embed Amazon credentials in a WebView, add an exported deep link, or accept callback URLs from arbitrary Android intents.

## Response URL handling

- The response URL is a short-lived credential-bearing value. Keep it only in the active sign-in operation's memory.
- Never log it, show it in status/error text, put it in analytics, backups, Android intents, or a persisted draft.
- A user explicitly pasting from the browser may involve the Android clipboard. Explain that the pasted URL is sensitive and should not be shared. Clear the input immediately after successful submission or cancellation; do not automatically read clipboard contents.
- Require an active, unexpired, one-time challenge and bind the response to the exact in-memory `ExternalLogin` instance created for that challenge.
- Before invoking the upstream parser, require an absolute HTTPS URL with a non-empty host, no embedded user information, and the default HTTPS port. Bind the pending challenge to the exact login host generated for the selected Audible marketplace and require a case-insensitive exact host match. Reject suffix lookalikes (for example, `www.amazon.com.evil.example`), unrelated hosts, non-default ports, and credential-bearing URL user-info.
- Only after the mobile host check succeeds, pass the unchanged response string to `ExternalLogin.Login` / `OAuth2.Parse` for provider-specific authorization-code parsing. The pinned upstream `OAuth2.Parse` extracts `openid.oa2.authorization_code` from the query string but does not validate the host; the marketplace binding is enforced by the mobile authenticator, not by the upstream parser.
- Reject empty, malformed, expired, replayed, mismatched-challenge, wrong-marketplace-host, and otherwise unsafe URL submissions. Do not attempt registration when validation or parsing fails.

## Registration and identity persistence

After `ExternalLogin.Login` succeeds:
1. Use the pinned AudibleApi registration API and explicitly reviewed default device-registration profile. Do not silently deregister an existing device, change profiles, or fabricate device identity data.
2. Parse registration into the in-memory Audible identity using the pinned upstream parser.
3. Persist the complete identity as one validated versioned encrypted snapshot using the app's Android Keystore-backed store.
4. If registration or persistence fails, clear the in-memory challenge/session and remain disconnected.
5. Create the authenticated API using the public `Api(IIdentityMaintainer)` constructor and an app-owned identity maintainer; never use the file-based `EzApiCreator.GetApiAsync` convenience overload because it delegates identity persistence to a file-oriented persister.

## Automated and physical-device validation

Automated coverage should continue to verify generated login URL validation, challenge lifecycle, and safe failure behavior.
- Physical Android 10 / ARMv7 validation remains outstanding: browser switching, copy/paste, Amazon MFA/CAPTCHA, app resume, encrypted save, restart restore, and a successful library sync.
- CI builds for ARMv7 and ARM64 do not substitute for this real-device validation.

## References

- Pinned AudibleApi 14.1.2 source: `ExternalLogin`, `OAuth2.Parse`, `ILoginExternal`, `RegistrationOptions`.
- Libation desktop implementation at commit `7f37f3a62e5269ec3f5ccf4906fc30ecccec088d`: `Source/LibationCli/Options/LoginExternalOptions.cs`.
