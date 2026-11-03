import type { CreateOidcKeyRequest, OidcKey, OidcKeyGrant, OidcKeyGrantRequest, OidcKeyPermission } from '../api/oidcKeys'
import { ALL } from '../api/keys'
import { isAll } from './keys'

/** Same limits as the server (`MachineClients`). */
export const OIDC_NAME_MAX = 100
export const OIDC_MAX_GRANTS = 50

export function validateOidcName(name: string): string | null {
  const n = name.trim()
  if (!n) return 'Required'
  if (n.length > OIDC_NAME_MAX) return `At most ${OIDC_NAME_MAX} characters`
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f-\u009f]/.test(n)) return 'No control characters'
  return null
}

export interface OidcKeyForm {
  name: string
  permission: OidcKeyPermission
  apps: string[]
  environments: string[]
}

export const emptyOidcKeyForm = (): OidcKeyForm => ({ name: '', permission: 'read', apps: [ALL], environments: [ALL] })

/** The apps x environments pairs the form stands for ("all" = one wildcard entry). */
export function formGrants(f: Pick<OidcKeyForm, 'permission' | 'apps' | 'environments'>): OidcKeyGrantRequest[] {
  const apps = isAll(f.apps) ? [ALL] : [...new Set(f.apps)]
  const envs = isAll(f.environments) ? [ALL] : [...new Set(f.environments)]
  const grants: OidcKeyGrantRequest[] = []
  for (const app of apps)
    for (const environment of envs) {
      const g: OidcKeyGrantRequest = { permission: f.permission }
      if (app !== ALL) g.app = app
      if (environment !== ALL) g.environment = environment
      grants.push(g)
    }
  return grants
}

/** Null when the selection fits the server limit; otherwise what to narrow. */
export function grantLimitError(f: Pick<OidcKeyForm, 'permission' | 'apps' | 'environments'>): string | null {
  const n = formGrants(f).length
  return n > OIDC_MAX_GRANTS
    ? `This selection makes ${n} grants (at most ${OIDC_MAX_GRANTS}). Choose fewer applications or environments, or use All.`
    : null
}

export function buildOidcRequest(f: OidcKeyForm): CreateOidcKeyRequest {
  return { name: f.name.trim(), grants: formGrants(f) }
}

/** `write · billing / prod`; "*" shown as "all". */
export function grantLabel(g: Pick<OidcKeyGrant, 'permission' | 'app' | 'environment'>): string {
  const app = g.app === ALL ? 'all apps' : g.app
  const env = g.environment === ALL ? 'all environments' : g.environment
  return `${g.permission} · ${app} / ${env}`
}

/** One-line summary shown in the create dialog before submitting. */
export function summarizeForm(f: Pick<OidcKeyForm, 'permission' | 'apps' | 'environments'>): string {
  const apps = isAll(f.apps) ? 'all applications' : f.apps.join(', ')
  const envs = isAll(f.environments) ? 'all environments' : f.environments.join(', ')
  return `${f.permission} access to ${apps} in ${envs}`
}

export const OIDC_STATUS_COLOR: Record<'active' | 'disabled', string> = { active: 'success', disabled: 'warning' }
export const oidcStatus = (k: Pick<OidcKey, 'enabled'>): 'active' | 'disabled' => (k.enabled ? 'active' : 'disabled')

/** Newest first, then by name; search matches name or client id (case-insensitive). */
export function filterOidcKeys(keys: readonly OidcKey[], search = ''): OidcKey[] {
  const q = search.trim().toLowerCase()
  const time = (iso: string | null) => {
    const t = iso ? Date.parse(iso) : NaN
    return Number.isNaN(t) ? 0 : t
  }
  return keys
    .filter((k) => !q || k.name.toLowerCase().includes(q) || k.clientId.toLowerCase().includes(q))
    .sort((a, b) => time(b.createdAt) - time(a.createdAt) || a.name.localeCompare(b.name))
}

/** Shell-safe single-quoted value. */
const sq = (v: string) => `'${v.replace(/'/g, `'\\''`)}'`

/** Ready-to-copy requests: get a token (client credentials), then call the API with it as a Bearer. */
export function oidcUsageSnippet(
  secret: string,
  clientId: string,
  tokenEndpoint: string | null,
  baseUrl: string,
  grants: readonly Pick<OidcKeyGrant, 'app' | 'environment'>[],
): string {
  const first = grants[0]
  const app = !first || first.app === ALL ? '<app>' : first.app
  const env = !first || first.environment === ALL ? '<env>' : first.environment
  const endpoint = tokenEndpoint || '<token endpoint of your identity provider>'
  return [
    `TOKEN=$(curl -s ${sq(endpoint)} \\`,
    `  -d grant_type=client_credentials \\`,
    `  -d client_id=${sq(clientId)} \\`,
    `  -d client_secret=${sq(secret)} | jq -r .access_token)`,
    '',
    `curl -H "Authorization: Bearer $TOKEN" ${baseUrl.replace(/\/+$/, '')}/config/${app}/${env}`,
  ].join('\n')
}
