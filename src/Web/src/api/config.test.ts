import { describe, expect, it, vi } from 'vitest'
import { absoluteApiBaseUrl, buildConfigUrl, ConfigApiError, fetchConfig } from './config'

const res = (body: string, status = 200) => new Response(body, { status })
const base = 'http://api.test'

describe('buildConfigUrl', () => {
  it('trims trailing slashes and encodes segments', () => {
    expect(buildConfigUrl('http://x:5000//', 'my app', 'prod/eu')).toBe('http://x:5000/config/my%20app/prod%2Feu')
  })
})

describe('same-origin base URL', () => {
  it('builds relative URLs when the base is empty or "/"', () => {
    expect(buildConfigUrl('', 'app', 'dev')).toBe('/config/app/dev')
    expect(buildConfigUrl('/', 'app', 'dev')).toBe('/config/app/dev')
  })
  it('turns same-origin bases into absolute ones for display', () => {
    expect(absoluteApiBaseUrl('', 'https://demo.test')).toBe('https://demo.test')
    expect(absoluteApiBaseUrl('/', 'https://demo.test/')).toBe('https://demo.test')
    expect(absoluteApiBaseUrl('/backend/', 'https://demo.test')).toBe('https://demo.test/backend')
    expect(absoluteApiBaseUrl('http://localhost:5000/', 'https://demo.test')).toBe('http://localhost:5000')
  })
})

describe('fetchConfig', () => {
  it('returns parsed JSON and requests the right URL', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(res('{"a":{"b":1}}'))
    const out = await fetchConfig('app', 'dev', { baseUrl: base, fetchImpl })
    expect(out).toEqual({ a: { b: 1 } })
    expect(fetchImpl.mock.calls[0][0]).toBe('http://api.test/config/app/dev')
  })

  it('maps 404 to not-found', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(res('', 404))
    await expect(fetchConfig('a', 'b', { baseUrl: base, fetchImpl })).rejects.toMatchObject({
      kind: 'not-found',
      status: 404,
    })
  })

  it('maps 400 to bad-request using problem detail', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(res('{"detail":"Bad name"}', 400))
    await expect(fetchConfig('a', 'b', { baseUrl: base, fetchImpl })).rejects.toMatchObject({
      kind: 'bad-request',
      message: 'Bad name',
    })
  })

  it('maps other statuses to server', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(res('boom', 500))
    await expect(fetchConfig('a', 'b', { baseUrl: base, fetchImpl })).rejects.toMatchObject({
      kind: 'server',
      status: 500,
    })
  })

  it('maps fetch rejection to network', async () => {
    const fetchImpl = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'))
    const err = await fetchConfig('a', 'b', { baseUrl: base, fetchImpl }).catch((e: unknown) => e)
    expect(err).toBeInstanceOf(ConfigApiError)
    expect((err as ConfigApiError).kind).toBe('network')
  })

  it('maps non-JSON success body to invalid-response', async () => {
    const fetchImpl = vi.fn().mockResolvedValue(res('<html>', 200))
    await expect(fetchConfig('a', 'b', { baseUrl: base, fetchImpl })).rejects.toMatchObject({
      kind: 'invalid-response',
    })
  })
})
