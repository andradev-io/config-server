/**
 * Scoped access grants (`GET api/auth/me` -> `grants`). A grant gives one permission on one application and
 * one environment; either can be "*" (all). Hierarchy: admin (global only) > write > read. Application and
 * environment names match exactly, like the server does for user roles (only API keys ignore environment case,
 * and the SPA never signs in with one).
 */

export type GrantPermission = 'read' | 'write' | 'admin'

export interface Grant {
  permission: GrantPermission
  app: string
  environment: string
}

export type Grants = readonly Grant[] | null | undefined

export const ANY = '*'
export const GRANT_PERMISSIONS: readonly GrantPermission[] = ['read', 'write', 'admin']
const RANK: Record<GrantPermission, number> = { read: 1, write: 2, admin: 3 }

const isPermission = (v: unknown): v is GrantPermission => GRANT_PERMISSIONS.includes(v as GrantPermission)
const nonEmpty = (v: unknown): v is string => typeof v === 'string' && v.length > 0

/** A grant from the server body (permission case-insensitive), or null when malformed. */
export function toGrant(v: unknown): Grant | null {
  if (!v || typeof v !== 'object' || Array.isArray(v)) return null
  const o = v as Record<string, unknown>
  const permission = typeof o.permission === 'string' ? o.permission.toLowerCase() : null
  if (!isPermission(permission) || !nonEmpty(o.app) || !nonEmpty(o.environment)) return null
  return { permission, app: o.app, environment: o.environment }
}

export const isGrant = (v: unknown): boolean => toGrant(v) !== null

export const ROLE_PREFIX = 'config:'

/**
 * Parses a token role `config:<perm>[:<app>[:<env>]]` (missing segments = "*"). Admin is global only, so a
 * scoped admin role is ignored, like anything that is not a config role. Case-sensitive: only a fallback for
 * older servers (current ones send `grants`, parsed server-side).
 */
export function grantFromRole(role: string): Grant | null {
  if (typeof role !== 'string' || !role.startsWith(ROLE_PREFIX)) return null
  const parts = role.slice(ROLE_PREFIX.length).split(':')
  if (parts.length > 3) return null
  const [permission, app = ANY, environment = ANY] = parts
  if (!isPermission(permission) || !app || !environment) return null
  if (permission === 'admin' && (app !== ANY || environment !== ANY)) return null
  return { permission, app, environment }
}

/** Fallback for servers that only return `roles`. */
export const grantsFromRoles = (roles: readonly string[] | null | undefined): Grant[] =>
  (roles ?? []).map(grantFromRole).filter((g): g is Grant => g !== null)

/** The caller's grants: the server's list, or the ones derived from the roles when it has none. */
export const effectiveGrants = (me: { roles: readonly string[]; grants?: readonly Grant[] } | null | undefined): Grant[] =>
  me ? [...(me.grants ?? grantsFromRoles(me.roles))] : []

const matches = (granted: string, wanted: string) => granted === ANY || granted === wanted
const enough = (g: Grant, p: GrantPermission) => RANK[g.permission] >= RANK[p]

/**
 * True when a grant gives `permission` (or a higher one) on the whole scope asked for. An omitted (or "*")
 * application / environment means every application / environment, so only a "*" grant covers it.
 */
export function can(grants: Grants, permission: GrantPermission, app: string = ANY, env: string = ANY): boolean {
  return !!grants && grants.some((g) => enough(g, permission) && matches(g.app, app) && matches(g.environment, env))
}

/**
 * True when a grant gives `permission` on at least part of the scope: the given application / environment
 * must be covered, an omitted one can be anything (e.g. "may write some environment of `app`").
 */
export function canSomewhere(grants: Grants, permission: GrantPermission, scope: { app?: string; env?: string } = {}): boolean {
  return (
    !!grants &&
    grants.some(
      (g) =>
        enough(g, permission) &&
        (scope.app === undefined || matches(g.app, scope.app)) &&
        (scope.env === undefined || matches(g.environment, scope.env)),
    )
  )
}

export const isGlobalAdmin = (grants: Grants) => can(grants, 'admin')
export const isGlobalWriter = (grants: Grants) => can(grants, 'write')
export const isGlobalReader = (grants: Grants) => can(grants, 'read')
/** Any grant at all (every grant implies read somewhere). */
export const hasAnyAccess = (grants: Grants) => canSomewhere(grants, 'read')
/** A grant covers every application (for at least some environment). */
export const readsAllApps = (grants: Grants) => canSomewhere(grants, 'read', { app: ANY })

export const canReadApp = (grants: Grants, app: string) => canSomewhere(grants, 'read', { app })
export const canReadEnv = (grants: Grants, app: string, env: string) => can(grants, 'read', app, env)
/** An environment readable in at least one application. */
export const canReadEnvSomewhere = (grants: Grants, env: string) => canSomewhere(grants, 'read', { env })
/** Write on every environment of the application (`app,*`): default file, declared variables. */
export const canWriteApp = (grants: Grants, app: string) => can(grants, 'write', app, ANY)
export const canWriteEnv = (grants: Grants, app: string, env: string) => can(grants, 'write', app, env)
/** Write on at least one environment of the application. */
export const canWriteSomeEnv = (grants: Grants, app: string) => canSomewhere(grants, 'write', { app })

/** The coarse flags (navigation, legacy checks): read/write anywhere, admin = global admin. */
export interface GrantFlags {
  read: boolean
  write: boolean
  admin: boolean
}
export const flagsOf = (grants: Grants): GrantFlags => ({
  read: hasAnyAccess(grants),
  write: canSomewhere(grants, 'write'),
  admin: isGlobalAdmin(grants),
})

// ---------------------------------------------------------------- summary (user menu)

const covers = (h: Grant, g: Grant) => enough(h, g.permission) && matches(h.app, g.app) && matches(h.environment, g.environment)
const same = (a: Grant, b: Grant) => a.permission === b.permission && a.app === b.app && a.environment === b.environment
const wildFirst = (a: string, b: string) => (a === ANY ? -1 : b === ANY ? 1 : a.localeCompare(b))

/** Drops duplicates and grants implied by another one; strongest first, then by application and environment. */
export function compactGrants(grants: Grants): Grant[] {
  const list = [...(grants ?? [])]
  return list
    .filter((g, i) => !list.some((h, j) => j !== i && covers(h, g) && (!same(h, g) || j < i)))
    .sort(
      (a, b) =>
        RANK[b.permission] - RANK[a.permission] || wildFirst(a.app, b.app) || wildFirst(a.environment, b.environment),
    )
}

/** `write · billing · dev`; an "all environments" grant drops the environment (`read · *`, `write · billing`). */
export const grantLabel = (g: Grant): string =>
  [g.permission, g.app, ...(g.environment === ANY ? [] : [g.environment])].join(' · ')
