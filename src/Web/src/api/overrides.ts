import type { FilesClientOptions } from './files'
import { apiRoot, isObj, isStrArr, jsonInit, requestJson } from './request'
import { VARIABLE_TYPES, type VariableType } from './variables'

export type OverrideFormat = 'json' | 'yaml' | 'env'
export type OverrideValue = { value: unknown; format: OverrideFormat; file: string } | { masked: true; file: string }
export type EffectiveValue = { value: unknown; from: string } | { masked: true; from: string }

export interface OverrideVariable {
  key: string
  type: VariableType
  secret: boolean
  declared: boolean
  values: Record<string, OverrideValue>
  effective: Record<string, EffectiveValue>
}

export interface OverridesDocument {
  app: string
  etag: string
  scopes: string[]
  variables: OverrideVariable[]
  warnings: string[]
}

export interface OverrideChange {
  key: string
  scope: string
  value?: unknown
  remove?: boolean
}

export interface OverridesPutBody {
  ifMatch?: string
  changes: OverrideChange[]
}

export interface PutFileResult {
  name: string
  created: boolean
  changedKeys: string[]
}
export interface OverridesPutResult {
  files: PutFileResult[]
  warnings: string[]
}

export const isMasked = (v: { masked?: true } | { value: unknown }): v is { masked: true } =>
  (v as { masked?: boolean }).masked === true

const FORMATS: readonly unknown[] = ['json', 'yaml', 'env']

const isOverrideValue = (v: unknown): v is OverrideValue =>
  isObj(v) &&
  typeof v.file === 'string' &&
  (v.masked === true || ('value' in v && FORMATS.includes(v.format)))

const isEffective = (v: unknown): v is EffectiveValue =>
  isObj(v) && typeof v.from === 'string' && (v.masked === true || 'value' in v)

const isRecordOf = <T>(v: unknown, guard: (x: unknown) => x is T): v is Record<string, T> =>
  isObj(v) && Object.values(v).every(guard)

const isVariable = (v: unknown): v is OverrideVariable =>
  isObj(v) &&
  typeof v.key === 'string' &&
  (VARIABLE_TYPES as readonly unknown[]).includes(v.type) &&
  typeof v.secret === 'boolean' &&
  typeof v.declared === 'boolean' &&
  isRecordOf(v.values, isOverrideValue) &&
  isRecordOf(v.effective, isEffective)

const isDocument = (v: unknown): v is OverridesDocument =>
  isObj(v) &&
  typeof v.app === 'string' &&
  typeof v.etag === 'string' &&
  isStrArr(v.scopes) &&
  Array.isArray(v.variables) &&
  v.variables.every(isVariable) &&
  (v.warnings === undefined || isStrArr(v.warnings))

const isFileResult = (v: unknown): v is PutFileResult =>
  isObj(v) && typeof v.name === 'string' && typeof v.created === 'boolean' && isStrArr(v.changedKeys)

const isPutResult = (v: unknown): v is OverridesPutResult =>
  isObj(v) &&
  Array.isArray(v.files) &&
  v.files.every(isFileResult) &&
  (v.warnings === undefined || isStrArr(v.warnings))

const url = (app: string, o: FilesClientOptions) => `${apiRoot(o)}/api/apps/${encodeURIComponent(app)}/overrides`

export async function getOverrides(app: string, o: FilesClientOptions = {}): Promise<OverridesDocument> {
  const body = await requestJson(url(app, o), {}, o, `application "${app}"`, isDocument)
  return { ...body, warnings: body.warnings ?? [] }
}

export async function putOverrides(
  app: string,
  body: OverridesPutBody,
  dryRun: boolean,
  o: FilesClientOptions = {},
): Promise<OverridesPutResult> {
  const res = await requestJson(
    `${url(app, o)}?dryRun=${dryRun ? 'true' : 'false'}`,
    jsonInit('PUT', body),
    o,
    `application "${app}"`,
    isPutResult,
  )
  return { ...res, warnings: res.warnings ?? [] }
}
