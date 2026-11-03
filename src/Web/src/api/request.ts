import { FilesApiError, send, toFilesError, type FilesClientOptions } from './files'
import { getApiBaseUrl } from './config'

export { send }

export const apiRoot = (o: FilesClientOptions) => (o.baseUrl ?? getApiBaseUrl()).replace(/\/+$/, '')

/** Call that returns a JSON body validated by `guard`. `forbidden` overrides the generic 403 message. */
export async function requestJson<T>(
  url: string,
  init: RequestInit,
  o: FilesClientOptions,
  subject: string,
  guard: (v: unknown) => v is T,
  forbidden?: string,
): Promise<T> {
  const res = await send(url, { ...init, headers: { Accept: 'application/json', ...init.headers } }, o)
  if (!res.ok) throw await toFilesError(res, subject, forbidden)
  try {
    const body = (await res.json()) as unknown
    if (guard(body)) return body
  } catch {
    /* fall through */
  }
  throw new FilesApiError('invalid-response', 'The server returned an unexpected response.', res.status)
}

/** Call whose success is any 2xx without a needed body. */
export async function requestVoid(
  url: string,
  init: RequestInit,
  o: FilesClientOptions,
  subject: string,
  forbidden?: string,
) {
  const res = await send(url, init, o)
  if (!res.ok) throw await toFilesError(res, subject, forbidden)
}

export const jsonInit = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
})

export const isObj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v)
export const isStrArr = (v: unknown): v is string[] => Array.isArray(v) && v.every((x) => typeof x === 'string')
