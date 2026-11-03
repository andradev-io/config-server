# Managing grants from the config server (Keycloak)

A global `config:admin` can manage who holds which config-server grants from the site (`api/access`). The server does
not store grants: it drives the Keycloak Admin REST API, and Keycloak stays the source of truth.

- A grant is a **client role** of the config client (`Auth:Oidc:ClientId`, e.g. `config-manager-web`) named
  `config:<read|write|admin>[:<app>[:<env>]]`, for example `config:read`, `config:write:billing-service`,
  `config:read:billing-service:prd`, `config:write:*:dev`. The role is created on the first grant and is kept when
  the last mapping is revoked.
- The role is mapped to a user or to a group. Users also get the roles of their groups and parent groups; the UI lists
  those as inherited (`via` = group name). Revoke them on the group.
- A change takes effect at the subject's **next token refresh or sign-in** (it is read from the access token).
- The API reads the roles from `resource_access.{clientId}.roles`, so set
  `Auth:Oidc:RoleClaim=resource_access.{clientId}.roles` (or a list that includes it).

The server signs in to Keycloak as a separate confidential client with a service account (`client_credentials`).
Its secret is read from configuration only; it is never logged or returned.

## 1. Create the admin service client

In the Keycloak admin console, in the realm that holds your users and the config client:

1. **Clients > Create client**
   - Client type: `OpenID Connect`
   - Client ID: `config-manager-admin`
   - Next.
2. **Capability config**
   - Client authentication: **On** (confidential client)
   - Authorization: Off
   - Authentication flow: clear **Standard flow**, **Direct access grants** and **Implicit flow**; select
     **Service accounts roles**. Leave the other flows off.
   - Next, then Save (no redirect URIs or web origins are needed).
3. **Credentials** tab: Client Authenticator `Client Id and Secret`. Copy the **Client secret**.

## 2. Give its service account the realm-management roles

**Clients > config-manager-admin > Service accounts roles > Assign role**. Switch the filter to **Filter by clients**,
search `realm-management` and assign:

| Role | Used for |
| --- | --- |
| `view-users` | read user and group role mappings, a user's groups, group details |
| `query-users` | search users |
| `query-groups` | search groups |
| `view-clients` | find the config client and read its roles |
| `manage-clients` | create the `config:*` client roles on first use |
| `manage-users` | map and unmap roles on users and groups |

`manage-users` and `manage-clients` are broad: they allow changing any user and any client of the realm. Keep the
secret in a secret store, rotate it from the Credentials tab, and consider Keycloak fine-grained admin permissions to
narrow them.

## 3. Configure the server

Environment variables (`__` separates sections):

```bash
IdentityAdmin__Provider=Keycloak
IdentityAdmin__Keycloak__BaseUrl=https://keycloak.example.com   # include /auth for Keycloak < 17
IdentityAdmin__Keycloak__Realm=acme
IdentityAdmin__Keycloak__AdminClientId=config-manager-admin
IdentityAdmin__Keycloak__AdminClientSecret=<client secret from step 1>
# Optional: the client whose roles carry the grants. Defaults to Auth__Oidc__ClientId.
IdentityAdmin__Keycloak__ConfigClientId=config-manager-web
# Optional: http is accepted for loopback hosts (http://localhost:8080); for other http hosts on a private network
# (e.g. http://keycloak:8080 in Docker) set this to false.
# IdentityAdmin__Keycloak__RequireHttps=false
# Optional: timeout of each Keycloak call, in seconds (1-300, default 15).
# IdentityAdmin__Keycloak__TimeoutSeconds=15
```

For local runs, put them in the git-ignored `.env` at the repository root (template: `.env.example`); real
environment variables win over it. The same settings work as command-line arguments (`--IdentityAdmin:Provider=Keycloak`, ...), but do not pass the
secret on the command line: other users of the machine can read process arguments. `appsettings.json` documents the
section with `Provider=None`; `appsettings.Development.json` keeps it `None`.

Invalid settings stop the server at startup with a message naming the setting (the secret is never printed).

Docker example:

```bash
docker run -e IdentityAdmin__Provider=Keycloak \
  -e IdentityAdmin__Keycloak__BaseUrl=https://keycloak.example.com \
  -e IdentityAdmin__Keycloak__Realm=acme \
  -e IdentityAdmin__Keycloak__AdminClientId=config-manager-admin \
  -e IdentityAdmin__Keycloak__AdminClientSecret="$KC_ADMIN_SECRET" \
  ...
```

## 4. Check it

1. Optional, outside the server: request a token for the admin client.

   ```bash
   curl -s -d grant_type=client_credentials -d client_id=config-manager-admin \
     --data-urlencode "client_secret=$KC_ADMIN_SECRET" \
     https://keycloak.example.com/realms/acme/protocol/openid-connect/token
   ```

2. As a global `config:admin`, `GET /api/access/info` returns
   `{"provider":"keycloak","enabled":true,"configClientId":"config-manager-web","note":"Grants take effect at ..."}`.
