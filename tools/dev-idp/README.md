# dev-idp

Local OIDC provider (`oidc-provider`) to test config-server authentication without Keycloak or Docker. **Dev only**: in-memory state, throwaway signing keys, no passwords.

```bash
npm --prefix tools/dev-idp install
npm --prefix tools/dev-idp start          # http://localhost:9000
```

Run the API against it (this is the `config-api-oidc` entry in `.claude/launch.json`):

```bash
dotnet run --project src/Application/ConfigServer.Application.Api --urls http://localhost:5000 --environment Development \
  --Auth:Mode=Oidc --Auth:Oidc:Authority=http://localhost:9000 --Auth:Oidc:Audience=http://localhost:5000 \
  --Auth:Oidc:ClientId=config-web --Auth:Oidc:RequireHttpsMetadata=false
```

- Client `config-web`: public, authorization code + PKCE, redirect `http://localhost:5173/` (override with `SPA_ORIGIN`).
- Access tokens are JWTs with `aud=http://localhost:5000` and the user's roles twice: a flat `roles` array (the API's default `RoleClaim`) and Keycloak-style client roles `resource_access.config-web.roles`. To read the Keycloak shape add `--Auth:Oidc:RoleClaim=resource_access.{clientId}.roles` (or a union such as `roles,resource_access.{clientId}.roles`).
- Role format: `config:<read|write|admin>[:<app>[:<env>]]` (omitted app/env = all; `write` includes `read`; `admin` is global only).

| User | Roles | Can |
| --- | --- | --- |
| `alice` | `config:admin` | everything, including API keys |
| `carol` | `config:write` | read/write every app and environment |
| `bob` | `config:read` | read everything |
| `dave` | none | nothing (403) |
| `erin` | `config:write:billing-service:dev`, `config:read:billing-service:stg` | write `billing-service` dev, read its stg; no `default.*` writes, no environment registry changes |
| `frank` | `config:read:demo-app` | read every environment of `demo-app` |

- Sign-out goes through the IdP's end-session page, which asks for confirmation.
