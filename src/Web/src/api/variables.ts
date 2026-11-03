import type { FilesClientOptions } from './files'
import { apiRoot, isObj, isStrArr, jsonInit, requestJson, requestVoid } from './request'

export const VARIABLE_TYPES = ['string', 'number', 'boolean', 'array', 'object'] as const
export type VariableType = (typeof VARIABLE_TYPES)[number]
export type CellState = 'defined' | 'inherited' | 'missing'

export interface DeclaredVariable {
  key: string
  type: VariableType
  required: boolean
  secret: boolean
  description: string
  /** null = all environments */
  environments: string[] | null
}

export interface TypeMismatch {
  key: string
  expected: string
  actual: string
}
export interface EnvironmentStatus {
  name: string
  ok: boolean
  missing: string[]
  typeMismatches: TypeMismatch[]
  secretsInPlainText: string[]
  error?: string
}
export interface AppStatus {
  app: string
  environments: EnvironmentStatus[]
  undeclared: string[]
}

export interface UsedBy {
  app: string
  required: boolean
  secret: boolean
  declared: boolean
  definedIn: string[]
  environments: string[]
  byEnvironment?: Record<string, CellState>
}
export interface VariableEntry {
  key: string
  type: VariableType
  appCount: number
  usedBy: UsedBy[]
}
export interface IndexWarning {
  app: string
  file: string
  error: string
}
export interface VariableIndex {
  variables: VariableEntry[]
  warnings: IndexWarning[]
}
export interface IndexQuery {
  search?: string
  app?: string
  env?: string
  declaredOnly?: boolean
}

const isType = (v: unknown): v is VariableType => (VARIABLE_TYPES as readonly unknown[]).includes(v)
const CELLS: readonly unknown[] = ['defined', 'inherited', 'missing']

const isDeclared = (v: unknown): v is DeclaredVariable =>
  isObj(v) &&
  typeof v.key === 'string' &&
  isType(v.type) &&
  typeof v.required === 'boolean' &&
  typeof v.secret === 'boolean' &&
  (v.environments === null || isStrArr(v.environments))

const isEnvStatus = (v: unknown): v is EnvironmentStatus =>
  isObj(v) &&
  typeof v.name === 'string' &&
  typeof v.ok === 'boolean' &&
  isStrArr(v.missing) &&
  isStrArr(v.secretsInPlainText) &&
  Array.isArray(v.typeMismatches) &&
  v.typeMismatches.every((m) => isObj(m) && typeof m.key === 'string')

const isUsedBy = (v: unknown): v is UsedBy =>
  isObj(v) &&
  typeof v.app === 'string' &&
  typeof v.required === 'boolean' &&
  typeof v.secret === 'boolean' &&
  typeof v.declared === 'boolean' &&
  isStrArr(v.definedIn) &&
  isStrArr(v.environments) &&
  (v.byEnvironment === undefined ||
    (isObj(v.byEnvironment) && Object.values(v.byEnvironment).every((c) => CELLS.includes(c))))

const isEntry = (v: unknown): v is VariableEntry =>
  isObj(v) &&
  typeof v.key === 'string' &&
  isType(v.type) &&
  typeof v.appCount === 'number' &&
  Array.isArray(v.usedBy) &&
  v.usedBy.every(isUsedBy)

const appVars = (app: string, o: FilesClientOptions) =>
  `${apiRoot(o)}/api/apps/${encodeURIComponent(app)}/variables`

export async function getAppVariables(app: string, o: FilesClientOptions = {}): Promise<DeclaredVariable[]> {
  const body = await requestJson(
    appVars(app, o),
    {},
    o,
    `application "${app}"`,
    // The server returns a bare array; a `{ variables }` wrapper is accepted too.
    (v): v is DeclaredVariable[] | { variables: DeclaredVariable[] } =>
      (Array.isArray(v) && v.every(isDeclared)) ||
      (isObj(v) && Array.isArray(v.variables) && v.variables.every(isDeclared)),
  )
  const list = Array.isArray(body) ? body : body.variables
  return list.map((x) => ({ ...x, description: typeof x.description === 'string' ? x.description : '' }))
}

export const putAppVariables = (app: string, variables: DeclaredVariable[], o: FilesClientOptions = {}) =>
  requestVoid(appVars(app, o), jsonInit('PUT', { variables }), o, `application "${app}"`)

export function getAppStatus(app: string, o: FilesClientOptions = {}): Promise<AppStatus> {
  return requestJson(
    `${apiRoot(o)}/api/apps/${encodeURIComponent(app)}/status`,
    {},
    o,
    `application "${app}"`,
    (v): v is AppStatus =>
      isObj(v) &&
      typeof v.app === 'string' &&
      Array.isArray(v.environments) &&
      v.environments.every(isEnvStatus) &&
      isStrArr(v.undeclared),
  )
}

export async function searchVariables(q: IndexQuery = {}, o: FilesClientOptions = {}): Promise<VariableIndex> {
  const p = new URLSearchParams()
  if (q.search) p.set('search', q.search)
  if (q.app) p.set('app', q.app)
  if (q.env) p.set('env', q.env)
  if (q.declaredOnly) p.set('declaredOnly', 'true')
  const qs = p.toString()
  const body = await requestJson(
    `${apiRoot(o)}/api/variables${qs ? `?${qs}` : ''}`,
    {},
    o,
    'variables',
    (v): v is VariableIndex => isObj(v) && Array.isArray(v.variables) && v.variables.every(isEntry),
  )
  return { variables: body.variables, warnings: Array.isArray(body.warnings) ? body.warnings : [] }
}

/** The key is a dotted path: each segment is URL-encoded separately. */
export const variableKeyPath = (key: string) => key.split('.').map(encodeURIComponent).join('.')

export const getVariable = (key: string, o: FilesClientOptions = {}): Promise<VariableEntry> =>
  requestJson(`${apiRoot(o)}/api/variables/${variableKeyPath(key)}`, {}, o, `variable "${key}"`, isEntry)
