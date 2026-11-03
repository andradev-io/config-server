import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FilesApiError } from '../api/files'
import { setAuthHooks } from '../api/http'
import { getAuthInfo, getMe } from './api'

const base = 'http://api.test/'
const res = (status: number, body?: unknown) => new Response(body === undefined ? null : JSON.stringify(body), { status })
const mock = (r: Response) => vi.fn(async (..._a: unknown[]) => r)
const opts = (f: ReturnType<typeof mock>) => ({ baseUrl: base, fetchImpl: f as unknown as typeof fetch })

async function errOf(p: Promise<unknown>) {
  try {
    await p
  } catch (e) {
    return e as FilesApiError
  }
  throw new Error('expected rejection')
}

afterEach(() => setAuthHooks(null))

describe('auth info client', () => {
  it('reads mode none', async () => {
    const f = mock(res(200, { mode: 'none', oidc: null, apiKeys: true }))
    expect(await getAuthInfo(opts(f))).toEqual({ mode: 'none', oidc: null, apiKeys: true })
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/auth/info')
  })

  it('reads mode oidc', async () => {
    const oidc = { authority: 'https://idp.test/realms/x', clientId: 'config-web', scopes: ['openid', 'profile'] }
    const info = await getAuthInfo(opts(mock(res(200, { mode: 'oidc', oidc, apiKeys: true }))))
    expect(info).toEqual({ mode: 'oidc', oidc, apiKeys: true })
  })

  it('reads the demo block when the server is a sandbox', async () => {
    const body = { mode: 'none', oidc: null, apiKeys: false, demo: { enabled: true, idleTtlMinutes: 60, notice: ' Hi ' } }
    const info = await getAuthInfo(opts(mock(res(200, body))))
    expect(info.demo).toEqual({ enabled: true, idleTtlMinutes: 60, notice: 'Hi' })
  })

  it.each([[undefined], [null], [{ enabled: false }], ['yes'], [{ idleTtlMinutes: 5 }]])('ignores demo=%j', async (demo) => {
    const info = await getAuthInfo(opts(mock(res(200, { mode: 'none', oidc: null, apiKeys: false, demo }))))
    expect(info.demo).toBeUndefined()
  })

  it('normalises a bad idle ttl to 0', async () => {
    const body = { mode: 'none', oidc: null, apiKeys: false, demo: { enabled: true, idleTtlMinutes: -3 } }
    const info = await getAuthInfo(opts(mock(res(200, body))))
    expect(info.demo).toEqual({ enabled: true, idleTtlMinutes: 0, notice: null })
  })

  it('is anonymous (never sends the token)', async () => {
    setAuthHooks({ getAccessToken: () => 'tok' })
    const f = mock(res(200, { mode: 'none', oidc: null, apiKeys: false }))
    await getAuthInfo(opts(f))
    expect(new Headers((f.mock.calls[0][1] as RequestInit).headers).get('Authorization')).toBeNull()
  })

  it.each([
    ['unknown mode', { mode: 'saml', oidc: null }],
    ['oidc without settings', { mode: 'oidc', oidc: null }],
    ['oidc without client id', { mode: 'oidc', oidc: { authority: 'https://idp', clientId: '', scopes: [] } }],
    ['scopes not strings', { mode: 'oidc', oidc: { authority: 'https://idp', clientId: 'c', scopes: [1] } }],
  ])('rejects %s', async (_n, body) => {
    expect((await errOf(getAuthInfo(opts(mock(res(200, body)))))).kind).toBe('invalid-response')
  })

  it('maps 404 (server without auth endpoints)', async () => {
    expect((await errOf(getAuthInfo(opts(mock(res(404)))))).kind).toBe('not-found')
  })
})

describe('me client', () => {
  it('reads the current user and sends the token', async () => {
    setAuthHooks({ getAccessToken: () => 'tok' })
    const f = mock(res(200, { name: 'Ana', roles: ['config:read'], via: 'oidc' }))
    expect(await getMe(opts(f))).toEqual({ name: 'Ana', roles: ['config:read'], via: 'oidc' })
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/auth/me')
    expect(new Headers((f.mock.calls[0][1] as RequestInit).headers).get('Authorization')).toBe('Bearer tok')
  })

  it('accepts a missing name', async () => {
    expect((await getMe(opts(mock(res(200, { name: null, roles: [], via: 'none' }))))).name).toBe('')
  })

  it('rejects malformed bodies', async () => {
    expect((await errOf(getMe(opts(mock(res(200, { name: 'a', roles: 'x', via: 'oidc' })))))).kind).toBe(
      'invalid-response',
    )
    expect((await errOf(getMe(opts(mock(res(200, { name: 'a', roles: [], via: 'cookie' })))))).kind).toBe(
      'invalid-response',
    )
  })

  it('maps 401 to unauthorized', async () => {
    expect((await errOf(getMe(opts(mock(res(401)))))).kind).toBe('unauthorized')
  })

  it('reads the scoped grants', async () => {
    const body = {
      name: 'Ana',
      roles: ['config:write:billing:dev'],
      via: 'oidc',
      grants: [
        { permission: 'write', app: 'billing', environment: 'dev' },
        { permission: 'Read', app: '*', environment: '*' },
      ],
    }
    expect((await getMe(opts(mock(res(200, body))))).grants).toEqual([
      { permission: 'write', app: 'billing', environment: 'dev' },
      { permission: 'read', app: '*', environment: '*' },
    ])
  })

  it('leaves grants out for older servers (derived from roles later)', async () => {
    const me = await getMe(opts(mock(res(200, { name: 'Ana', roles: ['config:read'], via: 'oidc' }))))
    expect('grants' in me).toBe(false)
    expect((await getMe(opts(mock(res(200, { name: 'a', roles: [], via: 'none', grants: null }))))).grants).toBeUndefined()
  })

  it('rejects malformed grants', async () => {
    for (const grants of ['x', [{ permission: 'owner', app: 'a', environment: 'b' }], [{ permission: 'read', app: 'a' }]]) {
      const body = { name: 'a', roles: [], via: 'oidc', grants }
      expect((await errOf(getMe(opts(mock(res(200, body))))))).toMatchObject({ kind: 'invalid-response' })
    }
  })
})
