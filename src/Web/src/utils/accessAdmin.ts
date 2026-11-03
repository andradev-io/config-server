import type {
  AccessGroup,
  AccessUser,
  GrantablePermission,
  GrantRequest,
  SubjectGrant,
  SubjectType,
} from '../api/access'
import { ANY, ROLE_PREFIX, type GrantPermission } from '../auth/grants'

/**
 * Pure helpers of the Access screen (identity administration): role preview, plain-language summaries,
 * source labels, revoke rules and the grant dialog form. Roles are `config:<perm>[:<app>[:<env>]]`;
 * "*" (or an omitted segment) means all.
 */

export const APPLY_NOTICE = "Changes apply at the user's next sign-in or token refresh"
export const PAGE_SIZE = 50
export const SEARCH_DEBOUNCE_MS = 300

const RANK: Record<GrantPermission, number> = { read: 1, write: 2, admin: 3 }

export const isAllScope = (v: string | null | undefined) => !v || v === ANY

/** Table / chip label: "*" shown as "all". */
export const scopeLabel = (v: string | null | undefined) => (isAllScope(v) ? 'all' : (v as string))

export interface GrantScope {
  permission: GrantPermission
  app?: string | null
  environment?: string | null
}

/** The role a grant maps to: trailing "all" segments are dropped, an app-wide "all" before an environment is `*`. */
export function rolePreview(g: GrantScope): string {
  const app = isAllScope(g.app) ? ANY : (g.app as string)
  const env = isAllScope(g.environment) ? ANY : (g.environment as string)
  if (g.permission === 'admin') return `${ROLE_PREFIX}admin`
  const parts: string[] = [g.permission]
  if (env !== ANY) parts.push(app, env)
  else if (app !== ANY) parts.push(app)
  return ROLE_PREFIX + parts.join(':')
}

const VERB: Record<GrantPermission, string> = {
  read: 'Can read',
  write: 'Can read and write',
  admin: 'Can administer',
}

/** "Can read every environment of billing-service", "Can read and write the dev environment of every application". */
export function grantSummary(g: GrantScope): string {
  if (g.permission === 'admin') return 'Full access to every application and environment, plus administration'
  const app = isAllScope(g.app) ? 'every application' : (g.app as string)
  const env = isAllScope(g.environment) ? 'every environment' : `the ${g.environment} environment`
  return `${VERB[g.permission]} ${env} of ${app}`
}

/** What revoking the grant takes away (confirm dialog). */
export function revokeConsequence(g: GrantScope, type: SubjectType): string {
  const what = grantSummary(g).replace(/^Can /, '')
  return type === 'group'
    ? `Members will no longer be able to ${what} through this group, unless another grant covers it.`
    : `The user will no longer be able to ${what}, unless another grant covers it.`
}

/** "direct", or "via <group>" for a grant inherited from a group. */
export const sourceLabel = (g: Pick<SubjectGrant, 'inherited' | 'via'>) =>
  g.inherited ? `via ${g.via?.trim() || 'a group'}` : 'direct'

/** Why a grant cannot be revoked here (tooltip), or null when it can. */
export function revokeDenial(g: Pick<SubjectGrant, 'permission' | 'inherited' | 'via'>, type: SubjectType): string | null {
  if (g.inherited) {
    const from = g.via?.trim() ? `group ${g.via.trim()}` : 'a group'
    return type === 'user'
      ? `Inherited from ${from}. Revoke it on that group or remove the user from it in the identity provider.`
      : `Inherited from ${from}. Revoke it on that group.`
  }
  if (g.permission === 'admin') return 'config:admin is managed in the identity provider, not here.'
  return null
}

/** Strongest first, then all-applications / all-environments first, then by name; direct before inherited. */
export function sortGrants(list: readonly SubjectGrant[]): SubjectGrant[] {
  const wildFirst = (a: string, b: string) => (a === b ? 0 : a === ANY ? -1 : b === ANY ? 1 : a.localeCompare(b))
  return [...list].sort(
    (a, b) =>
      RANK[b.permission] - RANK[a.permission] ||
      wildFirst(a.app, b.app) ||
      wildFirst(a.environment, b.environment) ||
      Number(a.inherited) - Number(b.inherited) ||
      (a.via ?? '').localeCompare(b.via ?? ''),
  )
}

/** Stable row key: the same role can be direct and inherited from several groups. */
export const grantKey = (g: SubjectGrant) => `${g.role}|${g.inherited ? `via:${g.via ?? ''}` : 'direct'}`

// ---------------------------------------------------------------- grant dialog

export interface GrantForm {
  permission: GrantablePermission
  /** "*" = all applications. */
  app: string
  /** "*" = all environments. */
  environment: string
}

