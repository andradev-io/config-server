# config-server

<p align="center"><img src="src/Web/public/favicon.svg" alt="config-server" width="72"></p>

A configuration server. It keeps **one default config per application** plus **small per-environment overrides** (only the fields that change). A client asks for an environment and receives the default merged with that override. It replaces manual, per-environment variable substitution in CI/CD releases.

- **Formats:** JSON, YAML and `.env`, mixed freely; the response can be rendered as JSON, YAML or `.env`.
- **Deep merge:** objects merge recursively, arrays and scalars are replaced, the override wins.
- **Management UI** (Vue 3 + Vuetify): view the merged result, compare environments, edit files, manage environments and declared variables, and an Azure DevOps-style **variables grid** (one row per key, one column per environment).
- **Authentication:** any OIDC provider (JWT bearer), plus hashed API keys for pipelines. Machine credentials can also be created in the identity provider from the UI ("OIDC API keys").
- **RBAC controlled by your identity provider:** grants per application and environment come from client roles in the token (`config:read|write|admin[:app[:env]]`). Admins can manage them from the UI through the provider's admin API (Keycloak first).
- **Public sandbox:** a demo mode where every visitor gets a private, disposable workspace.

> Early stage. Not implemented yet: SOPS/secret resolution, and the Git-backed source is not wired into the API (the API reads a directory).

## Quick start

Requirements: .NET 10 SDK and Node 22+.

```bash
# API on http://localhost:5000 (Development: no authentication, writes allowed)
dotnet run --project src/Application/ConfigServer.Application.Api --ConfigSource:Path=/path/to/config-files

# UI on http://localhost:5173
npm --prefix src/Web install
npm --prefix src/Web run dev
```

Config files live in a folder with this layout:

```
<ConfigSource:Path>/
  _environments.json          # optional environment registry
  billing-service/
    default.yaml              # the default config
    dev.json                  # override for "dev" (only the changed fields)
    prd.env                   # override for "prd" (formats can differ)
    _variables.json           # optional declared variables (type, required, secret)
```

Ask for a merged config:

```bash
curl "http://localhost:5000/config/billing-service/prd?format=yaml"
```

`demo-data/` is a ready-made example folder you can point `ConfigSource:Path` at.

## Authentication and access

Production and Docker require OIDC (`Auth:Mode=Oidc`, `Auth:Oidc:Authority`, `ClientId`). Copy `.env.example` to `.env` (git-ignored) for local values; names use `__` for sections (`Auth__Oidc__Authority`). Setting up Keycloak, including the admin service account used by the Access screen: [docs/keycloak-setup.md](docs/keycloak-setup.md).

## Public sandbox

How the demo works, how to run it locally with Docker and how to deploy it to Kubernetes: [docs/demo.md](docs/demo.md). Images are published to `ghcr.io/andradev-io/config-server-*` and `andradevio/config-server-*` on Docker Hub.

## Develop

```bash
dotnet build src/ConfigServer.sln
dotnet test src/ConfigServer.sln
npm --prefix src/Web run test
```

Architecture notes, API contracts and gotchas are in [CLAUDE.md](CLAUDE.md). `tools/dev-idp` is a small local OIDC provider for testing sign-in without Keycloak.

## License

See [LICENSE](LICENSE).
