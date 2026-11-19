---
title: Authentication
description: Endpoint credentials and web UI access
---

# Authentication

There are two separate things called authentication in Trignis, and it helps to keep them apart. The first is how Trignis proves itself to the HTTP endpoints you send changes to. The second is how you prove yourself to the dashboard. This page describes both, and how Trignis encrypts their credentials on disk.

## Endpoint authentication

An `auth` block on an HTTP destination sets how Trignis authenticates its requests. Four types are available, per destination.

### Bearer

```yaml
auth: { type: bearer, token: your-token }
```

This sends `Authorization: Bearer <token>` with each request, which suits a static token issued by the receiving service.

### Basic

```yaml
auth: { type: basic, username: user, password: secret }
```

The pair is base64 encoded into an `Authorization: Basic` header for you, so you can store the username and password as they are.

### API key

```yaml
auth:
  type: apikey
  api_key: your-key
  header_name: X-API-Key
```

`header_name` sets the header. Left out, it defaults to `X-API-Key`.

### OAuth2 client credentials

```yaml
auth:
  type: oauth2
  token_endpoint: https://id.example.com/oauth2/token
  client_id: client-id
  client_secret: client-secret
  scope: changes.write
  token_expiration: 1h
```

Use this option if the receiving side uses an identity provider. Trignis requests a token using `grant_type=client_credentials` and caches it per endpoint and client id, so a busy polling interval does not mean a token request every cycle.

The token lifetime is `token_expiration` if set, otherwise the server's `expires_in`, otherwise one hour. Trignis refreshes the token one minute before expiry, or at half its lifetime for tokens shorter than two minutes.

If several exports need the same fresh token at once, they wait on a single request rather than all calling the token endpoint together. Identity providers tend to appreciate that.

## Encryption at rest

You can write secrets into an environment file as plain text, which is by far the easiest way to get started. The first time Trignis reads the file it encrypts those values and rewrites the file in place, adding a `PWENC:` prefix to each encrypted value.

These fields are picked up automatically:

- Every value under `databases`
- `auth`: `token`, `password`, `api_key`, `client_secret`, `client_id`
- Every value under `headers`
- Queue destinations: `password`, `connection_string`, `secret_access_key`, `access_key_id`

Trignis replaces each secret value in place. Comments and layout are unchanged.

The scheme is hybrid: each value gets its own AES-256 key, which is then wrapped with a 2048-bit RSA key. The RSA private key is stored in `.core/`, encrypted with your `TRIGNIS_ENCRYPTION_KEY`. The practical upshot is that a copy of an environment file, on its own, gives nothing away.

::: danger
Losing `TRIGNIS_ENCRYPTION_KEY` or the `.core/` folder makes your existing configuration unreadable, and Trignis refuses to start with secrets it cannot decrypt. Recovery means deleting `.core/`, letting Trignis regenerate it, and entering every secret again as plain text. Please back both up alongside your other credentials.
:::

Day to day this stays out of your way. Values with a `PWENC:` prefix are left untouched, so you can drop a new plaintext secret into a file full of encrypted ones and it will be picked up on the next read.

## Dashboard access

Signing in to the dashboard uses `Trignis:AdminApiKey`. Trignis exchanges it for a session cookie good for 24 hours, signed with ASP.NET Core Data Protection. The signing keys are kept under `.core/dp-keys`, which is why your session survives a service restart rather than logging everyone out on every deploy.

The same key is asked for a second time before you [pause an environment](/guide/dashboard#pausing-change-tracking), since a paused environment exports nothing and raises no error.

A few protections sit on the sign-in route:

- **Lockout.** Ten failed attempts from one address lock it out for 30 minutes, and a correct key is refused while the lockout stands. For reverse proxy deployments, set [`WebHost:TrustedProxies`](/reference/global-settings#webhost) to count failures per client address.
- **CSRF.** Signing in needs a one-time token from `/ui/api/auth/csrf`, which is consumed as it is used.
- **Double submit.** Mutating calls echo the session CSRF cookie back in an `X-CSRF-Token` header, so a request forged from another site cannot succeed.

Cookies are `HttpOnly`, with the single exception of the CSRF cookie, which page JavaScript reads by design. All of them are `SameSite=Lax`. The `Secure` flag follows the scheme of the request unless `WebHost:SecureCookies` says otherwise, which is the setting for a TLS-terminating proxy without `X-Forwarded-Proto`.

::: warning
Worth being clear about a limitation here: the admin key is a single shared credential. There are no user accounts, and no audit trail of who did what. The dashboard is best treated as an operator tool on a trusted network, with TLS in front of it. Where only local access is needed, setting `WebHost:Host` to `localhost` and reaching it over an SSH tunnel is a tidy answer.
:::
