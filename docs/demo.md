# Public sandbox (demo)

A permanently running copy of the config server that anyone can try: sign in with a persona, get a private workspace of sample data, edit it freely, break it, and leave. Nothing in it is real and nothing in it is shared between visitors.

```
                       https://config-demo.andradev.io
                                    |
          Cloudflare Tunnel -> traefik-external (Ingress) or straight to the web Service
                                    |
                    +---------------v----------------+
   browser -------> |  web: nginx (port 8080)        |  Vue UI (static), gzip, cache + security headers,
                    |  one public origin             |  rate limit per visitor address
                    +--+----------------------+------+
        /api  /config  /health |              | /idp
                    +--------v-------+  +------v---------+
                    |  api (8080)    |  |  idp (9000)    |
                    |  Demo mode     |  |  demo-idp      |
                    |  workspaces on |  |  persona picker|
                    |  an emptyDir   |  |  (no password) |
                    +----------------+  +----------------+
```

Everything is same origin: the UI is built with `VITE_API_BASE_URL=/`, so there is no CORS and the OIDC issuer is `https://<host>/idp`.

## The visitor's experience

1. Opening the site redirects to the sign-in page of the demo IdP: three buttons, no password.
2. Every sign-in creates a **fresh visitor** (`visitor-<persona>-<random>`), and the API gives that visitor an isolated copy of the sample apps (`demo-data/`, three apps with dev/stg/prd). Signing in again starts over with a new copy.
3. A banner at the top says it is a sandbox and for how long an idle workspace is kept (dismissible per browser tab).
4. Workspaces are deleted after `Demo__IdleTtlMinutes` of inactivity (default 120) and when the API restarts.

| Persona | Roles | What they can do |
| --- | --- | --- |
| Viewer | `config:read` | read every application and environment, compare, view the merged config |
| Developer | `config:read`, `config:write:*:dev` | the above, plus edit the `dev` environment of every application |
| Admin | `config:admin`, `config:write`, `config:read` | everything: all files, environments, variables, overrides, plus **API Keys** and **Access** |

**Access and OIDC API keys are simulated.** In the sandbox the Access screen and the OIDC keys talk to an in-memory stand-in for an identity provider, scoped to the visitor's workspace, so the UI and the permission model can be shown without touching any real identity provider (the API refuses to start in Demo mode next to a real `IdentityAdmin` provider). Local API keys work too and authenticate into the workspace they were created in.

## Components

| Piece | Where | Notes |
| --- | --- | --- |
| web | `src/Web` (`Dockerfile`, `nginx/`) | Vue 3 build served by `nginxinc/nginx-unprivileged` on 8080; proxies `/api/`, `/config/`, `/health` to `DEMO_API_UPSTREAM` and `/idp/` to `DEMO_IDP_UPSTREAM` (defaults `config-demo-api:8080`, `config-demo-idp:9000`); `index.html` is `no-store`, `/assets/` immutable; CSP and friends; 30 req/s (burst 60) on the API and 5 req/s (burst 20) on the IdP per visitor address |
| idp | `tools/demo-idp` | `oidc-provider`, see its README. In memory: a restart signs everyone out |
| api | `deploy/Dockerfile.api-demo` | the regular API plus `demo-data/` at `/app/demo-data` (build context = repository root) |
| manifests | `deploy/k8s/config-demo.yaml` | namespace `config-demo`, 3 Deployments (1 replica, non-root, read-only root filesystem), Services, Ingress, NetworkPolicies |
| images | `deploy/build.sh` / `build.ps1` | builds the three images, no push |
| local rehearsal | `deploy/docker-compose.demo.yaml` | whole sandbox on http://localhost:8080 |

## API settings (environment)

All of them are in the ConfigMap of `deploy/k8s/config-demo.yaml` (`__` = section separator):

