# Audible browser sign-in architecture

## Current verified state
The app authentication adapter uses the pinned AudibleApi 14.1.2 ExternalLogin and registration flow. It must receive the provider-generated /ap/maplanding URL, create an in-memory Audible identity, complete device registration and persist the complete identity snapshot. Generic Login with Amazon profile OAuth is not a replacement for this private Audible device-registration protocol.

Amazon's published Login with Amazon guidance recommends Chrome Custom Tabs on Android rather than an embedded WebView. Audible's account pages may present MFA, OTP, CAPTCHA, risk checks or account recovery; the app cannot guarantee those upstream decisions.

## Important integration constraint
Android Custom Tabs are not equivalent to an interceptable WebView. Audible's generated openid.return_to points to the provider's marketplace callback. A browser-view-only switch cannot safely capture that URL unless the provider return_to can be set to an app-registered callback or a trusted HTTPS broker can relay the complete response. A custom app-link URL cannot be substituted for the provider callback unless AudibleApi exposes a supported way to configure that callback and its cryptographic state.

## Security requirements
- Never collect Amazon passwords, OTPs or CAPTCHA answers in an app-owned form.
- Never log authorization URLs, callback URLs, cookies, access tokens, refresh tokens or device private keys.
- Keep challenge state in memory, short-lived and one-time.
- Bind callback host to the selected Locale marketplace. Require HTTPS, default port, exact host (allow only a deliberate www normalization), exact callback path and a non-empty authorization-code parameter.
- Persist only a fully registered, validated identity through the existing Keystore-backed encrypted snapshot store.
- Treat invalid, cancelled, expired and replayed callbacks as failures; do not overwrite a prior valid identity.
- Do not bypass Amazon CAPTCHA, MFA, bot checks, regional restrictions or DRM.

## Next engineering gate
Before enabling Custom Tabs in production, inspect the exact ExternalLogin API surface in AudibleApi 14.1.2 and determine whether openid.return_to can be configured to an app link or a controlled HTTPS broker without changing the expected authorization semantics. If not, retain the documented manual browser return flow or obtain a documented provider-supported redirect mechanism. Do not ship a Custom Tab that launches correctly but cannot complete authentication.
