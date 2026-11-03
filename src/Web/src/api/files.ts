import { getApiBaseUrl } from './config'
import { authorizedFetch } from './http'
import type { FileFormat } from '../utils/fileNames'

export type { FileFormat }

export interface ConfigFile {
  name: string
  format: FileFormat
  size: number
}

export type FilesErrorKind =
  | 'bad-request'
  | 'unauthorized'
  | 'forbidden'
  | 'not-found'
  | 'conflict'
  | 'read-only'
  | 'server'
  | 'network'
  | 'invalid-response'

export class FilesApiError extends Error {
  readonly kind: FilesErrorKind
  readonly status?: number
  readonly line?: number
  readonly column?: number

  constructor(kind: FilesErrorKind, message: string, status?: number, line?: number, column?: number) {
    super(message)
    this.name = 'FilesApiError'
    this.kind = kind
    this.status = status
    this.line = line
    this.column = column
  }
}

export interface FilesClientOptions {
  baseUrl?: string
  signal?: AbortSignal
  /** Injectable for tests. Defaults to global fetch. */
  fetchImpl?: typeof fetch
  /** Do not attach the access token (anonymous endpoints). */
  anonymous?: boolean
}

export const WRITES_DISABLED_MESSAGE = 'Writes are disabled on this server (ConfigSource:AllowWrite)'
export const READ_ONLY_MESSAGE = 'This config source is read-only'
export const UNAUTHORIZED_MESSAGE = 'Sign in required.'

const root = (o: FilesClientOptions) => `${(o.baseUrl ?? getApiBaseUrl()).replace(/\/+$/, '')}/api/apps`
const filesUrl = (app: string, o: FilesClientOptions) => `${root(o)}/${encodeURIComponent(app)}/files`
const fileUrl = (app: string, name: string, o: FilesClientOptions) =>
  `${filesUrl(app, o)}/${encodeURIComponent(name)}`

/** Every API call goes through here: token attach + 401 renew live in `authorizedFetch`. */
export async function send(url: string, init: RequestInit, o: FilesClientOptions): Promise<Response> {
  const doFetch = o.fetchImpl ?? fetch
  try {
    return await authorizedFetch(doFetch, url, { ...init, signal: o.signal }, { anonymous: o.anonymous })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    throw new FilesApiError('network', 'Could not reach the config server.')
  }
}

interface ErrorBody {
  error?: string
  line?: number
  column?: number
}

async function readErrorBody(response: Response): Promise<ErrorBody> {
  try {
    const text = (await response.text()).trim()
    if (!text) return {}
    try {
      const body = JSON.parse(text) as unknown
      if (typeof body === 'string') return { error: body }
      if (body && typeof body === 'object') {
        const b = body as Record<string, unknown>
        const msg = [b.error, b.detail, b.message, b.title].find((v) => typeof v === 'string' && v) as
          | string
          | undefined
        return {
          error: msg,
          line: typeof b.line === 'number' ? b.line : undefined,
          column: typeof b.column === 'number' ? b.column : undefined,
        }
      }
    } catch {
      /* plain text body */
    }
    return { error: text.length > 300 ? undefined : text }
  } catch {
    return {}
  }
}

/**
 * Maps a non-success response to a typed error. A 403 whose body explains something other than the
 * AllowWrite gate (e.g. a missing role) keeps the server's message; otherwise `forbiddenMessage`
 * (default: the AllowWrite message) is used.
 */
export async function toFilesError(
  response: Response,
  subject = 'resource',
  forbiddenMessage = WRITES_DISABLED_MESSAGE,
): Promise<FilesApiError> {
  const status = response.status
  switch (status) {
    case 400: {
      const b = await readErrorBody(response)
      return new FilesApiError('bad-request', b.error ?? 'Invalid request.', 400, b.line, b.column)
    }
    case 401:
      return new FilesApiError('unauthorized', UNAUTHORIZED_MESSAGE, 401)
    case 403: {
      const msg = (await readErrorBody(response)).error
      if (msg && /allowwrite/i.test(msg)) return new FilesApiError('forbidden', WRITES_DISABLED_MESSAGE, 403)
      const specific = !!msg && !/^forbidden\.?$/i.test(msg.trim())
      return new FilesApiError('forbidden', specific ? msg : forbiddenMessage, 403)
    }
    case 404:
      return new FilesApiError('not-found', `Not found: ${subject}.`, 404)
    case 409: {
      const b = await readErrorBody(response)
      return new FilesApiError('conflict', b.error ?? 'Conflict.', 409)
    }
    case 501:
      return new FilesApiError('read-only', READ_ONLY_MESSAGE, 501)
    case 502: {
      // The identity provider failed behind the server: its message explains what to fix.
      const b = await readErrorBody(response)
      return new FilesApiError('server', b.error ?? `Server responded with status ${status}.`, status)
    }
    default:
      return new FilesApiError('server', `Server responded with status ${status}.`, status)
  }
}

async function parseArray<T>(res: Response, guard: (v: unknown) => v is T): Promise<T[]> {
  try {
    const body = (await res.json()) as unknown
    if (Array.isArray(body) && body.every(guard)) return body
  } catch {
    /* fall through */
  }
  throw new FilesApiError('invalid-response', 'The server returned an unexpected response.', res.status)
}

export async function listApps(o: FilesClientOptions = {}): Promise<string[]> {
  const res = await send(root(o), { headers: { Accept: 'application/json' } }, o)
  if (!res.ok) throw await toFilesError(res, 'applications')
  return parseArray<string>(res, (v): v is string => typeof v === 'string')
}

export async function listFiles(app: string, o: FilesClientOptions = {}): Promise<ConfigFile[]> {
  const res = await send(filesUrl(app, o), { headers: { Accept: 'application/json' } }, o)
  if (!res.ok) throw await toFilesError(res, `application "${app}"`)
  return parseArray<ConfigFile>(
    res,
    (v): v is ConfigFile =>
      !!v &&
      typeof v === 'object' &&
      typeof (v as ConfigFile).name === 'string' &&
      ['json', 'yaml', 'env'].includes((v as ConfigFile).format),
  )
}

export async function getFile(app: string, name: string, o: FilesClientOptions = {}): Promise<string> {
  const res = await send(fileUrl(app, name, o), { headers: { Accept: 'text/plain' } }, o)
  if (!res.ok) throw await toFilesError(res, `file "${name}"`)
  return res.text()
}

/** Resolves to 'created' (201) or 'updated' (other 2xx). */
export async function putFile(
  app: string,
  name: string,
  content: string,
  o: FilesClientOptions = {},
): Promise<'created' | 'updated'> {
  const res = await send(
    fileUrl(app, name, o),
    { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ content }) },
    o,
  )
  if (res.status === 201) return 'created'
  if (res.ok) return 'updated'
  throw await toFilesError(res, `file "${name}"`)
}

export async function deleteFile(app: string, name: string, o: FilesClientOptions = {}): Promise<void> {
  const res = await send(fileUrl(app, name, o), { method: 'DELETE' }, o)
  if (res.ok) return
  throw await toFilesError(res, `file "${name}"`)
}
