import type { OverrideChange, OverrideVariable } from '../api/overrides'
import { isMasked } from '../api/overrides'
import type { VariableType } from '../api/variables'

export const MASK = '••••••'

// ---------------------------------------------------------------- value parsing

export type ParseResult = { ok: true; value: unknown } | { ok: false; error: string }

export const isScalarType = (t: VariableType) => t === 'string' || t === 'number' || t === 'boolean'

/**
 * Parses cell text according to the variable type.
 * Strings are kept as is (an empty string is a valid value: clearing is an explicit action).
 */
export function parseValue(type: VariableType, text: string): ParseResult {
  switch (type) {
    case 'string':
      return { ok: true, value: text }
    case 'number': {
      const t = text.trim()
      if (!t) return { ok: false, error: 'Enter a number' }
      const n = Number(t)
      return Number.isFinite(n) ? { ok: true, value: n } : { ok: false, error: 'Not a valid number' }
    }
    case 'boolean': {
      const t = text.trim().toLowerCase()
      if (t === 'true') return { ok: true, value: true }
      if (t === 'false') return { ok: true, value: false }
      return { ok: false, error: 'Use true or false' }
    }
    case 'array':
    case 'object': {
      let v: unknown
      try {
        v = JSON.parse(text)
      } catch (e) {
        return { ok: false, error: e instanceof Error ? e.message : 'Invalid JSON' }
      }
      const isArr = Array.isArray(v)
      if (type === 'array' && !isArr) return { ok: false, error: 'Expected a JSON array' }
      if (type === 'object' && (isArr || v === null || typeof v !== 'object')) {
        return { ok: false, error: 'Expected a JSON object' }
      }
      return { ok: true, value: v }
    }
  }
}

export function actualType(v: unknown): VariableType | 'null' {
  if (v === null) return 'null'
  if (Array.isArray(v)) return 'array'
  switch (typeof v) {
    case 'number':
      return 'number'
    case 'boolean':
      return 'boolean'
    case 'object':
      return 'object'
    default:
      return 'string'
  }
}

/**
 * True when a stored value does not match the declared type. Like the server, a string that reads as a
 * number/boolean satisfies number/boolean (values coming from .env files are always strings).
 */
export function hasTypeMismatch(type: VariableType, v: unknown): boolean {
  if (actualType(v) === type) return false
  if (typeof v !== 'string') return true
  const s = v.trim()
  if (type === 'number') return s === '' || !Number.isFinite(Number(s))
  if (type === 'boolean') return !/^(true|false)$/i.test(s)
  return true
}

/** Text shown/edited for a value (scalars raw, arrays/objects as JSON). */
export function formatValue(v: unknown, pretty = false): string {
  if (typeof v === 'string') return v
  if (v === undefined) return ''
  return pretty ? JSON.stringify(v, null, 2) : JSON.stringify(v)
}

export function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true
  if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') return false
  if (Array.isArray(a) !== Array.isArray(b)) return false
  const ka = Object.keys(a as object)
  const kb = Object.keys(b as object)
  if (ka.length !== kb.length) return false
  return ka.every((k) => deepEqual((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k]))
}

// ---------------------------------------------------------------- pending change set

export interface PendingChange {
  key: string
  scope: string
  /** true = remove the override (cell goes back to inheriting). */
  remove: boolean
  value?: unknown
}
export type PendingSet = ReadonlyMap<string, PendingChange>

export const cellId = (key: string, scope: string) => `${key}\u0000${scope}`
export const emptyPending = (): PendingSet => new Map()

/** The stored override of a cell: undefined when the scope has none, `masked` for secrets. */
export function storedOverride(
  row: Pick<OverrideVariable, 'values'>,
  scope: string,
): { masked: true } | { value: unknown } | undefined {
  const v = row.values[scope]
  if (!v) return undefined
  return isMasked(v) ? { masked: true } : { value: v.value }
}

/**
 * Records "set override" for a cell. Dedupes: setting the stored value again drops the pending entry.
 * Secret (masked) values cannot be compared, so they always count as a change.
 */
export function setPending(
  pending: PendingSet,
  row: Pick<OverrideVariable, 'key' | 'values'>,
  scope: string,
  value: unknown,
): PendingSet {
  const next = new Map(pending)
  const id = cellId(row.key, scope)
  const stored = storedOverride(row, scope)
  if (stored && 'value' in stored && deepEqual(stored.value, value)) next.delete(id)
  else next.set(id, { key: row.key, scope, remove: false, value })
  return next
}

/** Records "back to inherit" for a cell. Dropped when the cell has no stored override. */
export function removePending(
  pending: PendingSet,
  row: Pick<OverrideVariable, 'key' | 'values'>,
  scope: string,
): PendingSet {
  const next = new Map(pending)
  const id = cellId(row.key, scope)
  if (storedOverride(row, scope)) next.set(id, { key: row.key, scope, remove: true })
  else next.delete(id)
  return next
}

/** Forgets the pending change of one cell (undo). */
export function clearPending(pending: PendingSet, key: string, scope: string): PendingSet {
  const next = new Map(pending)
  next.delete(cellId(key, scope))
  return next
}

/**
 * Drops entries that no longer apply after a reload (variable gone, or the server now
 * has exactly the pending state).
 */
