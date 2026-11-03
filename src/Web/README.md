# Config Server Web

Vue 3 (`<script setup>`, TypeScript) + Vuetify 3 + Vite UI for the config server, with CodeMirror 6 editors.

- **Layout**: app bar (hamburger, VA logo + name, global Application / Environment selectors, theme toggle, user menu)
  and a left navigation drawer with three groups: **Configuration** (View, Compare, Editor), **Management**
  (Environments, Variables, Overrides) and **Access** (API Keys and Access, global `config:admin` only), after a top-level **Overview**
  entry (the default screen). On desktop the drawer collapses to a rail (hamburger; remembered in localStorage); below
  960px it is a temporary drawer and the selectors move to a second app-bar row. No router: each screen mounts on
  first visit and stays alive (state kept). Every screen uses the same `PageHeader` (title, subtitle, actions).
- **Global context** (`src/composables/useContext.ts`): the selected application (`api/apps`, autocomplete, clearable)
  and environment (`api/environments`, in declared order) are shared by every screen and persisted in localStorage
  (`config-server-context`, read/write wrapped in try/catch). On load the stored app is dropped if it no longer exists
  and an unknown environment falls back to the first one (with scoped grants: see "Context and grants" below). Screens with unsaved edits (Editor file, Editor > Variables,
  Overrides) register themselves: switching the application then asks "Discard and switch" / "Keep editing".
  App-scoped screens show the application list as chips while no application is selected.
- **Environment colours**: each environment has a stable colour (`src/utils/envColor.ts`: fixed slots for
  dev/stg/prd and their aliases, FNV-1a hash of the name into the other slots; Vuetify theme colours `env-0..7`),
  used by environment chips/dots (selectors, Overview, Environments, Compare, Editor file list, API key scopes) and
  the Overrides / Variables matrix column headers.
- **Overview** (default): without an application, the applications as cards (name, number of environments = override
  files, environment chips). With one, a card per environment from `GET api/apps/{app}/status`: ok / missing /
  issues chip, override file in use (or "no override file"), counts of missing required keys, type mismatches and
  secrets in plain text (with the keys), errors, and quick actions: open Overrides, open the environment's file in
  the Editor (or the New file dialog prefilled when it does not exist) and view the merged config for that
  environment (each also selects the environment). Undeclared variables are listed with a "Declare in Editor" link.
  Refreshes each time the screen is shown.
- **View**: the merged config of the context application + environment in a read-only CodeMirror, loaded
  automatically (and on app/environment/format change), format toggle (json | yaml | env, `?format=`), Copy, Refresh.
- **Compare**: diff two environments of the context application (JSON tree with diff highlighting + table of
  differences). The baseline follows the context environment, the target defaults to the next environment in order;
  both stay editable, with a Swap button. Compares automatically when both are set.
- **Editor** (advanced): create/edit/delete config files (`.json`, `.yaml`, `.yml`, `.env`) of the context
  application, with client-side lint, server validation errors (400 `{error,line,column}` shown in an alert and as a
  lint marker), Save (Ctrl/Cmd+S), Revert, delete confirmation, unsaved-changes warning, and a collapsible Preview of
  the merged result (refreshed after each save; its environment follows the context environment). On phones the
  file list becomes a select.

- **Environments**: table of environments (name, description, order, app count, declared/implicit chip), a
  "New environment" dialog (naming rule `[A-Za-z0-9][A-Za-z0-9_-]*`, description, order, apps to scaffold override
  files in, optional "copy from" environment), edit description/order, and delete with a confirm dialog and an
  "Also delete the override files in all apps" checkbox (off by default).
