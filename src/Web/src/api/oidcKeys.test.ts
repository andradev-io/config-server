import { describe, expect, it, vi } from 'vitest'
import { FilesApiError } from './files'
import {
  createOidcKey,
  deleteOidcKey,
  listOidcKeys,
  OIDC_KEYS_FORBIDDEN_MESSAGE,
  OIDC_KEYS_NOT_CONFIGURED_MESSAGE,
  rotateOidcKey,
  type OidcKey,
} from './oidcKeys'

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

const key = (over: Partial<OidcKey> = {}): OidcKey => ({
  id: 'abc-1',
  clientId: 'cfs-ci-x1y2z3',
  name: 'ci',
  grants: [{ permission: 'read', app: 'web', environment: 'prod', role: 'config:read:web:prod' }],
  enabled: true,
  createdAt: '2026-10-01T10:00:00Z',
  ...over,
})

const issued = (over: Record<string, unknown> = {}) => ({
  client: key(),
  clientId: 'cfs-ci-x1y2z3',
  secret: 's3cret',
  tokenEndpoint: 'https://idp.test/realms/r/protocol/openid-connect/token',
  ...over,
})

describe('oidc keys client', () => {
  it('lists from api/oidc-keys and normalizes', async () => {
    const { createdAt: _c, ...rest } = key()
    const f = mock(res(200, [key(), { ...rest, id: 7 }]))
    const list = await listOidcKeys(opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/oidc-keys')
    expect(list).toHaveLength(2)
    expect(list[1]).toMatchObject({ id: '7', createdAt: null })
  })

  it.each([
    ['not an array', { a: 1 }],
    ['missing client id', [{ ...key(), clientId: undefined }]],
    ['grants not an array', [{ ...key(), grants: 'x' }]],
    ['bad grant', [{ ...key(), grants: [{ permission: 'owner', app: 'a', environment: 'b', role: 'r' }] }]],
    ['enabled not boolean', [{ ...key(), enabled: 'yes' }]],
  ])('rejects malformed list (%s)', async (_n, body) => {
    expect((await errOf(listOidcKeys(opts(mock(res(200, body)))))).kind).toBe('invalid-response')
  })

  it('creates with a JSON body and returns the one-time secret and token endpoint', async () => {
    const f = mock(res(201, issued()))
    const req = { name: 'ci', grants: [{ permission: 'read' as const, app: 'web', environment: 'prod' }] }
    const out = await createOidcKey(req, opts(f))
    expect(out.secret).toBe('s3cret')
    expect(out.clientId).toBe('cfs-ci-x1y2z3')
    expect(out.tokenEndpoint).toContain('openid-connect/token')
    expect(out.client.grants[0].role).toBe('config:read:web:prod')
    const init = f.mock.calls[0][1] as RequestInit
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/oidc-keys')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual(req)
  })

  it('accepts a missing token endpoint', async () => {
    const out = await createOidcKey({ name: 'ci', grants: [{ permission: 'read' }] }, opts(mock(res(201, issued({ tokenEndpoint: undefined })))))
    expect(out.tokenEndpoint).toBeNull()
  })

  it('rejects an issued response without a secret', async () => {
    const e = await errOf(createOidcKey({ name: 'ci', grants: [{ permission: 'read' }] }, opts(mock(res(201, issued({ secret: '' }))))))
    expect(e.kind).toBe('invalid-response')
  })

  it('rotates with POST {id}/rotate (id URL-encoded)', async () => {
    const f = mock(res(200, issued({ secret: 'new' })))
    const out = await rotateOidcKey('a/b c', opts(f))
    expect(out.secret).toBe('new')
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/oidc-keys/a%2Fb%20c/rotate')
    expect((f.mock.calls[0][1] as RequestInit).method).toBe('POST')
  })

  it('deletes with DELETE {id}', async () => {
    const f = mock(res(204))
    await deleteOidcKey('abc-1', opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/oidc-keys/abc-1')
    expect((f.mock.calls[0][1] as RequestInit).method).toBe('DELETE')
  })

  it('maps 501 to the not-configured message for every call', async () => {
    for (const call of [
      (o: ReturnType<typeof opts>) => listOidcKeys(o),
      (o: ReturnType<typeof opts>) => createOidcKey({ name: 'ci', grants: [{ permission: 'read' }] }, o),
      (o: ReturnType<typeof opts>) => rotateOidcKey('x', o),
      (o: ReturnType<typeof opts>) => deleteOidcKey('x', o),
    ]) {
      const e = await errOf(call(opts(mock(res(501, { error: 'x' })))))
      expect(e).toMatchObject({ kind: 'read-only', status: 501, message: OIDC_KEYS_NOT_CONFIGURED_MESSAGE })
    }
  })

  it('explains a bare 403 and keeps a specific 403 / 502 message', async () => {
    expect((await errOf(listOidcKeys(opts(mock(res(403)))))).message).toBe(OIDC_KEYS_FORBIDDEN_MESSAGE)
    const e = await errOf(listOidcKeys(opts(mock(res(502, { error: "lacks 'manage-clients'" })))))
    expect(e).toMatchObject({ kind: 'server', message: "lacks 'manage-clients'" })
  })

  it('reports validation errors from the server', async () => {
    const e = await errOf(createOidcKey({ name: '', grants: [] }, opts(mock(res(400, { error: 'name is required' })))))
    expect(e).toMatchObject({ kind: 'bad-request', message: 'name is required' })
  })
})
