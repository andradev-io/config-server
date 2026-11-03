import type { FilesClientOptions } from '../api/files'
import { apiRoot, isObj, isStrArr, requestJson } from '../api/request'
import { isGrant, toGrant, type Grant } from './grants'

export type AuthMode = 'none' | 'oidc'

export interface OidcInfo {
  authority: string
  clientId: string
  scopes: string[]
}

/** Present only when the server runs as a public sandbox (`demo` in `api/auth/info`). */
export interface DemoInfo {
  enabled: true
  /** Minutes of inactivity after which the visitor's workspace is discarded; 0 = unknown. */
  idleTtlMinutes: number
  /** Optional custom banner text from the server. */
  notice: string | null
}

export interface AuthInfo {
  mode: AuthMode
  oidc: OidcInfo | null
  apiKeys: boolean
  demo?: DemoInfo
}

/** Tolerant: anything that is not `{enabled: true, ...}` means "not a demo". */
export function parseDemo(v: unknown): DemoInfo | null {
  if (!isObj(v) || v.enabled !== true) return null
  const ttl =
    typeof v.idleTtlMinutes === 'number' && Number.isFinite(v.idleTtlMinutes) && v.idleTtlMinutes > 0
      ? Math.floor(v.idleTtlMinutes)
      : 0
  const notice = typeof v.notice === 'string' && v.notice.trim() ? v.notice.trim() : null
  return { enabled: true, idleTtlMinutes: ttl, notice }
}

export type AuthVia = 'none' | 'oidc' | 'apikey'

export interface Me {
  name: string
  roles: string[]
  via: AuthVia
  /** Scoped grants; absent on older servers (then derived from `roles`, see `effectiveGrants`). */
  grants?: Grant[]
}

const isOidc = (v: unknown): v is OidcInfo =>
  isObj(v) && typeof v.authority === 'string' && !!v.authority && typeof v.clientId === 'string' && !!v.clientId && isStrArr(v.scopes)

export const isAuthInfo = (v: unknown): v is AuthInfo =>
  isObj(v) &&
  (v.mode === 'none' || (v.mode === 'oidc' && isOidc(v.oidc))) &&
  (v.oidc === null || v.oidc === undefined || isOidc(v.oidc))

const VIA: readonly unknown[] = ['none', 'oidc', 'apikey']
export const isMe = (v: unknown): v is Me =>
  isObj(v) &&
  (typeof v.name === 'string' || v.name == null) &&
  isStrArr(v.roles) &&
  VIA.includes(v.via) &&
  (v.grants == null || (Array.isArray(v.grants) && v.grants.every(isGrant)))

/** Anonymous: tells the SPA which login (if any) to use. */
export async function getAuthInfo(o: FilesClientOptions = {}): Promise<AuthInfo> {
  const body = await requestJson(`${apiRoot(o)}/api/auth/info`, {}, { ...o, anonymous: true }, 'auth info', isAuthInfo)
  const info: AuthInfo = {
    mode: body.mode,
    oidc: body.mode === 'oidc' ? body.oidc : null,
    apiKeys: body.apiKeys === true,
  }
  const demo = parseDemo((body as { demo?: unknown }).demo)
  if (demo) info.demo = demo
  return info
}

/** The current caller (token attached when signed in). */
export async function getMe(o: FilesClientOptions = {}): Promise<Me> {
  const body = await requestJson(`${apiRoot(o)}/api/auth/me`, {}, o, 'current user', isMe)
  const me: Me = { name: typeof body.name === 'string' ? body.name : '', roles: body.roles, via: body.via }
  if (Array.isArray(body.grants)) me.grants = body.grants.map((g) => toGrant(g)!)
  return me
}
