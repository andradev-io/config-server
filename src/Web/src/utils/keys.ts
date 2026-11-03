import { ALL, type ApiKey, type CreateKeyRequest, type KeyPermission, type KeyStatus } from '../api/keys'

/** Secrets look like `cfs_<prefix>_<random>`; only the prefix is ever listed. */
export const keyDisplay = (prefix: string) => `cfs_${prefix}_…`

export const isAll = (list: readonly string[]) => list.length === 0 || list.includes(ALL)

/** Chip labels for an apps / environments scope ("*" shown as "all"). */
export const scopeChips = (list: readonly string[]) => (isAll(list) ? ['all'] : [...list])

/** Theme color names (Vuetify `color` prop). */
export const STATUS_COLOR: Record<KeyStatus, string> = { active: 'success', expired: 'warning', revoked: 'error' }
export const STATUS_ICON: Record<KeyStatus, string> = {
  active: 'mdi-check-circle-outline',
  expired: 'mdi-clock-alert-outline',
  revoked: 'mdi-cancel',
}
export const PERMISSION_COLOR: Record<KeyPermission, string | undefined> = { read: undefined, write: 'neutral' }

export function formatInstant(iso: string | null | undefined, empty = '-'): string {
  if (!iso) return empty
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

/**
 * Keeps an "All" ("*") multi-select consistent: picking All clears the others, picking a specific item
 * while All is selected drops All, and an empty selection falls back to All.
 */
export function normalizeAllSelection(prev: readonly string[], next: readonly string[]): string[] {
  if (!next.length) return [ALL]
  if (next.includes(ALL)) {
    if (!prev.includes(ALL)) return [ALL]
    if (next.length > 1) return next.filter((x) => x !== ALL)
  }
  return [...next]
}

/** `YYYY-MM-DD` from a local date (the value of an `<input type="date">`). */
export const dateInputValue = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

/** Expiry at the end of the chosen local day, as an ISO instant; empty -> undefined (never expires). */
export function expiryToIso(date: string): string | undefined {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date.trim())
  if (!m) return undefined
  return new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]), 23, 59, 59, 999).toISOString()
}

export function expiryError(date: string, now = new Date()): string | null {
  if (!date.trim()) return null
  const iso = expiryToIso(date)
  if (!iso) return 'Invalid date'
  return new Date(iso).getTime() <= now.getTime() ? 'Pick a future date' : null
}

export const KEY_NAME_MAX = 100

export function validateKeyName(name: string): string | null {
  const n = name.trim()
  if (!n) return 'Required'
  if (n.length > KEY_NAME_MAX) return `At most ${KEY_NAME_MAX} characters`
  return null
}

export interface KeyForm {
  name: string
  permission: KeyPermission
  apps: string[]
  environments: string[]
  /** `YYYY-MM-DD` or empty. */
  expires: string
}

export const emptyKeyForm = (): KeyForm => ({ name: '', permission: 'read', apps: [ALL], environments: [ALL], expires: '' })

export function buildCreateRequest(f: KeyForm): CreateKeyRequest {
  const req: CreateKeyRequest = {
    name: f.name.trim(),
    permission: f.permission,
    apps: isAll(f.apps) ? [ALL] : [...f.apps],
    environments: isAll(f.environments) ? [ALL] : [...f.environments],
  }
  const expiresAt = expiryToIso(f.expires)
  if (expiresAt) req.expiresAt = expiresAt
  return req
}

/** Ready-to-copy example request with the new secret (first scoped app/env, placeholders when "all"). */
export function usageSnippet(secret: string, baseUrl: string, key: Pick<ApiKey, 'apps' | 'environments'>): string {
  const app = isAll(key.apps) ? '<app>' : key.apps[0]
  const env = isAll(key.environments) ? '<env>' : key.environments[0]
  return `curl -H "X-Api-Key: ${secret}" ${baseUrl.replace(/\/+$/, '')}/config/${app}/${env}`
}

export interface KeyFilter {
  search?: string
  hideRevoked?: boolean
}

const time = (iso: string) => {
  const t = Date.parse(iso)
  return Number.isNaN(t) ? 0 : t
}

/** Newest first; search matches name or prefix (case-insensitive). */
export function filterKeys(keys: readonly ApiKey[], f: KeyFilter = {}): ApiKey[] {
  const q = (f.search ?? '').trim().toLowerCase()
  return keys
    .filter((k) => !(f.hideRevoked && k.status === 'revoked'))
    .filter((k) => !q || k.name.toLowerCase().includes(q) || k.prefix.toLowerCase().includes(q))
    .sort((a, b) => time(b.createdAt) - time(a.createdAt) || a.name.localeCompare(b.name))
}

/** Revoked keys cannot be revoked or rotated again. */
export const canChangeKey = (k: Pick<ApiKey, 'status'>) => k.status !== 'revoked'
