---
title: Authentication
description: Endpoint credentials and web UI access
---

# Authentication

There are two separate things called authentication in Trignis, and it helps to keep them apart. The first is how Trignis proves itself to the HTTP endpoints you send changes to. The second is how you prove yourself to the dashboard. This page covers both, along with how the credentials for either are kept safe on disk.

## Endpoint authentication

Adding an `Auth` block to an HTTP endpoint tells Trignis how to authenticate when it posts your changes. Four types are available, and you can use a different one per endpoint.

### Bearer

```json
{ "Auth": { "Type": "Bearer", "Token": "your-token" } }
```

This sends `Authorization: Bearer <token>` with each request, which suits a static token issued by the receiving service.

### Basic

```json
{ "Auth": { "Type": "Basic", "Username": "user", "Password": "secret" } }
```

The pair is base64 encoded into an `Authorization: Basic` header for you, so you can store the username and password as they are.

### ApiKey

```json
{
  "Auth": {
    "Type": "ApiKey",
    "ApiKey": "your-key",
    "HeaderName": "X-API-Key"
  }
}
```

If the receiving service expects a different header, `HeaderName` lets you name it. Left out, it defaults to `X-API-Key`.

### OAuth2ClientCredentials

```json
{
  "Auth": {
    "Type": "OAuth2ClientCredentials",
    "TokenEndpoint": "https://id.example.com/oauth2/token",
    "ClientId": "client-id",
    "ClientSecret": "client-secret",
    "Scope": "changes.write",
    "TokenExpirationSeconds": 3600
  }
}
```

This is the option to reach for when the receiving side sits behind a proper identity provider. Trignis requests a token using `grant_type=client_credentials` and caches it per endpoint and client id, so a busy polling interval does not mean a token request every cycle.

Lifetime is worked out in the order you would expect: `TokenExpirationSeconds` if you set it, otherwise whatever the server reports in `expires_in`, and one hour as a last resort. Refreshes happen a minute ahead of expiry, which keeps a token from lapsing partway through a request.

If several exports need the same fresh token at once, they wait on a single request rather than all calling the token endpoint together. Identity providers tend to appreciate that.

## Encryption at rest

You can write secrets into an environment file as plain text, which is by far the easiest way to get started. The first time Trignis reads the file it encrypts those values and rewrites the file in place, leaving a `PWENC:` prefix so you can see at a glance what has been handled.

These fields are picked up automatically:

- Every value under `ConnectionStrings`
- `Auth`: `Token`, `Password`, `ApiKey`, `ClientSecret`, `ClientId`
- `MessageQueue`: `Password`, `ConnectionString`, `SecretAccessKey`, `AccessKeyId`

The scheme is hybrid: each value gets its own AES-256 key, which is then wrapped with a 2048-bit RSA key. That RSA private key lives in `.core/`, encrypted in turn with your `TRIGNIS_ENCRYPTION_KEY`. The practical upshot is that a copy of an environment file, on its own, gives nothing away.

::: danger
Losing `TRIGNIS_ENCRYPTION_KEY` or the `.core/` folder makes your existing configuration unreadable, and Trignis will refuse to start rather than carry on with secrets it cannot decrypt. Recovery means deleting `.core/`, letting Trignis regenerate it, and entering every secret again as plain text. Please back both up alongside your other credentials.
:::

Day to day this stays out of your way. Values already carrying a `PWENC:` prefix are left untouched, so you can drop a new plaintext secret into a file full of encrypted ones and it will be picked up on the next read.

## Dashboard access

Signing in to the dashboard uses `Trignis:AdminApiKey`. Trignis exchanges it for a session cookie good for 24 hours, signed with ASP.NET Core Data Protection. The signing keys are kept under `.core/dp-keys`, which is why your session survives a service restart rather than logging everyone out on every deploy.

The same key is asked for a second time before you [pause an environment](/guide/dashboard#pausing-change-tracking), since that is the one action whose failure mode is silence.

A few protections sit on the sign-in route:

- **Lockout.** Ten failed attempts from one address lock it out for 30 minutes, and a correct key is refused while the lockout stands.
- **CSRF.** Signing in needs a one-time token from `/ui/api/auth/csrf`, which is consumed as it is used.
- **Double submit.** Mutating calls echo the session CSRF cookie back in an `X-CSRF-Token` header, so a request forged from another site cannot succeed.

Cookies are `HttpOnly`, with the single exception of the CSRF cookie, which page JavaScript reads by design. All of them are `SameSite=Lax`. The `Secure` flag follows the scheme of the request unless `WebHost:SecureCookies` says otherwise, which is the setting you want behind a TLS-terminating proxy.

::: warning
Worth being clear about a limitation here: the admin key is a single shared credential. There are no user accounts, and no audit trail of who did what. The dashboard is best treated as an operator tool on a trusted network, with TLS in front of it. Where only local access is needed, setting `WebHost:Host` to `localhost` and reaching it over an SSH tunnel is a tidy answer.
:::
