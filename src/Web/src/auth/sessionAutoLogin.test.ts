import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const manager = {
  getUser: vi.fn(async () => null as unknown),
  signinRedirect: vi.fn(async () => undefined),
  signoutRedirect: vi.fn(async () => undefined),
  signinSilent: vi.fn(async () => null),
  signinCallback: vi.fn(async () => null),
  removeUser: vi.fn(async () => undefined),
}
vi.mock('./oidc', () => ({ createUserManager: () => manager }))

import { authState, initAuth, logout, resetAuthForTests } from './session'

const res = (status: number, body?: unknown) => new Response(body === undefined ? null : JSON.stringify(body), { status })

function stubBrowser(search = '') {
  const store = new Map<string, string>()
  vi.stubGlobal('sessionStorage', {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
    clear: () => store.clear(),
  })
  vi.stubGlobal('window', {
    location: { search, href: `http://localhost:5173/${search}` },
    history: { state: null, replaceState: () => undefined },
  })
}

beforeEach(() => {
  stubBrowser()
  Object.values(manager).forEach((m) => m.mockClear())
  manager.getUser.mockResolvedValue(null)
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) =>
      url.endsWith('/api/auth/info')
        ? res(200, { mode: 'oidc', oidc: { authority: 'http://idp', clientId: 'web', scopes: ['openid'] }, apiKeys: true })
        : res(401),
    ),
  )
})

afterEach(() => {
  vi.unstubAllGlobals()
  resetAuthForTests()
})

describe('automatic sign-in redirect', () => {
  it('redirects to the identity provider when there is no session', async () => {
    await initAuth()
    expect(manager.signinRedirect).toHaveBeenCalledTimes(1)
  })

  it('does not redirect again within the loop-guard window', async () => {
    await initAuth()
    await initAuth()
    expect(manager.signinRedirect).toHaveBeenCalledTimes(1)
    expect(authState.status).toBe('unauthenticated')
  })

  it('shows the sign-in screen (no redirect) right after an explicit sign-out', async () => {
    manager.getUser.mockResolvedValue({ expired: false, access_token: 't' })
    await initAuth() // creates the manager (signed in: no redirect, /me answers 401 in this stub)
    manager.signinRedirect.mockClear()
    await logout()
    expect(manager.signoutRedirect).toHaveBeenCalled()
    manager.getUser.mockResolvedValue(null)
    await initAuth()
    expect(manager.signinRedirect).not.toHaveBeenCalled()
    expect(authState.status).toBe('unauthenticated')
    expect(authState.notice).toBe('You are signed out.')
  })

  it('does not redirect when the sign-in callback failed', async () => {
    stubBrowser('?error=access_denied&state=x')
    manager.signinCallback.mockRejectedValueOnce(new Error('denied'))
    await initAuth()
    expect(manager.signinRedirect).not.toHaveBeenCalled()
    expect(authState.notice).toContain('Sign-in failed')
  })
})
