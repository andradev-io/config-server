import type { GrantPermission } from '../auth/grants'
import { FilesApiError, type FilesClientOptions } from './files'
import { apiRoot, isObj, jsonInit, requestJson, requestVoid } from './request'

/**
 * "OIDC API keys" (admin only): machine credentials created in the identity provider, with the same config grants
 * as users. A client requests a token with its client id and secret (client credentials) and sends it as a Bearer.
 * Errors are `{error}`; 501 = identity management is not configured on the server.
 */

/** Machine credentials hold read or write grants; admin is never granted to them. */
export type OidcKeyPermission = 'read' | 'write'

/** One grant of a credential; app / environment "*" = all. */
export interface OidcKeyGrant {
  permission: GrantPermission
  app: string
  environment: string
  role: string
}

export interface OidcKey {
  /** The identity provider's internal id, used in the URLs. */
  id: string
  /** What the credential presents when it asks for a token. */
  clientId: string
  name: string
  grants: OidcKeyGrant[]
  enabled: boolean
  createdAt: string | null
}

/** Omitted app / environment = all. */
export interface OidcKeyGrantRequest {
  permission: OidcKeyPermission
  app?: string
  environment?: string
}

export interface CreateOidcKeyRequest {
  name: string
  grants: OidcKeyGrantRequest[]
}

/** The secret is returned only by create and rotate, never again. */
export interface IssuedOidcKey {
  client: OidcKey
  clientId: string
  secret: string
  tokenEndpoint: string | null
}

export const OIDC_KEYS_FORBIDDEN_MESSAGE = 'Managing OIDC API keys requires the config:admin role.'
export const OIDC_KEYS_NOT_CONFIGURED_MESSAGE =
  'OIDC API keys are not available: identity management is not configured on this server (IdentityAdmin).'

const PERMISSIONS: readonly unknown[] = ['read', 'write', 'admin']
const str = (v: unknown): v is string => typeof v === 'string'
const optStr = (v: unknown) => v === undefined || v === null || typeof v === 'string'

const isGrant = (v: unknown): v is OidcKeyGrant =>
  isObj(v) && PERMISSIONS.includes(v.permission) && str(v.app) && str(v.environment) && str(v.role)

export const isOidcKey = (v: unknown): v is OidcKey =>
  isObj(v) &&
  (str(v.id) || typeof v.id === 'number') &&
  str(v.clientId) &&
  str(v.name) &&
  Array.isArray(v.grants) &&
  v.grants.every(isGrant) &&
  typeof v.enabled === 'boolean' &&
  optStr(v.createdAt)

const isIssued = (v: unknown): v is IssuedOidcKey =>
  isObj(v) && isOidcKey(v.client) && str(v.clientId) && str(v.secret) && !!v.secret && optStr(v.tokenEndpoint)

const normalize = (k: OidcKey): OidcKey => ({
  id: String(k.id),
  clientId: k.clientId,
  name: k.name,
  grants: k.grants.map((g) => ({ permission: g.permission, app: g.app, environment: g.environment, role: g.role })),
  enabled: k.enabled,
  createdAt: k.createdAt ?? null,
})

const normalizeIssued = (r: IssuedOidcKey): IssuedOidcKey => ({
  client: normalize(r.client),
  clientId: r.clientId,
  secret: r.secret,
  tokenEndpoint: r.tokenEndpoint ?? null,
})

const root = (o: FilesClientOptions) => `${apiRoot(o)}/api/oidc-keys`
const one = (id: string, o: FilesClientOptions) => `${root(o)}/${encodeURIComponent(id)}`

/** 501 means identity management is off: say so instead of the files API's "read-only source" text. */
async function guarded<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call()
  } catch (e) {
    if (e instanceof FilesApiError && e.status === 501)
      throw new FilesApiError('read-only', OIDC_KEYS_NOT_CONFIGURED_MESSAGE, 501)
    throw e
  }
}

export async function listOidcKeys(o: FilesClientOptions = {}): Promise<OidcKey[]> {
  const list = await guarded(() =>
    requestJson(
      root(o),
      {},
      o,
      'OIDC API keys',
      (v): v is OidcKey[] => Array.isArray(v) && v.every(isOidcKey),
      OIDC_KEYS_FORBIDDEN_MESSAGE,
    ),
  )
  return list.map(normalize)
}

export async function createOidcKey(req: CreateOidcKeyRequest, o: FilesClientOptions = {}): Promise<IssuedOidcKey> {
  const r = await guarded(() =>
    requestJson(root(o), jsonInit('POST', req), o, 'OIDC API key', isIssued, OIDC_KEYS_FORBIDDEN_MESSAGE),
  )
  return normalizeIssued(r)
}

/** Issues a new secret for the credential; the old one stops working. */
export async function rotateOidcKey(id: string, o: FilesClientOptions = {}): Promise<IssuedOidcKey> {
  const r = await guarded(() =>
    requestJson(`${one(id, o)}/rotate`, { method: 'POST' }, o, `OIDC API key "${id}"`, isIssued, OIDC_KEYS_FORBIDDEN_MESSAGE),
  )
  return normalizeIssued(r)
}

/** Deletes the credential (204, idempotent). */
export const deleteOidcKey = (id: string, o: FilesClientOptions = {}) =>
  guarded(() => requestVoid(one(id, o), { method: 'DELETE' }, o, `OIDC API key "${id}"`, OIDC_KEYS_FORBIDDEN_MESSAGE))
