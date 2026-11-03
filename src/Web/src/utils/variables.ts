import {
  VARIABLE_TYPES,
  type CellState,
  type DeclaredVariable,
  type UsedBy,
  type VariableEntry,
} from '../api/variables'

export const ENV_NAME_RULE = /^[A-Za-z0-9][A-Za-z0-9_-]*$/
export const ENV_NAME_HINT = 'Name: [A-Za-z0-9][A-Za-z0-9_-]*'

export function validateEnvName(name: string): string | null {
  if (!name) return 'Required'
  return ENV_NAME_RULE.test(name) ? null : 'Use letters, digits, "_" or "-"; start with a letter or digit'
}

// ---- editable variable rows

export interface VariableRow {
  id: number
  key: string
  type: DeclaredVariable['type']
  required: boolean
  secret: boolean
  description: string
  /** empty = all environments */
  environments: string[]
}

let nextId = 1
export const newRow = (key = ''): VariableRow => ({
  id: nextId++,
  key,
  type: 'string',
  required: false,
  secret: false,
  description: '',
  environments: [],
})

export const toRows = (vars: DeclaredVariable[]): VariableRow[] =>
  vars.map((v) => ({
    ...newRow(v.key),
    type: v.type,
    required: v.required,
    secret: v.secret,
    description: v.description,
    environments: v.environments ?? [],
  }))

export const fromRows = (rows: VariableRow[]): DeclaredVariable[] =>
  rows.map((r) => ({
    key: r.key.trim(),
    type: r.type,
    required: r.required,
    secret: r.secret,
    description: r.description,
    environments: r.environments.length ? [...r.environments] : null,
  }))

export interface RowErrors {
  key?: string
  type?: string
}

/** Errors by row id. Empty map = valid. */
export function validateRows(rows: VariableRow[]): Map<number, RowErrors> {
  const errors = new Map<number, RowErrors>()
  const seen = new Set<string>()
  for (const r of rows) {
    const e: RowErrors = {}
    const key = r.key.trim()
    if (!key) e.key = 'Required'
    else if (seen.has(key)) e.key = 'Duplicate key'
    else seen.add(key)
    if (!(VARIABLE_TYPES as readonly string[]).includes(r.type)) e.type = 'Invalid type'
    if (e.key || e.type) errors.set(r.id, e)
  }
  return errors
}

// ---- matrix

export interface MatrixRow {
  app: string
  cells: { env: string; state: CellState | null }[]
}

/** Union of environments in `byEnvironment`, ordered by `order` (unknown last), then alphabetically. */
export function matrixEnvironments(usedBy: UsedBy[], order: string[] = []): string[] {
  const set = new Set<string>()
  for (const u of usedBy) for (const e of Object.keys(u.byEnvironment ?? {})) set.add(e)
  const rank = (e: string) => {
    const i = order.indexOf(e)
    return i < 0 ? Number.MAX_SAFE_INTEGER : i
  }
  return [...set].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b))
}

export function buildMatrix(usedBy: UsedBy[], order: string[] = []): { envs: string[]; rows: MatrixRow[] } {
  const envs = matrixEnvironments(usedBy, order)
  const rows = [...usedBy]
    .sort((a, b) => a.app.localeCompare(b.app))
    .map((u) => ({ app: u.app, cells: envs.map((env) => ({ env, state: u.byEnvironment?.[env] ?? null })) }))
  return { envs, rows }
}

export const CELL_VISUAL: Record<CellState, { icon: string; color: string; label: string }> = {
  defined: { icon: 'mdi-check-circle', color: 'success', label: 'defined' },
  inherited: { icon: 'mdi-arrow-down-circle-outline', color: 'info', label: 'inherited from default' },
  missing: { icon: 'mdi-close-circle', color: 'error', label: 'missing' },
}

/** Visual state of an app chip: undeclared > secret > required > optional. */
export type UsageKind = 'undeclared' | 'secret' | 'required' | 'optional'
export const usageKind = (u: Pick<UsedBy, 'declared' | 'secret' | 'required'>): UsageKind =>
  !u.declared ? 'undeclared' : u.secret ? 'secret' : u.required ? 'required' : 'optional'

export const USAGE_VISUAL: Record<UsageKind, { icon: string; color: string; label: string }> = {
  undeclared: { icon: 'mdi-help-circle-outline', color: 'warning', label: 'Undeclared' },
  secret: { icon: 'mdi-lock-outline', color: 'info', label: 'Secret' },
  required: { icon: 'mdi-asterisk', color: 'neutral', label: 'Required' },
  optional: { icon: 'mdi-circle-outline', color: 'muted', label: 'Optional' },
}

// ---- status summary

export function statusLabel(s: {
  ok: boolean
  missing: string[]
  typeMismatches: unknown[]
  secretsInPlainText: string[]
  error?: string
}): string {
  if (s.error) return 'error'
  if (s.ok) return 'ok'
  if (s.missing.length) return `${s.missing.length} missing`
  const n = s.typeMismatches.length + s.secretsInPlainText.length
  return n ? `${n} issue${n === 1 ? '' : 's'}` : 'not ok'
}

// ---- CSV (never includes values)

export const CSV_HEADER = ['key', 'app', 'required', 'secret', 'files', 'environments']

export function csvCell(value: string): string {
  // Neutralise spreadsheet formula injection, then quote when needed.
  const safe = /^[=+\-@\t\r]/.test(value) ? "'" + value : value
  return /[",\n\r]/.test(safe) ? '"' + safe.replace(/"/g, '""') + '"' : safe
}

export function variablesToCsv(entries: VariableEntry[]): string {
  const lines = [CSV_HEADER.join(',')]
  for (const e of entries) {
    for (const u of e.usedBy) {
      lines.push(
        [e.key, u.app, String(u.required), String(u.secret), u.definedIn.join(';'), u.environments.join(';')]
          .map(csvCell)
          .join(','),
      )
    }
  }
  return lines.join('\r\n') + '\r\n'
}
