# Android external-browser Audible sign-in: supported manual return flow

## Status

This is a design correction based on the exact pinned AudibleApi source and Libation desktop implementation. It is not a claim that mobile authentication is implemented.

## Upstream behavior verified

In AudibleApi 14.1.2, `ExternalLogin` exposes:
- `GetLoginUrl()`: creates the provider login URL.
- `GetSignInCookies()`: returns a CookieCollection documented for the initial sign-in page.
- `Login(responseUrl)`: calls `OAuth2.Parse(responseUrl)` and creates an in-memory `Identity`.

The `ILoginExternal.GetResponseUrl(loginUrl, signInCookies)` callback is synchronous. Importantly, the pinned Libation desktop `LoginExternalOptions.CliLoginExternal` implementation does **not** install or forward `signInCookies` to its browser. It prints the login URL, asks the user to complete sign-in in their browser, and asks the user to paste the final address-bar URL. It then validates that the response is an absolute URL and returns it to AudibleApi.

Therefore, cookie injection into Android's external browser is not a prerequisite for reproducing the existing supported Libation desktop workflow. The earlier handoff audit overstated this requirement. The cookie collection must still be treated as sensitive and must never be leaked; mobile should not invent a new cookie transport.

## Proposed mobile UX

1. User selects their Audible marketplace and starts sign-in.
2. App creates one in-memory `ExternalLogin` instance and obtains its generated login URL.
3. App opens that URL through Android's normal external browser resolver.
4. App explains that the user must finish Amazon/Audible sign-in, then copy the complete final address-bar URL and return to the app.
5. User pastes the URL into a dedicated response field and explicitly submits it.
6. The app passes the exact response string directly to the same in-memory `ExternalLogin.Login(responseUrl)` instance. Do not parse/rewrite the query string in the UI layer.
7. The app performs the upstream registration exchange and persists the resulting complete identity through the mobile-owned encrypted snapshot store. Only show connected after registration and encrypted persistence both succeed.

This is a user-assisted flow matching the desktop CLI, not an OAuth redirect/deep-link callback. Do not add an exported deep link or accept callback URLs from arbitrary Android intents.

## Response URL handling

- The response URL is a short-lived credential-bearing value. Keep it only in the active sign-in operation's memory.
- Never log it, show it in status/error text, put it in analytics, backups, Android intents, or a persisted draft.
- A user explicitly pasting from the browser may involve the Android clipboard. Explain that the pasted URL is sensitive and should not be shared. Clear the input immediately after successful submission or cancellation; do not automatically read clipboard contents.
- Require an active, unexpired, one-time challenge and bind the response to the exact in-memory `ExternalLogin` instance created for that challenge.
- Require an absolute HTTPS URL as a minimum syntactic check, then let the pinned upstream `ExternalLogin.Login` / `OAuth2.Parse` perform the provider-specific authorization-code parsing. Note that upstream `OAuth2.Parse` extracts `openid.oa2.authorization_code` from the query string and does not validate the host; mobile must not claim that URL host validation is provided by the upstream parser.
- Reject empty, malformed, expired, replayed, and mismatched challenge submissions. Do not attempt registration when parsing fails.

## Registration and identity persistence

After `ExternalLogin.Login` succeeds:
1. Use the pinned AudibleApi registration API and explicitly reviewed default device-registration profile. Do not silently deregister an existing device, change profiles, or fabricate device identity data.
2. Parse registration into the in-memory Audible identity using the pinned upstream parser.
3. Persist the complete identity as one validated versioned encrypted snapshot using the app's Android Keystore-backed store.
4. If registration or persistence fails, clear the in-memory challenge/session and remain disconnected.
5. Create the authenticated API using the public `Api(IIdentityMaintainer)` constructor and an app-owned identity maintainer; never use the file-based `EzApiCreator.GetApiAsync` convenience overload because it delegates identity persistence to a file-oriented persister.

## Required implementation tests

- Generated login URL is absolute HTTPS and comes from the pinned AudibleApi instance.
- Browser launch uses only the generated URL.
- Initial sign-in cookies, if retrieved for API compatibility, never enter intents, UI, logs, preferences, files or clipboard.
- A valid pasted response is passed unchanged to the same challenge's `ExternalLogin.Login`.
- Empty, non-HTTPS, malformed, expired, replayed and cross-challenge responses are rejected.
- A failed parser/registration/persistence step leaves no connected state and no partial identity.
- Successful registration is not reported as connected until encrypted snapshot persistence completes.
- Input and in-memory challenge are cleared on success, cancellation, timeout, and disconnect.
- Process death before response submission safely cancels the attempt; it must not persist the response URL for later replay.
- ARMv7 Android 10 physical-device test covers browser switching, copy/paste, Amazon MFA/CAPTCHA, app resume, encrypted save and restart restore.

## References

- Pinned AudibleApi 14.1.2 source: `ExternalLogin`, `OAuth2.Parse`, `ILoginExternal`, `RegistrationOptions`.
- Libation desktop implementation at commit `7f37f3a62e5269ec3f5ccf4906fc30ecccec088d`: `Source/LibationCli/Options/LoginExternalOptions.cs`.