- **Variables**: cross-project index of required variables (debounced search, app/environment filters, "declared
  only" switch, chips per app coloured by undeclared/secret/required/optional with a legend). Clicking a row opens
  "Usage of <key>" (per-app flags, defining files, environment matrix defined/inherited/missing). Collapsible
  indexing warnings, "Copy key" and "Export CSV" (key, app, required, secret, files, environments; never values).
- **Editor > Variables panel** (collapsible, above the editor, for the selected app): editable table of declared
  variables (key, type, required, secret, environments where empty = all, description) with add/remove, Save
  (server validation errors shown), and a status matrix (one chip per environment: ok / N missing, expanding to
  missing keys, type mismatches, secrets in plain text) plus the undeclared keys with a "Declare" button. Status
  refreshes after saving/deleting a config file or the variables. `_environments.json` and `_variables.json` are
  hidden from the file list and rejected as new file names.

- **Overrides** (central working screen): Azure DevOps Release style grid for the context application. Rows = variables (dotted keys, type shown),
  padlock column = secret (display only; change the flag in Editor > Variables, linked from the legend), columns =
  scopes (`default` first, then environments; environment headers carry the environment colour and the context
  environment's column header is tinted). A cell shows its override value; an empty cell inherits and shows the
  effective value dimmed with a small arrow ("from default"). Secrets show `••••••` wherever a value exists and are
  never revealed; editing sets a new value. Click a cell to edit inline (Enter commits, Esc cancels, Tab commits and
  moves right, Shift+Tab left). Scalars are parsed by the variable type (number/boolean validated, string kept as is;
  an empty string is a valid value, "Clear" (x) is the only way to go back to inherit). Arrays/objects open a JSON
  dialog with CodeMirror. Changed cells use the theme warning color until saved (undo button per cell), type
  mismatches show an error outline. Header actions "Add variable" (key, scope, value, optional type) and "Reload"; toolbar search and
  "Only overridden". While there are pending changes a bar sticks to the bottom of the content area with the count,
  "Discard" and "Review". Review calls PUT with `dryRun=true`, shows a review dialog
  (files to create/modify, changed keys, server warnings), then PUT with `dryRun=false` and `ifMatch=etag`, then
  reloads. A 409 offers "Reload" (keeps pending changes that still apply). Unsaved changes ask for confirmation when
  switching app (app bar), reloading, discarding, leaving the screen, or closing the page. The grid scrolls horizontally with a
  sticky first column on phones.

- **Sign-in** (`src/auth/`): at startup the SPA reads `GET api/auth/info`. Mode `none`: no login, no token. Mode
  `oidc`: generic OpenID Connect via `oidc-client-ts` (authorization code + PKCE, built only from
  `{authority, clientId, scopes}`; `openid` is always requested; no provider-specific code). Unauthenticated users see
  a "Sign in" screen; the app bar has a user menu (name, access summary, roles, Sign out) in mode `oidc`. The redirect callback
  (`?code=&state=` / `?error=&state=`) is handled on load and stripped from the URL (no router). User and PKCE state
  live in `sessionStorage` (never `localStorage`). Tokens renew silently (refresh token when the IdP issues one,
  otherwise a hidden iframe that loads the SPA itself). Every API call goes through `authorizedFetch`
  (`src/api/http.ts`), which adds `Authorization: Bearer <access_token>`; on 401 it tries one renew (shared by
  concurrent calls) and retries once, else shows the sign-in screen (views stay mounted behind it, so pending edits are
  kept until the redirect). Sign out uses RP-initiated logout when the IdP supports it, otherwise drops the local
  session. A server without `api/auth/*` (404) is treated as mode `none` with full access.
- **Grants** (`src/auth/grants.ts`): `GET api/auth/me` returns `grants:[{permission:read|write|admin, app, environment}]`
  (`*` = all); without `grants` (older server) they are derived from the roles `config:<perm>[:<app>[:<env>]]`
  (missing segments = `*`; admin is global only, a scoped admin role is ignored; mode `none` = global admin). Write
  implies read, global admin implies everything; app and environment names match exactly (as the server does for
  user roles). Pure helpers:
  `can(grants, perm, app?, env?)` (omitted = `*`, only a wildcard grant covers it), `canSomewhere`, `canWriteApp`
  (`app,*`), `canWriteEnv`, `isGlobalAdmin`, `isGlobalWriter`, `grantsFromRoles`, `effectiveGrants`,
  `compactGrants` / `grantLabel` (summary). `useAuth()` exposes `grants`; `permissions` (`read`/`write` somewhere,
  `admin` = global admin) drives the drawer: the API Keys and Access entries exist only for a global admin
  (`src/utils/nav.ts`).
  `src/auth/roles.ts` keeps `canRead`/`canWrite`/`canAdmin`/`permissionsOf` on top of the grants.
- **Grants-aware UI** (`src/utils/access.ts`, each `...Denial` returns null or the tooltip text; disabled controls
  are wrapped in `PermissionTip.vue`, a focusable tooltip wrapper): Editor save/delete per file (`default.*` needs
  write on every environment of the app, `{env}.*` on that environment; otherwise the editor is read-only), New file
  (write on some environment; the dialog rejects names you may not write); Editor > Variables add/declare/save
  (app-wide write); Environments new/edit/delete (global writer); Overrides per scope column (`default` needs
  app-wide write; read-only columns have a muted fill, a pencil-off icon with the reason in the header and the reason
  as cell title; Tab skips them; "Add variable" offers only writable scopes; the sticky bar counts and saves only
  allowed changes, and changes that became forbidden after a grants change are dropped); Overview "Editor" quick
  action (an existing file opens read-only, creating a missing one needs write on that environment) and "Declare in
  Editor" (app-wide write). The server still decides: its 403 `{error}` is shown as is.
- **Context and grants**: the server filters `api/apps`, `api/environments`, overrides and status; the context store
  filters again (older servers) and the environment selector / Compare only list the environments readable in the
  selected app. A persisted app the caller may no longer read falls back to the first readable app when the grants
  are scoped to specific apps (a caller who reads every app gets the picker), and the environment to the first one
  readable in it; switching app keeps the environment only if it is readable there. Grants changing (another user
  after an expired session) re-run the reconciliation. Signed in without any grant, Overview, the app-scoped
  screens, Environments and Variables show "No applications are assigned to your account — ask an administrator to
  grant access in the identity provider" instead of errors. The user menu (mode `oidc`) shows an Access summary as
  chips (`write · billing-service · dev`, `read · *`; duplicates and implied grants dropped) above the raw roles.
- **API Keys** (admin): table (name, `cfs_<prefix>_…`, permission chip, apps/environments chips with `*` shown as
  "all", status chip active/expired/revoked in the theme success/warning/error colors, created/expires/last used),
  filter by name or prefix, "Hide revoked" switch, Rotate and Revoke with confirm dialogs. "New API key": name,
  permission (read default / write), apps and environments multi-selects with an "All" option (picking All clears the
  others; an empty selection means All), optional expiry date (end of that local day). After create/rotate a
  persistent one-time dialog shows the secret and a ready-to-copy `curl -H "X-Api-Key: …" <api>/config/<app>/<env>`
  snippet, with Copy buttons and a "shown only once" warning; the secret lives only in component memory and is
  cleared when the dialog closes.
- **Access** (global admin): who has which grants, managed through the server, which drives the identity provider
  (Keycloak). Nobody types role names. Reads `GET api/access/info` first: when identity management is off
  (`enabled: false`, provider `none`, or a server without `api/access` = 404) a "not configured" state explains that
  grants are then assigned in the IdP directly, lists the role formats (`config:read`, `config:write:<app>`,
  `config:write:<app>:<env>`, `config:read:*:<env>`, `config:admin`) and points to the server's `IdentityAdmin`
  section (docs/keycloak-setup.md). When enabled: a persistent "Changes apply at the user's next sign-in or token
  refresh" notice, two tabs **Groups** (default, recommended) and **Users** with a debounced search (300 ms) and
  "Load more" paging (50 per page); selecting one shows its grants (permission chip, application, environment as an
  `EnvChip`, `*` shown as "all", source "direct" or "via <group>", sorted strongest first). Revoke asks for
  confirmation; it is disabled with a tooltip for inherited grants (change them on the group) and for `config:admin`
  (managed in the IdP). "Grant access": permission (read / write), application (autocomplete, "All applications"),
  environment ("All environments"), a live preview (summary sentence such as "Can read every environment of
  billing-service" and the resulting role), a warning for write on every application, "already has this grant"
  (Grant disabled) and "already covered by <role>" notes. Snackbars after grant / revoke repeat the sign-in notice.

## Run

```bash
cd src/Web
npm install
npm run dev      # http://localhost:5173 (the port the API's CORS allows)
npm run build    # type-check (vue-tsc) + production build into dist/
npm run test     # Vitest (diff, API clients with mocked fetch, auth/token handling, grants, access checks, access admin helpers, lint, helpers, context, nav, env colours)
```

## Configuration

| Variable            | Default                 | Description                      |
| ------------------- | ----------------------- | -------------------------------- |
| `VITE_API_BASE_URL` | `http://localhost:5000` | Base URL of the config server API. Empty or `/` = same origin (relative URLs, used by the container image behind nginx; copy-paste snippets show the page origin) |

Set it in `.env` (see `.env.example`). The `Dockerfile` (build context `src/Web`) builds with `/` and serves the UI with nginx, proxying `/api`, `/config`, `/health` and `/idp`: see `docs/demo.md`. When `api/auth/info` carries a `demo` block the app shows a dismissible sandbox banner.

Sign-in needs no build-time setting: the server publishes it in `api/auth/info`. For mode `oidc`, register a
**public** client (no secret) with authorization code + PKCE in the IdP, and add the SPA base URL (e.g.
`http://localhost:5173/`) as redirect URI, post-logout redirect URI and allowed web origin (CORS for the token
endpoint). Add `offline_access` (or the IdP's refresh-token option) to the server's scopes if the IdP only issues
refresh tokens on request; otherwise silent renew uses a hidden iframe, which third-party-cookie blocking can break.

## API used

```
GET    config/{app}/{env}?format=json|yaml|env
GET    api/apps                         -> string[]
GET    api/apps/{app}/files             -> [{name, format, size}]
GET    api/apps/{app}/files/{file}      -> text/plain
PUT    api/apps/{app}/files/{file}      body {content} -> 201 | 204 | 400 {error,line?,column?} | 403 | 409 | 501
DELETE api/apps/{app}/files/{file}      -> 204 | 404 | 409

GET    api/environments                 -> [{name,description,order,declared,appCount}]
POST   api/environments                 body {name,description?,order?,scaffold?:{apps,copyFrom?}} -> 201 | 400 | 409
PUT    api/environments/{name}          body {description,order} -> 204
DELETE api/environments/{name}?deleteFiles=false|true -> 204 | 409
GET|PUT api/apps/{app}/variables        {variables:[{key,type,required,secret,description,environments|null}]} (PUT -> 204 | 400)
GET    api/apps/{app}/status            -> {app,environments:[{name,ok,missing,typeMismatches,secretsInPlainText,error?}],undeclared}
GET    api/variables?search=&app=&env=&declaredOnly= -> {variables:[{key,type,appCount,usedBy:[...]}],warnings:[{app,file,error}]}
GET    api/variables/{key}              -> one variable; usedBy[] items add byEnvironment {env: defined|inherited|missing}; 404
GET    api/apps/{app}/overrides         -> {app,etag,scopes,variables:[{key,type,secret,declared,values:{scope:{value,format,file}|{masked,file}},effective:{env:{value,from}|{masked,from}}}],warnings}
PUT    api/apps/{app}/overrides?dryRun=true|false  body {ifMatch?,changes:[{key,scope,value?|remove:true}]}
                                        -> {files:[{name,created,changedKeys}],warnings} | 400 {error,key?,scope?} | 403 | 409 | 501

GET    api/auth/info  (anonymous)       -> {mode:none|oidc, oidc:{authority,clientId,scopes}|null, apiKeys}
GET    api/auth/me                      -> {name, roles:[config:<perm>[:<app>[:<env>]]], via:none|oidc|apikey,
                                            grants?:[{permission:read|write|admin, app, environment}]}  ("*" = all)
GET    api/access/info                  -> {provider:none|keycloak, enabled, configClientId?, note?}  (404 = disabled)
GET    api/access/users?search=&first=&max=   -> [{id,username,displayName,email}]
GET    api/access/groups?search=&first=&max=  -> [{id,name,path}]
GET    api/access/subjects/{user|group}/{id}/grants -> [{permission,app,environment,role,inherited,via?}]  ("*" = all)
POST   api/access/subjects/{user|group}/{id}/grants body {permission:read|write, app?, environment?} -> 201 grant
DELETE api/access/subjects/{user|group}/{id}/grants?permission=&app=&environment= -> 204  (scope sent as listed, "*" kept)
                                        (all admin only; 501 when identity management is not configured)
GET    api/keys                         -> [{id,name,prefix,apps,environments,permission,createdAt,expiresAt,revokedAt,lastUsedAt,status}]
POST   api/keys                         body {name,apps?,environments?,permission?,expiresAt?} -> 201 {key,secret}
DELETE api/keys/{id}                    -> 204 (revoke)
POST   api/keys/{id}/rotate             -> 200 {key,secret}
```

401 maps to kind `unauthorized` ("Sign in required."). 403 keeps "Writes are disabled on this server
(ConfigSource:AllowWrite)" when the body is empty, generic ("Forbidden") or mentions AllowWrite; another `{error}` (e.g.
a missing role) is shown as is. The keys client uses "Managing API keys requires the config:admin role." for a bare 403.
Clients: `src/auth/api.ts` (info, me), `src/api/keys.ts` (strict guards; `FilesApiError`), helpers `src/utils/keys.ts`.
Access client `src/api/access.ts` (strict guards, `subjectQuery` leaves empty search/first/max out; a bare 403 says
"Managing access requires the config:admin role.", 501 "Identity management is not configured on this server
(IdentityAdmin)."), helpers `src/utils/accessAdmin.ts` (role preview, summaries, source labels, revoke rules, grant
form, paging).

Client `src/api/overrides.ts` (strict guards), pure helpers (value parsing by type, pending change set with
set/remove/clear/dedupe/reconcile, payload building, cell views, row filtering) in `src/utils/overrides.ts`;
view `src/views/OverridesView.vue`.

The files, environments and variables endpoints above never return or accept values. Clients: `src/api/environments.ts`, `src/api/variables.ts`
(both reuse `FilesApiError` and the same status mapping through `src/api/request.ts`; variable keys are dotted
paths and each segment is URL-encoded). Pure helpers (name rule, row validation, matrix, CSV): `src/utils/variables.ts`.

Clients: `src/api/config.ts` (merged config) and `src/api/files.ts` (files; typed `FilesApiError` with kinds
`bad-request`, `forbidden`, `not-found`, `conflict`, `read-only`, `server`, `network`, `invalid-response`).
403 shows "Writes are disabled on this server (ConfigSource:AllowWrite)", 501 "This config source is read-only".

File names: `default` or `[A-Za-z0-9_-]+` plus `.json`, `.yaml`, `.yml` or `.env` (`src/utils/fileNames.ts`).
Lint (`src/utils/lint.ts`): JSON via `JSON.parse` error position, YAML via the `yaml` package, `.env` lines
that are neither blank, comments nor `KEY=value`.

## Structure

- `src/App.vue`: shell (app bar, navigation drawer, screen switching, leave/switch-app confirmations).
- `src/composables/useContext.ts`: global application/environment context (persistence helpers `parseContext`,
  `readContext`, `writeContext`, `reconcileContext`; store with `requestApp`, `setEnv`, `registerUnsaved`, `refresh`).
- `src/utils/nav.ts`: drawer entries and groups, `navSections(permissions)`, `allowedView`.
- `src/utils/envColor.ts`: `envSlot`, `envColor` (theme colour name), `envColorCss`.
- `src/utils/overview.ts`: `overrideFiles` (env -> file with the server lookup order), `orderEnvironments`,
  `summarizeEnv`, `summarizeApp`. `src/utils/alerts.ts`: alert type/title per error kind.
- `src/components/PageHeader.vue`, `ContextSelectors.vue` (app bar selectors), `EnvChip.vue`, `EnvDot.vue`,
  `NoAppState.vue` (application picker shown by app-scoped screens).
- `src/views/OverviewView.vue`, `MergedConfigView.vue` (View), `CompareView.vue`.
- `src/components/CodeEditor.vue`: CodeMirror wrapper (props `modelValue`, `language`, `readOnly`, `dark`, `markers`, `lint`; emits `update:modelValue`, `save`).
- `src/views/ConfigEditor.vue`: the Editor screen. `src/components/AppVariablesPanel.vue`: its Variables panel.
- `src/views/EnvironmentsView.vue`, `src/views/VariablesView.vue`: the Environments and Variables screens. Variables'
  application/environment filters start from (and follow) the global context; clear them to search everything.
- `src/views/ApiKeysView.vue`: the API Keys screen (admin only). `src/views/AccessView.vue`: the Access screen
  (admin only; users, groups and their grants through the identity provider).
- `src/api/http.ts`: `authorizedFetch` + `setAuthHooks` (token attach, one renew on 401). `src/api/files.ts` `send`,
  `src/api/request.ts` and `src/api/config.ts` all call it.
- `src/utils/access.ts`: per-screen permission checks (denial reasons) and the "no applications assigned" texts.
  `src/components/PermissionTip.vue`: tooltip explaining a control disabled by the grants.
- `src/auth/`: `api.ts` (info/me clients), `grants.ts` (scoped grants), `roles.ts` (role helpers), `oidcConfig.ts` (pure OIDC settings and
  callback URL helpers), `oidc.ts` (`UserManager` factory, lazy-loaded chunk), `session.ts` (reactive auth state,
  `initAuth`, `login`, `logout`, `useAuth`). `main.ts` mounts the app, then runs `initAuth`; inside the silent-renew
  iframe it only forwards the response.
- `src/theme/`: self-contained visual identity (tokens, Vuetify config, CodeMirror themes); see its README.
  Default theme is dark (toggle and choice saved in localStorage), font JetBrains Mono. Layered surfaces (background,
  surface, surface-light, surface-bright), outlined style only on editable inputs, tonal buttons, flat cards; the
  olive accent is reserved for the primary action, focus and the active navigation item.

## Notes

- Compare mode treats arrays and scalars as whole values (same as the server merge).
- Tests mock `fetch`; they do not need the backend running. Components are not unit tested (no DOM test environment);
  the shell logic they rely on (context persistence, navigation filtering, environment colours, overview summaries)
  is in pure modules with tests.
- The OIDC redirect, silent renew and logout flows are not covered by tests (they need a real IdP); only the settings,
  callback detection, token attach/renew logic and mode `none` bootstrap are. Scoped grants are covered by unit tests
  (helpers, access checks, context reset, nav) and were checked in the browser against mode `none` with a stubbed
  `api/auth/me`, not yet with scoped OIDC users. The Access screen was checked in the browser against a server without
  `api/access` (not configured state) and with a stubbed `fetch` for the enabled flow (tabs, search, grant, duplicate
  detection, revoke, inherited tooltip, 501), not yet against a real Keycloak.