| Setting | Value | Why |
| --- | --- | --- |
| `Auth__Mode` | `Oidc` | Demo mode requires real tokens: every visitor needs a `sub` |
| `Auth__Oidc__Authority` | `https://config-demo.andradev.io/idp` | exactly the issuer of the demo IdP (also what the SPA is told to use) |
| `Auth__Oidc__ClientId` | `config-demo-web` | public client of the demo IdP; the audience of its tokens (`Audience` falls back to it) |
| `Auth__Oidc__RequireHttpsMetadata` | `true` (`false` only for the local compose) | |
| `Auth__Oidc__RoleClaim` | `resource_access.{clientId}.roles` | the Keycloak-shaped roles the demo IdP emits (a flat `roles` claim exists too) |
| `IdentityAdmin__Provider` | `None` | Demo mode simulates identity management; any real provider is rejected at startup |
| `Demo__Enabled` | `true` | |
| `Demo__TemplatePath` | `/app/demo-data` | read-only seed baked into the image |
| `Demo__WorkspacesPath` | `/workspaces` | writable, an `emptyDir` with a size limit |
| `Demo__IdleTtlMinutes` | `120` | also reported to the UI banner |
| `Demo__TokenEndpoint` | `https://config-demo.andradev.io/idp/token` | shown in the OIDC key `curl` snippets (the default assumes Keycloak's path) |
| `ApiKeys__StorePath` | `/tmp/api-keys.json` | only so nothing writes under a read-only `/app`; Demo mode keeps keys per workspace |

Other `Demo__*` limits (`MaxWorkspaces`, `MaxBytesPerWorkspace`, `MaxFilesPerWorkspace`, `MaxApiKeysPerWorkspace`, `RequestsPerMinute`, `MaxRequestBodyBytes`, `Notice`) have safe defaults.

### How the API reaches the issuer

The API validates tokens against the discovery document at `<Authority>/.well-known/openid-configuration`, and `Authority` must be the **public** issuer URL (it is also what the browser is told). Inside the cluster there are three ways to make that URL reachable from the API pod:

1. **Through the public name (default of the manifest).** The pod resolves `config-demo.andradev.io` like any client and goes out through Cloudflare. It needs outbound internet from the pod and the tunnel route to be up; the discovery document and keys are cached and refreshed periodically, so a brief outage is tolerated after the first successful fetch.
2. **`hostAliases` to the ingress.** Add `hostAliases: [{ip: <ClusterIP of the traefik-external Service>, hostnames: [config-demo.andradev.io]}]` to the API pod: the traffic stays in the cluster over TLS (the wildcard certificate matches). The IP changes if that Service is recreated.
3. **A separate metadata address** (needs a small API change that does not exist yet: an `Auth:Oidc:MetadataAddress` such as `http://config-demo-idp:9000/idp/.well-known/openid-configuration` while keeping `Authority` public). Best option once available; setting `Authority` to the internal URL instead is not an option because the SPA would then be sent to it.

The local compose avoids all of this by sharing one network namespace between web, api and idp.

## Run it locally

Needs Docker (or Podman) and the `demo-data/` folder at the repository root.

```bash
docker compose -f deploy/docker-compose.demo.yaml up --build
# http://localhost:8080  ->  redirected to the persona picker, then back to the app
docker compose -f deploy/docker-compose.demo.yaml down
```

Without containers (three terminals): `npm --prefix tools/demo-idp start`, the API with `Demo__*` and the settings above pointing at `http://localhost:9000/idp` (`RequireHttpsMetadata=false`), and `npm --prefix src/Web run dev` (UI on 5173 is already in the IdP's default redirect URIs).

## Deploy to the cluster

1. **Images.** The `demo-images` workflow (`.github/workflows/demo-images.yml`) builds and pushes the three images when a version tag is pushed (`git tag v0.1.0 && git push origin v0.1.0`); pull requests only build them to validate the Dockerfiles, and pushes to `main` publish nothing: `ghcr.io/andradev-io/config-server-{api-demo,web,demo-idp}` (always) and `andradevio/config-server-{api-demo,web,demo-idp}` on Docker Hub (only when the secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` exist (repository or organization level)). Tags: `x.y.z`, `x.y`, `sha-<short>` and `latest` (not for pre-releases such as `v1.0.0-rc.1`). Make the GHCR packages public (Package settings > Change visibility), otherwise create a pull secret in `config-demo` and uncomment the `imagePullSecrets` lines in the manifest.
   To build by hand instead: `deploy/build.sh ghcr.io/andradev-io 0.1.0` (or `deploy/build.ps1`), then push.
2. **Apply** (the manifest points at `ghcr.io/andradev-io/...:latest`; pin a tag or sha for anything long-lived):

   ```bash
   kubectl apply -f deploy/k8s/config-demo.yaml
   kubectl -n config-demo rollout restart deploy   # pick up a new :latest
   kubectl -n config-demo get pods
   ```

3. **Expose it.** In Cloudflare Zero Trust > Networks > Tunnels > `homelab` > Published application routes add `config-demo.andradev.io`, either to `https://traefik-external.traefik-external.svc.cluster.local:443` with Origin Server Name and HTTP Host Header `config-demo.andradev.io` (uses the Ingress, like `auth.lotties.app`), or straight to `http://config-demo-web.config-demo.svc.cluster.local:8080` (like the snake game). Never route the tunnel to the api or idp Services.
4. **Check**: `https://config-demo.andradev.io/health` answers `Healthy`, `/idp/.well-known/openid-configuration` shows `https://config-demo.andradev.io/idp` as issuer, and signing in as each persona works.

Change the host name everywhere it appears in the manifest (`Auth__Oidc__Authority`, `Demo__TokenEndpoint`, `DEMO_IDP_ISSUER`, `DEMO_IDP_REDIRECT_URIS`, the Ingress) if you use another one: the issuer must be identical in the IdP, the API and what the browser uses.

## Operating it

- **Reset everything**: `kubectl -n config-demo rollout restart deploy/config-demo-api deploy/config-demo-idp` (workspaces vanish with the pod; the IdP restart signs everybody out). The API alone also cleans idle workspaces by itself.
- **Update the sample data**: edit `demo-data/`, rebuild the API image, roll it out. Existing workspaces keep their old copy until they expire or the pod restarts.
- **Roll back one piece**: `kubectl -n config-demo rollout undo deploy/config-demo-web` (or `-api`, `-idp`).
- **Logs**: `kubectl -n config-demo logs deploy/config-demo-api` (workspace creation/deletion at Information, never request bodies or secrets).
- **Capacity**: one replica of each, tiny requests (see the manifest). `Demo__MaxWorkspaces` and the per-workspace limits bound disk and memory; the `emptyDir` has a size limit so a full disk evicts the pod instead of the node.
- Single replica is a hard rule while workspaces, sessions and signing keys live in process memory or on an `emptyDir`.

## Security notes

- **What is public**: the whole site, the sign-in page (anyone can become Admin of their own throwaway workspace) and the API behind it. That is the point of the sandbox; it is why the data is fake and why nothing may be shared between workspaces.
- **No real secrets, ever**: the sample data holds only invented values, the manifest has no Secret, the IdP has no password store, and the API refuses a real identity provider in Demo mode. Visitors are told not to enter real secrets (banner and sign-in page).
- **Isolation**: the API derives the workspace from the token `sub` (or from the owning workspace of an API key) and applies per-workspace size, file-count and key-count limits. The seed is read-only.
- **Abuse limits**: nginx rate-limits per visitor address (`CF-Connecting-IP` when present), the API rate-limits per visitor, the IdP limits sign-ins per address (30 per 10 minutes by default), request bodies are capped, and the pods run non-root with a read-only root filesystem and no service-account token. The NetworkPolicies keep api and idp reachable only through nginx (effective only if the CNI enforces them).
- **TLS** ends at Cloudflare/Traefik; the cookies of the IdP are `Secure` because nginx forwards `X-Forwarded-Proto` and the IdP trusts it (`DEMO_IDP_TRUST_PROXY=true`). The CSP allows only same-origin scripts, connections and frames.
- Tokens live 15 minutes and are renewed with refresh tokens; the IdP signing keys are generated at every start.