3. `GET /api/access/users?search=ali` lists users. If something is missing, the API answers `502 {error}` naming the
   setting or the realm-management role to add, for example
   `The admin service account lacks permission to search users: grant the service account of client 'config-manager-admin' the realm-management role 'query-users'.`

The first global admin still has to be assigned in Keycloak itself: map the client role `config:admin` of the config
client to that user (or a group), or use `Auth:Oidc:RoleMap`.

## API reference (`api/access`, global `config:admin` only, never with an API key)

| Request | Result |
| --- | --- |
| `GET api/access/info` | `{provider:"none"\|"keycloak", enabled, configClientId?, note?}` |
| `GET api/access/users?search=&first=0&max=20` | `[{id, username, displayName, email}]` (`max` 1-100) |
| `GET api/access/groups?search=&first=0&max=20` | `[{id, name, path}]` |
| `GET api/access/subjects/{user\|group}/{id}/grants` | `[{permission, app, environment, role, inherited, via?}]` |
| `POST api/access/subjects/{user\|group}/{id}/grants` body `{permission, app?, environment?}` | `201` with the grant (idempotent) |
| `DELETE api/access/subjects/{user\|group}/{id}/grants?permission=&app=&environment=` | `204` (idempotent) |

- `app`/`environment` default to `*`. `admin` is global only. Names are case-sensitive, like role matching.
- POST refuses applications and environments the config source does not know (`400 {error}`, with a hint when only
  the case differs). DELETE only checks the syntax, so grants on removed applications can still be revoked.
- Errors are `{error}`: `400` invalid input, `404` unknown user or group, `501` when `IdentityAdmin:Provider=None`,
  `502` when Keycloak refuses or fails (credentials, missing realm-management role, unknown realm or client, timeout).
- Every grant and revoke is logged at Information with the acting admin, the subject and the role.

Limits: without a search, Keycloak 23+ returns only top-level groups (search to find subgroups). Composite roles that
contain `config:*` roles are not shown as grants.

## OIDC API keys (`api/oidc-keys`): machine credentials created from the site

The **API Keys** screen can also create **OIDC API keys**: confidential clients with a service account that hold
`config:*` grants, created and deleted through the same admin client. Nothing else is needed in Keycloak beyond
section 2, which already covers them:

| Step | Keycloak call | Realm-management role |
| --- | --- | --- |
| list | `GET clients?clientId=cfs-&search=true`, service account user and its client role mappings | `view-clients`, `view-users` |
| create | `POST clients`, `GET clients/{id}/service-account-user`, create missing `config:*` roles, map them, `GET clients/{id}/client-secret` | `manage-clients`, `manage-users`, `view-users` |
| rotate secret | `POST clients/{id}/client-secret` | `manage-clients` |
| delete | `DELETE clients/{id}` | `manage-clients` |

- The server only lists, rotates and deletes clients whose id starts with `cfs-` **and** that carry the attribute
  `managed-by=config-server` (it sets both on create). Any other client of the realm is never touched, even when its
  id looks similar.
- A new client is service-account only: confidential, `client-secret` authentication, standard, implicit and
  direct-access flows off. It gets the client roles of the config client (`config-manager-web`) mapped to its service
  account user, so the token carries `resource_access.config-manager-web.roles` like a user's. Keycloak's default
  `roles` client scope (audience resolve + client roles mappers) puts the config client in `aud`; if your realm
  removed that scope or the realm default scopes, add an *Audience* mapper for the config client to the credential's
  client scope or the API rejects the token (`Auth:Oidc:Audience` defaults to the config client id).
- Callers request a token and send it as a Bearer. The token endpoint shown in the UI is
  `{Auth:Oidc:Authority}/protocol/openid-connect/token` (the public issuer, not the internal admin `BaseUrl`);
  override it with `IdentityAdmin__Keycloak__TokenEndpoint`.

  ```bash
  TOKEN=$(curl -s https://auth.example.com/realms/acme/protocol/openid-connect/token \
    -d grant_type=client_credentials -d client_id=cfs-ci-x1y2z3 --data-urlencode client_secret=... | jq -r .access_token)
  curl -H "Authorization: Bearer $TOKEN" https://config.example.com/config/billing-service/prd
  ```

- Machine credentials never get `config:admin`. If the creation fails after the client was made (for example a
  missing `manage-users`), the server deletes the half-made client before answering `502 {error}`.
- The secret is returned once, on create and on rotate, and is never logged.

| Request (global `config:admin` only, never with an API key) | Result |
| --- | --- |
| `GET api/oidc-keys` | `[{id, clientId, name, grants:[{permission, app, environment, role, inherited}], enabled, createdAt?}]` |
| `POST api/oidc-keys` body `{name, grants:[{permission, app?, environment?}]}` | `201 {client, clientId, secret, tokenEndpoint}` |
| `POST api/oidc-keys/{id}/rotate` | `200 {client, clientId, secret, tokenEndpoint}` |
| `DELETE api/oidc-keys/{id}` | `204` (idempotent) |

`501` when `IdentityAdmin:Provider=None`, `400` invalid input (applications and environments must exist, exact case),
`404` unknown credential on rotate, `502` when Keycloak refuses or fails.
