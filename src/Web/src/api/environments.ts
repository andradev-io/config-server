import type { FilesClientOptions } from './files'
import { apiRoot, isObj, jsonInit, requestJson, requestVoid } from './request'

export interface EnvironmentInfo {
  name: string
  description: string
  order: number
  declared: boolean
  appCount: number
}

export interface CreateEnvironmentRequest {
  name: string
  description?: string
  order?: number
  scaffold?: { apps: string[]; copyFrom?: string }
}

const root = (o: FilesClientOptions) => `${apiRoot(o)}/api/environments`
const one = (name: string, o: FilesClientOptions) => `${root(o)}/${encodeURIComponent(name)}`

const isEnv = (v: unknown): v is EnvironmentInfo =>
  isObj(v) &&
  typeof v.name === 'string' &&
  typeof v.order === 'number' &&
  typeof v.declared === 'boolean' &&
  typeof v.appCount === 'number'

export async function listEnvironments(o: FilesClientOptions = {}): Promise<EnvironmentInfo[]> {
  const list = await requestJson(
    root(o),
    {},
    o,
    'environments',
    (v): v is EnvironmentInfo[] => Array.isArray(v) && v.every(isEnv),
  )
  return list.map((e) => ({ ...e, description: typeof e.description === 'string' ? e.description : '' }))
}

export const createEnvironment = (req: CreateEnvironmentRequest, o: FilesClientOptions = {}) =>
  requestVoid(root(o), jsonInit('POST', req), o, 'environment')

export const updateEnvironment = (
  name: string,
  body: { description: string; order: number },
  o: FilesClientOptions = {},
) => requestVoid(one(name, o), jsonInit('PUT', body), o, `environment "${name}"`)

export const deleteEnvironment = (name: string, deleteFiles: boolean, o: FilesClientOptions = {}) =>
  requestVoid(
    `${one(name, o)}?deleteFiles=${deleteFiles ? 'true' : 'false'}`,
    { method: 'DELETE' },
    o,
    `environment "${name}"`,
  )
