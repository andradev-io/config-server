import { authorizedFetch } from './http'

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue }

export type ConfigErrorKind =
  | 'not-found'
  | 'bad-request'
  | 'unauthorized'
  | 'forbidden'
  | 'server'
  | 'network'
  | 'invalid-response'

export class ConfigApiError extends Error {
  readonly kind: ConfigErrorKind
  readonly status?: number

  constructor(kind: ConfigErrorKind, message: string, status?: number) {
    super(message)
    this.name = 'ConfigApiError'
    this.kind = kind
    this.status = status
  }
}

export interface FetchConfigOptions {
  baseUrl?: string
  signal?: AbortSignal
  /** Injectable for tests. Defaults to global fetch. */
  fetchImpl?: typeof fetch
}

export const DEFAULT_API_BASE_URL = 'http://localhost:5000'

/**
 * API base URL. Unset = local dev default. Empty or `/` = same origin (relative URLs, e.g. behind the
 * nginx of the container image); an absolute URL points at another host.
 */
export function getApiBaseUrl(): string {
  const value = import.meta.env?.VITE_API_BASE_URL
  return value === undefined ? DEFAULT_API_BASE_URL : value
}

/** Absolute form of a base URL (for display and copy-paste snippets): same-origin bases get the page origin. */
export function absoluteApiBaseUrl(baseUrl: string, origin: string): string {
  const base = baseUrl.trim().replace(/\/+$/, '')
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(base)) return base
  const path = base && !base.startsWith('/') ? `/${base}` : base
  return `${origin.replace(/\/+$/, '')}${path}`
}

export function getDisplayApiBaseUrl(): string {
  return absoluteApiBaseUrl(getApiBaseUrl(), typeof window === 'undefined' ? '' : window.location.origin)
}

export function buildConfigUrl(baseUrl: string, application: string, environment: string): string {
  const base = baseUrl.replace(/\/+$/, '')
  return `${base}/config/${encodeURIComponent(application)}/${encodeURIComponent(environment)}`
}

export async function fetchConfig(
  application: string,
  environment: string,
  options: FetchConfigOptions = {},
): Promise<JsonValue> {
  const doFetch = options.fetchImpl ?? fetch
  const url = buildConfigUrl(options.baseUrl ?? getApiBaseUrl(), application, environment)

  let response: Response
  try {
    response = await authorizedFetch(doFetch, url, { headers: { Accept: 'application/json' }, signal: options.signal })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    throw new ConfigApiError('network', 'Could not reach the config server.')
  }

  if (response.status === 404) {
    throw new ConfigApiError('not-found', `No configuration found for "${application}" / "${environment}".`, 404)
  }
  if (response.status === 400) {
    throw new ConfigApiError(
      'bad-request',
      await readMessage(response, 'Invalid application or environment name, or invalid JSON.'),
      400,
    )
  }
  await throwIfAuthError(response)
  if (!response.ok) {
    throw new ConfigApiError('server', `Server responded with status ${response.status}.`, response.status)
  }

  try {
    return (await response.json()) as JsonValue
  } catch {
    throw new ConfigApiError('invalid-response', 'The server returned a response that is not valid JSON.', response.status)
  }
}

export type ConfigFormat = 'json' | 'yaml' | 'env'

export function buildConfigFormatUrl(
  baseUrl: string,
  application: string,
  environment: string,
  format: ConfigFormat,
): string {
  return `${buildConfigUrl(baseUrl, application, environment)}?format=${format}`
}

/** Fetches the merged config as text in the requested format. */
export async function fetchConfigText(
  application: string,
  environment: string,
  format: ConfigFormat,
  options: FetchConfigOptions = {},
): Promise<string> {
  const doFetch = options.fetchImpl ?? fetch
  const url = buildConfigFormatUrl(options.baseUrl ?? getApiBaseUrl(), application, environment, format)

  let response: Response
  try {
    response = await authorizedFetch(doFetch, url, {
      headers: { Accept: 'text/plain, application/json' },
      signal: options.signal,
    })
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e
    throw new ConfigApiError('network', 'Could not reach the config server.')
  }
  if (response.status === 404) {
    throw new ConfigApiError('not-found', `No configuration found for "${application}" / "${environment}".`, 404)
  }
  if (response.status === 400) {
    throw new ConfigApiError('bad-request', await readMessage(response, 'Invalid request or invalid configuration.'), 400)
  }
  await throwIfAuthError(response)
  if (!response.ok) {
    throw new ConfigApiError('server', `Server responded with status ${response.status}.`, response.status)
  }
  return response.text()
}

export const CONFIG_UNAUTHORIZED_MESSAGE = 'Sign in required.'
export const CONFIG_FORBIDDEN_MESSAGE = 'You do not have permission to read this configuration.'

async function throwIfAuthError(response: Response): Promise<void> {
  if (response.status === 401) throw new ConfigApiError('unauthorized', CONFIG_UNAUTHORIZED_MESSAGE, 401)
  if (response.status === 403) {
    const msg = await readMessage(response, CONFIG_FORBIDDEN_MESSAGE)
    throw new ConfigApiError('forbidden', /^forbidden\.?$/i.test(msg.trim()) ? CONFIG_FORBIDDEN_MESSAGE : msg, 403)
  }
}

async function readMessage(response: Response, fallback: string): Promise<string> {
  try {
    const text = (await response.text()).trim()
    if (!text) return fallback
    try {
      const body = JSON.parse(text) as string | { error?: unknown; detail?: unknown; message?: unknown; title?: unknown }
      if (typeof body === 'string') return body || fallback
      for (const v of [body.error, body.detail, body.message, body.title]) if (typeof v === 'string' && v) return v
    } catch {
      /* plain text body */
    }
    return text.length > 300 ? fallback : text
  } catch {
    return fallback
  }
}
