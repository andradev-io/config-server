import { afterEach, describe, expect, it, vi } from 'vitest'
import { fetchConfig, fetchConfigText } from './config'
import { listEnvironments } from './environments'
import { getFile, listApps } from './files'
import { authorizedFetch, setAuthHooks } from './http'
import { listKeys } from './keys'
import { getOverrides } from './overrides'
import { requestJson, requestVoid } from './request'
import { searchVariables } from './variables'

const base = 'http://api.test'
const json = (status: number, body?: unknown) =>
  new Response(body === undefined ? null : JSON.stringify(body), { status })
const authOf = (init: unknown) => new Headers((init as RequestInit | undefined)?.headers).get('Authorization')

afterEach(() => setAuthHooks(null))

describe('authorizedFetch', () => {
  it('sends no Authorization header without hooks', async () => {
    const f = vi.fn(async (..._a: unknown[]) => json(200, []))
    await authorizedFetch(f as unknown as typeof fetch, `${base}/x`, { headers: { Accept: 'application/json' } })
    expect(authOf(f.mock.calls[0][1])).toBeNull()
  })

  it('attaches the bearer token and keeps the other headers', async () => {
    setAuthHooks({ getAccessToken: () => 'tok' })
    const f = vi.fn(async (..._a: unknown[]) => json(200, []))
    await authorizedFetch(f as unknown as typeof fetch, `${base}/x`, { headers: { Accept: 'application/json' } })
    const h = new Headers((f.mock.calls[0][1] as RequestInit).headers)
    expect(h.get('Authorization')).toBe('Bearer tok')
    expect(h.get('Accept')).toBe('application/json')
  })

  it('skips the token for anonymous calls', async () => {
    setAuthHooks({ getAccessToken: () => 'tok' })
    const f = vi.fn(async (..._a: unknown[]) => json(200, {}))
    await authorizedFetch(f as unknown as typeof fetch, `${base}/x`, {}, { anonymous: true })
    expect(authOf(f.mock.calls[0][1])).toBeNull()
  })

  it('on 401 renews once and retries with the new token', async () => {
    let token = 'old'
    const renew = vi.fn(async () => {
      token = 'new'
      return true
    })
    const onUnauthenticated = vi.fn()
    setAuthHooks({ getAccessToken: () => token, renew, onUnauthenticated })
    const f = vi.fn(async (..._a: unknown[]) => (authOf(_a[1]) === 'Bearer new' ? json(200, []) : json(401)))
    const res = await authorizedFetch(f as unknown as typeof fetch, `${base}/x`)
    expect(res.status).toBe(200)
    expect(renew).toHaveBeenCalledTimes(1)
    expect(f).toHaveBeenCalledTimes(2)
    expect(onUnauthenticated).not.toHaveBeenCalled()
  })

  it('sends the user to login when renew fails or the retry is still 401', async () => {
    const onUnauthenticated = vi.fn()
    setAuthHooks({ getAccessToken: () => 'tok', renew: async () => false, onUnauthenticated })
    const f = vi.fn(async (..._a: unknown[]) => json(401))
    expect((await authorizedFetch(f as unknown as typeof fetch, `${base}/x`)).status).toBe(401)
    expect(f).toHaveBeenCalledTimes(1)
    expect(onUnauthenticated).toHaveBeenCalledTimes(1)

    const again = vi.fn()
    setAuthHooks({ getAccessToken: () => 'tok', renew: async () => true, onUnauthenticated: again })
    const g = vi.fn(async (..._a: unknown[]) => json(401))
    await authorizedFetch(g as unknown as typeof fetch, `${base}/x`)
    expect(g).toHaveBeenCalledTimes(2)
    expect(again).toHaveBeenCalledTimes(1)
  })

  it('treats a throwing renew as failed', async () => {
    const onUnauthenticated = vi.fn()
    setAuthHooks({
      getAccessToken: () => 'tok',
      renew: async () => {
        throw new Error('iframe timeout')
      },
      onUnauthenticated,
    })
    const f = vi.fn(async (..._a: unknown[]) => json(401))
    expect((await authorizedFetch(f as unknown as typeof fetch, `${base}/x`)).status).toBe(401)
    expect(onUnauthenticated).toHaveBeenCalledTimes(1)
  })

  it('shares one renewal between concurrent 401s', async () => {
    let token = 'old'
    const renew = vi.fn(async () => {
      await new Promise((r) => setTimeout(r, 5))
      token = 'new'
      return true
    })
    setAuthHooks({ getAccessToken: () => token, renew })
    const f = vi.fn(async (..._a: unknown[]) => (authOf(_a[1]) === 'Bearer new' ? json(200, []) : json(401)))
    const doFetch = f as unknown as typeof fetch
    const all = await Promise.all([authorizedFetch(doFetch, 'a'), authorizedFetch(doFetch, 'b')])
    expect(all.map((r) => r.status)).toEqual([200, 200])
    expect(renew).toHaveBeenCalledTimes(1)
  })
})

describe('every API client attaches the token', () => {
  const ok = (body: unknown) => vi.fn(async (..._a: unknown[]) => json(200, body))
  const o = (f: ReturnType<typeof ok>) => ({ baseUrl: base, fetchImpl: f as unknown as typeof fetch })

  it.each([
    ['requestJson', (f: ReturnType<typeof ok>) => requestJson(`${base}/x`, {}, o(f), 'x', (_v): _v is unknown => true), {}],
    ['requestVoid', (f: ReturnType<typeof ok>) => requestVoid(`${base}/x`, { method: 'DELETE' }, o(f), 'x'), {}],
    ['files.listApps', (f: ReturnType<typeof ok>) => listApps(o(f)), []],
    ['files.getFile', (f: ReturnType<typeof ok>) => getFile('a', 'default.json', o(f)), '{}'],
    ['config.fetchConfig', (f: ReturnType<typeof ok>) => fetchConfig('a', 'dev', o(f)), {}],
    ['config.fetchConfigText', (f: ReturnType<typeof ok>) => fetchConfigText('a', 'dev', 'yaml', o(f)), 'a: 1'],
    ['environments.list', (f: ReturnType<typeof ok>) => listEnvironments(o(f)), []],
    ['variables.search', (f: ReturnType<typeof ok>) => searchVariables({}, o(f)), { variables: [], warnings: [] }],
    [
      'overrides.get',
      (f: ReturnType<typeof ok>) => getOverrides('a', o(f)),
      { app: 'a', etag: 'e', scopes: [], variables: [], warnings: [] },
    ],
    ['keys.list', (f: ReturnType<typeof ok>) => listKeys(o(f)), []],
  ] as const)('%s', async (_name, call, body) => {
    setAuthHooks({ getAccessToken: async () => 'abc' })
    const f = ok(body)
    await call(f)
    expect(authOf(f.mock.calls[0][1])).toBe('Bearer abc')
  })
})