export const emptyGrantForm = (): GrantForm => ({ permission: 'read', app: ANY, environment: ANY })

/** Request body: "all" scopes are left out (the server defaults them to all). */
export function buildGrantRequest(f: GrantForm): GrantRequest {
  const req: GrantRequest = { permission: f.permission }
  if (!isAllScope(f.app)) req.app = f.app.trim()
  if (!isAllScope(f.environment)) req.environment = f.environment.trim()
  return req
}

/** Shown when the form would give write on every application. */
export function broadWriteWarning(f: GrantForm): string | null {
  if (f.permission !== 'write' || !isAllScope(f.app)) return null
  return isAllScope(f.environment)
    ? 'This gives write access to every application and environment. Prefer a grant per application.'
    : `This gives write access to the ${f.environment} environment of every application. Prefer a grant per application.`
}

const matches = (granted: string, wanted: string) => granted === ANY || granted === wanted

/** An existing grant (direct first) that already gives the form's permission on its whole scope, or null. */
export function coveringGrant(existing: readonly SubjectGrant[], f: GrantForm): SubjectGrant | null {
  const app = isAllScope(f.app) ? ANY : f.app
  const env = isAllScope(f.environment) ? ANY : f.environment
  const hits = existing.filter(
    (g) => RANK[g.permission] >= RANK[f.permission] && matches(g.app, app) && matches(g.environment, env),
  )
  return hits.find((g) => !g.inherited) ?? hits[0] ?? null
}

/** The subject already has exactly this grant directly (adding it again would change nothing). */
export function isDuplicateGrant(existing: readonly SubjectGrant[], f: GrantForm): boolean {
  const app = isAllScope(f.app) ? ANY : f.app
  const env = isAllScope(f.environment) ? ANY : f.environment
  return existing.some((g) => !g.inherited && g.permission === f.permission && g.app === app && g.environment === env)
}

// ---------------------------------------------------------------- subjects

export const userTitle = (u: AccessUser) => u.displayName.trim() || u.username

/** `username · email` (the username only when the title is the display name). */
export const userSubtitle = (u: AccessUser) =>
  [u.displayName.trim() ? u.username : '', u.email].filter((x) => !!x).join(' · ')

/** The group path when it says more than the name (subgroups). */
export const groupSubtitle = (g: AccessGroup) => (g.path && g.path !== `/${g.name}` ? g.path : '')

export const subjectNoun = (type: SubjectType) => (type === 'user' ? 'user' : 'group')

/** A user or group as the subject list shows it. */
export interface SubjectItem {
  type: SubjectType
  id: string
  title: string
  subtitle: string
}
export const userItem = (u: AccessUser): SubjectItem => ({ type: 'user', id: u.id, title: userTitle(u), subtitle: userSubtitle(u) })
export const groupItem = (g: AccessGroup): SubjectItem => ({ type: 'group', id: g.id, title: g.name, subtitle: groupSubtitle(g) })
export const sameSubject = (a: SubjectItem | null | undefined, b: SubjectItem | null | undefined) =>
  !!a && !!b && a.type === b.type && a.id === b.id

export const grantedMessage = (role: string, type: SubjectType, name: string) =>
  `Granted ${role} to ${subjectNoun(type)} ${name}. ${APPLY_NOTICE}.`
export const revokedMessage = (role: string, type: SubjectType, name: string) =>
  `Revoked ${role} from ${subjectNoun(type)} ${name}. ${APPLY_NOTICE}.`

/** Appends a page of results; `hasMore` while a page comes back full. */
export function mergePage<T extends { id: string }>(prev: readonly T[], page: readonly T[], first: number, size = PAGE_SIZE) {
  const base = first > 0 ? [...prev] : []
  const seen = new Set(base.map((x) => x.id))
  return { items: [...base, ...page.filter((x) => !seen.has(x.id))], hasMore: page.length >= size }
}

export const providerLabel = (p: string) => (p === 'keycloak' ? 'Keycloak' : p || 'none')

/** Theme color names (Vuetify `color` prop), as the user menu and API keys use them. */
export const GRANT_COLOR: Record<GrantPermission, string | undefined> = { read: undefined, write: 'neutral', admin: 'neutral' }

/** Role format reference for the "not configured" state. */
export const ROLE_FORMATS: readonly { role: string; meaning: string }[] = [
  { role: 'config:read', meaning: 'Read every application and environment' },
  { role: 'config:write:<app>', meaning: 'Read and write every environment of one application' },
  { role: 'config:write:<app>:<env>', meaning: 'Read and write one environment of one application' },
  { role: 'config:read:*:<env>', meaning: 'Read one environment of every application' },
  { role: 'config:admin', meaning: 'Global administrator (API keys, access); global only' },
]