export function reconcilePending(pending: PendingSet, rows: OverrideVariable[]): PendingSet {
  const byKey = new Map(rows.map((r) => [r.key, r]))
  const next = new Map<string, PendingChange>()
  for (const [id, c] of pending) {
    const row = byKey.get(c.key)
    if (!row) {
      // Variables created in this session (not yet on the server) stay pending.
      if (!c.remove) next.set(id, c)
      continue
    }
    const stored = storedOverride(row, c.scope)
    if (c.remove ? !stored : stored && 'value' in stored && deepEqual(stored.value, c.value)) continue
    next.set(id, c)
  }
  return next
}

export const getPending = (pending: PendingSet, key: string, scope: string) => pending.get(cellId(key, scope))

/** The pending changes whose scope passes `keep` (e.g. the scopes the caller may write). */
export function filterPending(pending: PendingSet, keep: (scope: string) => boolean): PendingSet {
  return new Map([...pending].filter(([, c]) => keep(c.scope)))
}

/** Request body changes, stably ordered by key then scope. */
export function buildChanges(pending: PendingSet): OverrideChange[] {
  return [...pending.values()]
    .sort((a, b) => a.key.localeCompare(b.key) || a.scope.localeCompare(b.scope))
    .map((c) => (c.remove ? { key: c.key, scope: c.scope, remove: true } : { key: c.key, scope: c.scope, value: c.value }))
}

// ---------------------------------------------------------------- cells and rows

export type CellView =
  | { kind: 'override'; masked: boolean; text: string; value?: unknown; file: string }
  | { kind: 'inherited'; masked: boolean; text: string; from: string }
  | { kind: 'empty' }

/** What a grid cell shows before considering pending edits. */
export function storedCell(row: OverrideVariable, scope: string): CellView {
  const v = row.values[scope]
  if (v) {
    return isMasked(v)
      ? { kind: 'override', masked: true, text: MASK, file: v.file }
      : { kind: 'override', masked: false, text: formatValue(v.value), value: v.value, file: v.file }
  }
  const e = row.effective[scope]
  if (scope !== 'default' && e) {
    return isMasked(e)
      ? { kind: 'inherited', masked: true, text: MASK, from: e.from }
      : { kind: 'inherited', masked: false, text: formatValue(e.value), from: e.from }
  }
  return { kind: 'empty' }
}

/** What a grid cell shows once the pending change (if any) is applied. */
export function cellView(row: OverrideVariable, scope: string, change: PendingChange | undefined): CellView {
  if (!change) return storedCell(row, scope)
  if (change.remove) {
    // After removing, a non-default scope inherits from default.
    const def = row.values.default
    if (scope !== 'default' && def) {
      return isMasked(def)
        ? { kind: 'inherited', masked: true, text: MASK, from: 'default' }
        : { kind: 'inherited', masked: false, text: formatValue(def.value), from: 'default' }
    }
    return { kind: 'empty' }
  }
  return {
    kind: 'override',
    masked: row.secret,
    text: row.secret ? MASK : formatValue(change.value),
    value: change.value,
    file: '',
  }
}

export function cellMismatch(row: OverrideVariable, view: CellView): boolean {
  return view.kind === 'override' && !view.masked && 'value' in view && hasTypeMismatch(row.type, view.value)
}

export interface RowFilter {
  search: string
  onlyOverridden: boolean
}

const hasOverride = (row: OverrideVariable, pending: PendingSet) =>
  Object.keys(row.values).length > 0 || [...pending.values()].some((c) => c.key === row.key && !c.remove)

export function filterRows(rows: OverrideVariable[], f: RowFilter, pending: PendingSet = emptyPending()): OverrideVariable[] {
  const q = f.search.trim().toLowerCase()
  return rows.filter((r) => (!q || r.key.toLowerCase().includes(q)) && (!f.onlyOverridden || hasOverride(r, pending)))
}

/** Rows that exist only locally (added in this session, not yet saved). */
export function withLocalRows(
  rows: OverrideVariable[],
  added: OverrideVariable[],
): OverrideVariable[] {
  const have = new Set(rows.map((r) => r.key))
  return [...rows, ...added.filter((a) => !have.has(a.key))].sort((a, b) => a.key.localeCompare(b.key))
}

// ---------------------------------------------------------------- add variable

export const KEY_RULE = /^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*$/

export function validateNewKey(key: string, existing: Iterable<string>): string | true {
  const k = key.trim()
  if (!k) return 'Required'
  if (!KEY_RULE.test(k)) return 'Use dotted segments of letters, digits, _ or -'
  for (const e of existing) if (e.toLowerCase() === k.toLowerCase()) return 'Variable already exists'
  return true
}

/** Guess a type from a typed value when the user did not choose one. */
export function inferType(text: string): VariableType {
  const t = text.trim()
  if (t === 'true' || t === 'false') return 'boolean'
  if (t !== '' && Number.isFinite(Number(t))) return 'number'
  try {
    const v = JSON.parse(t)
    if (Array.isArray(v)) return 'array'
    if (v && typeof v === 'object') return 'object'
  } catch {
    /* plain string */
  }
  return 'string'
}

export function newLocalRow(key: string, type: VariableType): OverrideVariable {
  return { key: key.trim(), type, secret: false, declared: false, values: {}, effective: {} }
}
