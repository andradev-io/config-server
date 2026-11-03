import type { GrantPermission } from '../auth/grants'
import { FilesApiError, type FilesClientOptions } from './files'
import { apiRoot, isObj, jsonInit, requestJson, requestVoid } from './request'

/**
 * Identity administration (admin only): the server drives the identity provider (e.g. Keycloak) to list users
 * and groups and to assign the `config:<perm>[:<app>[:<env>]]` client roles. Errors are `{error}`; when identity
 * management is not configured the info says `enabled: false` and the other endpoints answer 501.
 */

export type SubjectType = 'user' | 'group'
/** Only read and write are grantable here; admin is global and managed in the identity provider. */
export type GrantablePermission = 'read' | 'write'

export interface AccessInfo {
  /** `none` or `keycloak` (any other provider name is kept as is). */
  provider: string
  enabled: boolean
  configClientId?: string
  note?: string
  /** Public OIDC token endpoint machine credentials request tokens from. */
  tokenEndpoint?: string
}

export interface AccessUser {
  id: string
  username: string
  displayName: string
  email: string
}

export interface AccessGroup {
  id: string
  name: string
  path: string
}

/** One grant of a subject; app / environment "*" = all. Inherited grants come from a group (`via`). */
export interface SubjectGrant {
  permission: GrantPermission
  app: string
  environment: string
  role: string
  inherited: boolean
  via?: string
}

/** Omitted app / environment = all. */
export interface GrantRequest {
  permission: GrantablePermission
  app?: string
  environment?: string
}

export interface SubjectQuery {
  search?: string
  first?: number
  max?: number
}

export const ACCESS_FORBIDDEN_MESSAGE = 'Managing access requires the config:admin role.'
export const ACCESS_NOT_CONFIGURED_MESSAGE = 'Identity management is not configured on this server (IdentityAdmin).'
export const ACCESS_UNSUPPORTED_NOTE = 'This server has no identity management endpoints (api/access).'

const PERMISSIONS: readonly unknown[] = ['read', 'write', 'admin']
const str = (v: unknown): v is string => typeof v === 'string'
const optStr = (v: unknown) => v === undefined || v === null || typeof v === 'string'
const id = (v: unknown) => typeof v === 'string' || typeof v === 'number'

const isInfo = (v: unknown): v is AccessInfo =>
  isObj(v) &&
  str(v.provider) &&
  typeof v.enabled === 'boolean' &&
  optStr(v.configClientId) &&
  optStr(v.note) &&
  optStr(v.tokenEndpoint)

const isUser = (v: unknown): v is AccessUser =>
  isObj(v) && id(v.id) && str(v.username) && optStr(v.displayName) && optStr(v.email)

const isGroup = (v: unknown): v is AccessGroup => isObj(v) && id(v.id) && str(v.name) && optStr(v.path)

const isSubjectGrant = (v: unknown): v is SubjectGrant =>
  isObj(v) &&
  PERMISSIONS.includes(v.permission) &&
  str(v.app) &&
  !!v.app &&
  str(v.environment) &&
  !!v.environment &&
  str(v.role) &&
  typeof v.inherited === 'boolean' &&
  optStr(v.via)

const arrayOf =
  <T>(guard: (v: unknown) => v is T) =>
  (v: unknown): v is T[] =>
    Array.isArray(v) && v.every(guard)

const normalizeInfo = (i: AccessInfo): AccessInfo => ({
  provider: i.provider,
  enabled: i.enabled,
  ...(i.configClientId ? { configClientId: i.configClientId } : {}),
  ...(i.note ? { note: i.note } : {}),
  ...(i.tokenEndpoint ? { tokenEndpoint: i.tokenEndpoint } : {}),
})
const normalizeUser = (u: AccessUser): AccessUser => ({
  id: String(u.id),
  username: u.username,
  displayName: u.displayName ?? '',
  email: u.email ?? '',
})
const normalizeGroup = (g: AccessGroup): AccessGroup => ({ id: String(g.id), name: g.name, path: g.path ?? '' })
const normalizeGrant = (g: SubjectGrant): SubjectGrant => {
  const out: SubjectGrant = {
    permission: g.permission,
    app: g.app,
    environment: g.environment,
    role: g.role,
    inherited: g.inherited,
  }
  if (g.via) out.via = g.via
  return out
}

