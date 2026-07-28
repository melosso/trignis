---
title: Authentication
description: Endpoint credentials and web UI access
---

# Authentication

## Endpoint authentication

Set `Auth` on an HTTP endpoint. Four types are supported.

### Bearer

```json
{ "Auth": { "Type": "Bearer", "Token": "your-token" } }
```

Sends `Authorization: Bearer <token>`.

### Basic

```json
{ "Auth": { "Type": "Basic", "Username": "user", "Password": "secret" } }
```

Base64 encodes the pair into `Authorization: Basic`.

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

`HeaderName` defaults to `X-API-Key`.

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

Trignis requests a token with `grant_type=client_credentials` and caches it per endpoint and client id. Lifetime comes from `TokenExpirationSeconds` if set, otherwise the server's `expires_in`, otherwise one hour. Tokens are refreshed a minute before expiry so one cannot lapse mid request.

Concurrent exports needing the same fresh token wait on a single request rather than stampeding the token endpoint.

## Encryption at rest

Sensitive values are encrypted the first time Trignis reads an environment file, and the file is rewritten in place. Encrypted values are prefixed `PWENC:`.

Encrypted automatically:

- Every value under `ConnectionStrings`
- `Auth`: `Token`, `Password`, `ApiKey`, `ClientSecret`, `ClientId`
- `MessageQueue`: `Password`, `ConnectionString`, `SecretAccessKey`, `AccessKeyId`

Encryption is hybrid: a per-value AES-256 key wrapped with a 2048-bit RSA key. The RSA private key lives in `.core/`, itself encrypted with `TRIGNIS_ENCRYPTION_KEY`.

::: danger
Losing `TRIGNIS_ENCRYPTION_KEY` or `.core/` makes existing configuration unreadable. Trignis refuses to start rather than continuing with unreadable secrets. Recovery means deleting `.core/`, letting Trignis regenerate, and re-entering every secret as plaintext.
:::

Values already prefixed `PWENC:` are left alone, so writing a new plaintext secret into a file that already has encrypted ones works fine.

## Web UI access

`Trignis:AdminApiKey` is the credential. Sign-in exchanges it for a session cookie valid for 24 hours, signed with ASP.NET Core Data Protection. Keys persist under `.core/dp-keys`, so sessions survive a restart.

Protections on the sign-in route:

- **Lockout.** 10 failed attempts from one IP locks it for 30 minutes. A correct key is refused while locked.
- **CSRF.** Sign-in needs a one-time token from `/ui/api/auth/csrf`, consumed on use.
- **Double submit.** Every mutating call must echo the session CSRF cookie in `X-CSRF-Token`.

Cookies are `HttpOnly` (except the CSRF cookie, which page JavaScript must read) and `SameSite=Lax`. The `Secure` flag follows the request scheme unless `WebHost:SecureCookies` overrides it.

::: warning
The admin key is a single shared credential with no user accounts and no audit trail of who did what. Treat the dashboard as an operator tool on a trusted network, put TLS in front of it, and set `WebHost:Host` to `localhost` if only local access is needed.
:::
