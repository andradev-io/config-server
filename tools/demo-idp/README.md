# demo-idp

Passwordless OIDC provider (`oidc-provider`) for the **public sandbox** of the config server (see `docs/demo.md`). Not for real accounts: anyone who can reach it can sign in. State is in memory and signing keys are generated at start, so a restart signs everybody out (the SPA just redirects to the login again).

```bash
npm --prefix tools/demo-idp install
npm --prefix tools/demo-idp start      # http://localhost:9000, issuer http://localhost:9000/idp
npm --prefix tools/demo-idp test       # unit tests + a full code+PKCE flow against the real provider
```

## What it does

- The login page is a persona picker (no password). **Every sign-in creates a fresh visitor**: `sub` = `visitor-<persona>-<8 hex>`, name `Visitor <Persona> <4 hex>`. The API derives each visitor's private workspace from `sub`.
- Personas and the roles they carry (format `config:<read|write|admin>[:<app>[:<env>]]`):

| Persona | Roles |
| --- | --- |
| Viewer | `config:read` |
| Developer | `config:read`, `config:write:*:dev` |
| Admin | `config:admin`, `config:write`, `config:read` |

- Public client `config-demo-web` (authorization code + PKCE, no secret). Access tokens are JWTs (RS256, 15 min by default, refresh token for silent renew) with `aud=config-demo-web` and the roles twice: Keycloak-shaped `resource_access.config-demo-web.roles` (use `Auth__Oidc__RoleClaim=resource_access.{clientId}.roles`) and a flat `roles` claim (the API default).
- Mounted under the path of the issuer (`/idp`), so one public host can serve the SPA, the API and the IdP.
- `GET /healthz` (also under the prefix) for probes.
- Sign-ins are limited per client address (`CF-Connecting-IP`, else `X-Forwarded-For`): 30 per 10 minutes by default.

## Configuration (environment)

| Variable | Default | Meaning |
| --- | --- | --- |
| `PORT` | `9000` | Listen port |
| `DEMO_IDP_ISSUER` | `http://localhost:$PORT/idp` | Public issuer URL; its path is the mount prefix. Must be exactly what the API and the SPA use as `Authority` |
| `DEMO_IDP_REDIRECT_URIS` | `http://localhost:5173/` | Comma/space separated redirect and post-logout URIs of the SPA (e.g. `https://config.demo.andradev.io/`) |
| `DEMO_IDP_TRUST_PROXY` | `true` | Honour `X-Forwarded-Proto/Host` (needed behind Traefik/Cloudflare so cookies get `Secure`); `false` for plain local use |
| `DEMO_IDP_COOKIE_KEYS` | random per start | Optional cookie signing keys |
| `DEMO_IDP_ACCESS_TOKEN_TTL` | `900` | Access/ID token lifetime (seconds) |
| `DEMO_IDP_SESSION_TTL` | `28800` | Session, grant and refresh-token lifetime (seconds) |
| `DEMO_IDP_LOGINS_PER_10_MIN` | `30` | Sign-ins allowed per client address and 10 minutes |

For local development against `npm run dev` of the Web (port 5173) the defaults work as they are: point the API at `Auth:Oidc:Authority=http://localhost:9000/idp`, `ClientId=config-demo-web`, `RequireHttpsMetadata=false`.

## Notes

- oidc-provider requires resource indicators to be absolute URIs, so tokens are issued for the resource `urn:config-demo:api` and the `aud` claim is rewritten to `config-demo-web` (the API's default audience is its `ClientId`).
- Single replica only (in-memory storage and keys).