const root = (o: FilesClientOptions) => `${apiRoot(o)}/api/access`
const grantsUrl = (type: SubjectType, subjectId: string, o: FilesClientOptions) =>
  `${root(o)}/subjects/${type}/${encodeURIComponent(subjectId)}/grants`

/** `?search=&first=&max=` with empty values left out (no `?` at all when nothing is set). */
export function subjectQuery(q: SubjectQuery = {}): string {
  const p = new URLSearchParams()
  const search = q.search?.trim()
  if (search) p.set('search', search)
  if (q.first !== undefined && Number.isFinite(q.first) && q.first > 0) p.set('first', String(Math.floor(q.first)))
  if (q.max !== undefined && Number.isFinite(q.max) && q.max > 0) p.set('max', String(Math.floor(q.max)))
  const s = p.toString()
  return s ? `?${s}` : ''
}

/** 501 means identity management is off: say so instead of the files API's "read-only source" text. */
async function guarded<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call()
  } catch (e) {
    if (e instanceof FilesApiError && e.status === 501)
      throw new FilesApiError('read-only', ACCESS_NOT_CONFIGURED_MESSAGE, 501)
    throw e
  }
}

/**
 * Whether the server can manage the identity provider. An older server without `api/access` (404) is reported
 * as disabled, like a server with provider `none`.
 */
export async function getAccessInfo(o: FilesClientOptions = {}): Promise<AccessInfo> {
  try {
    return normalizeInfo(
      await guarded(() => requestJson(`${root(o)}/info`, {}, o, 'access info', isInfo, ACCESS_FORBIDDEN_MESSAGE)),
    )
  } catch (e) {
    if (e instanceof FilesApiError && e.status === 404)
      return { provider: 'none', enabled: false, note: ACCESS_UNSUPPORTED_NOTE }
    throw e
  }
}

export async function listUsers(q: SubjectQuery = {}, o: FilesClientOptions = {}): Promise<AccessUser[]> {
  const list = await guarded(() =>
    requestJson(`${root(o)}/users${subjectQuery(q)}`, {}, o, 'users', arrayOf(isUser), ACCESS_FORBIDDEN_MESSAGE),
  )
  return list.map(normalizeUser)
}

export async function listGroups(q: SubjectQuery = {}, o: FilesClientOptions = {}): Promise<AccessGroup[]> {
  const list = await guarded(() =>
    requestJson(`${root(o)}/groups${subjectQuery(q)}`, {}, o, 'groups', arrayOf(isGroup), ACCESS_FORBIDDEN_MESSAGE),
  )
  return list.map(normalizeGroup)
}

export async function listGrants(type: SubjectType, subjectId: string, o: FilesClientOptions = {}): Promise<SubjectGrant[]> {
  const list = await guarded(() =>
    requestJson(grantsUrl(type, subjectId, o), {}, o, `${type} "${subjectId}"`, arrayOf(isSubjectGrant), ACCESS_FORBIDDEN_MESSAGE),
  )
  return list.map(normalizeGrant)
}

/** Assigns the role for the grant (201 with the grant). Omitted app / environment = all. */
export async function addGrant(
  type: SubjectType,
  subjectId: string,
  req: GrantRequest,
  o: FilesClientOptions = {},
): Promise<SubjectGrant> {
  const g = await guarded(() =>
    requestJson(grantsUrl(type, subjectId, o), jsonInit('POST', req), o, `${type} "${subjectId}"`, isSubjectGrant, ACCESS_FORBIDDEN_MESSAGE),
  )
  return normalizeGrant(g)
}

/** Removes a direct grant (204). The scope is sent as listed ("*" = all). */
export const removeGrant = (
  type: SubjectType,
  subjectId: string,
  grant: Pick<SubjectGrant, 'permission' | 'app' | 'environment'>,
  o: FilesClientOptions = {},
) => {
  const p = new URLSearchParams({ permission: grant.permission, app: grant.app, environment: grant.environment })
  return guarded(() =>
    requestVoid(`${grantsUrl(type, subjectId, o)}?${p}`, { method: 'DELETE' }, o, `${type} "${subjectId}"`, ACCESS_FORBIDDEN_MESSAGE),
  )
}
