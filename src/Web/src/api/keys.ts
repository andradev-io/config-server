import type { FilesClientOptions } from './files'
import { apiRoot, isObj, isStrArr, jsonInit, requestJson, requestVoid } from './request'

export type KeyPermission = 'read' | 'write'
export type KeyStatus = 'active' | 'expired' | 'revoked'

/** "*" in apps / environments means all. */
export const ALL = '*'

export interface ApiKey {
  id: string
  name: string
  prefix: string
  apps: string[]
  environments: string[]
  permission: KeyPermission
  createdAt: string
  expiresAt: string | null
  revokedAt: string | null
  lastUsedAt: string | null
  status: KeyStatus
}

export interface CreateKeyRequest {
  name: string
  apps?: string[]
  environments?: string[]
  permission?: KeyPermission
  /** ISO 8601 instant. */
  expiresAt?: string
}

/** The secret is returned only by create and rotate, never again. */
export interface IssuedKey {
  key: ApiKey
  secret: string
}

export const KEYS_FORBIDDEN_MESSAGE = 'Managing API keys requires the config:admin role.'

const PERMISSIONS: readonly unknown[] = ['read', 'write']
const STATUSES: readonly unknown[] = ['active', 'expired', 'revoked']
const isDateOrNull = (v: unknown) => v === null || v === undefined || typeof v === 'string'

export const isApiKey = (v: unknown): v is ApiKey =>
  isObj(v) &&
  (typeof v.id === 'string' || typeof v.id === 'number') &&
  typeof v.name === 'string' &&
  typeof v.prefix === 'string' &&
  isStrArr(v.apps) &&
  isStrArr(v.environments) &&
  PERMISSIONS.includes(v.permission) &&
  typeof v.createdAt === 'string' &&
  isDateOrNull(v.expiresAt) &&
  isDateOrNull(v.revokedAt) &&
  isDateOrNull(v.lastUsedAt) &&
  STATUSES.includes(v.status)

const isIssued = (v: unknown): v is IssuedKey =>
  isObj(v) && isApiKey(v.key) && typeof v.secret === 'string' && !!v.secret

const normalize = (k: ApiKey): ApiKey => ({
  ...k,
  id: String(k.id),
  expiresAt: k.expiresAt ?? null,
  revokedAt: k.revokedAt ?? null,
  lastUsedAt: k.lastUsedAt ?? null,
})

const root = (o: FilesClientOptions) => `${apiRoot(o)}/api/keys`
const one = (id: string, o: FilesClientOptions) => `${root(o)}/${encodeURIComponent(id)}`

export async function listKeys(o: FilesClientOptions = {}): Promise<ApiKey[]> {
  const list = await requestJson(
    root(o),
    {},
    o,
    'API keys',
    (v): v is ApiKey[] => Array.isArray(v) && v.every(isApiKey),
    KEYS_FORBIDDEN_MESSAGE,
  )
  return list.map(normalize)
}

export async function createKey(req: CreateKeyRequest, o: FilesClientOptions = {}): Promise<IssuedKey> {
  const r = await requestJson(root(o), jsonInit('POST', req), o, 'API key', isIssued, KEYS_FORBIDDEN_MESSAGE)
  return { key: normalize(r.key), secret: r.secret }
}

/** Revokes the key (204). */
export const revokeKey = (id: string, o: FilesClientOptions = {}) =>
  requestVoid(one(id, o), { method: 'DELETE' }, o, `API key "${id}"`, KEYS_FORBIDDEN_MESSAGE)

/** Issues a new secret for the key; the old one stops working. */
export async function rotateKey(id: string, o: FilesClientOptions = {}): Promise<IssuedKey> {
  const r = await requestJson(
    `${one(id, o)}/rotate`,
    { method: 'POST' },
    o,
    `API key "${id}"`,
    isIssued,
    KEYS_FORBIDDEN_MESSAGE,
  )
  return { key: normalize(r.key), secret: r.secret }
}
