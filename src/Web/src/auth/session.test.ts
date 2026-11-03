import { afterEach, describe, expect, it, vi } from 'vitest'
import { authState, grants, initAuth, permissions, resetAuthForTests } from './session'

const res = (status: number, body?: unknown) => new Response(body === undefined ? null : JSON.stringify(body), { status })

function stubFetch(routes: Record<string, () => Response | Promise<Response>>) {
  const f = vi.fn(async (url: string) => {
    const hit = Object.entries(routes).find(([path]) => url.endsWith(path))
    if (!hit) throw new Error(`unexpected ${url}`)
    return hit[1]()
  })
  vi.stubGlobal('fetch', f)
  return f
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetAuthForTests()
})

describe('initAuth', () => {
  it('mode none: no login, permissions from api/auth/me', async () => {
    stubFetch({
      '/api/auth/info': () => res(200, { mode: 'none', oidc: null, apiKeys: true }),
      '/api/auth/me': () => res(200, { name: 'anonymous', roles: ['config:admin'], via: 'none' }),
    })
    await initAuth()
    expect(authState.status).toBe('authenticated')
    expect(authState.mode).toBe('none')
    expect(permissions.value).toEqual({ read: true, write: true, admin: true })
  })

  it('a server without the auth endpoints behaves as mode none with full access', async () => {
    stubFetch({ '/api/auth/info': () => res(404), '/api/auth/me': () => res(404) })
    await initAuth()
    expect(authState.status).toBe('authenticated')
    expect(permissions.value.admin).toBe(true)
  })

  it('read-only user: write and admin are off', async () => {
    stubFetch({
      '/api/auth/info': () => res(200, { mode: 'none', oidc: null, apiKeys: true }),
      '/api/auth/me': () => res(200, { name: 'x', roles: ['config:read'], via: 'none' }),
    })
    await initAuth()
    expect(permissions.value).toEqual({ read: true, write: false, admin: false })
  })

  it('mode none without the me endpoint is a global admin grant', async () => {
    stubFetch({ '/api/auth/info': () => res(200, { mode: 'none', oidc: null, apiKeys: false }), '/api/auth/me': () => res(404) })
    await initAuth()
    expect(grants.value).toEqual([{ permission: 'admin', app: '*', environment: '*' }])
  })

  it('exposes the server grants; write somewhere is not admin', async () => {
    stubFetch({
      '/api/auth/info': () => res(200, { mode: 'none', oidc: null, apiKeys: true }),
      '/api/auth/me': () =>
        res(200, {
          name: 'x',
          roles: ['config:write:billing:dev'],
          via: 'none',
          grants: [{ permission: 'write', app: 'billing', environment: 'dev' }],
        }),
    })
    await initAuth()
    expect(grants.value).toEqual([{ permission: 'write', app: 'billing', environment: 'dev' }])
    expect(permissions.value).toEqual({ read: true, write: true, admin: false })
  })

  it('derives the grants from scoped roles when the server sends none', async () => {
    stubFetch({
      '/api/auth/info': () => res(200, { mode: 'none', oidc: null, apiKeys: true }),
      '/api/auth/me': () => res(200, { name: 'x', roles: ['config:read:orders', 'config:admin:orders'], via: 'none' }),
    })
    await initAuth()
    expect(grants.value).toEqual([{ permission: 'read', app: 'orders', environment: '*' }])
    expect(permissions.value).toEqual({ read: true, write: false, admin: false })
  })

  it('no grants at all: no access', async () => {
    stubFetch({
      '/api/auth/info': () => res(200, { mode: 'none', oidc: null, apiKeys: true }),
      '/api/auth/me': () => res(200, { name: 'x', roles: [], via: 'none', grants: [] }),
    })
    await initAuth()
    expect(authState.status).toBe('authenticated')
    expect(grants.value).toEqual([])
    expect(permissions.value).toEqual({ read: false, write: false, admin: false })
  })

  it('reports an unreachable server', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('Failed to fetch')
      }),
    )
    await initAuth()
    expect(authState.status).toBe('error')
    expect(authState.error).toMatch(/reach/i)
  })

  it('reports an unexpected auth info response', async () => {
    stubFetch({ '/api/auth/info': () => res(200, { mode: 'magic' }) })
    await initAuth()
    expect(authState.status).toBe('error')
  })
})
