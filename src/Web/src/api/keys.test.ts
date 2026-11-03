import { describe, expect, it, vi } from 'vitest'
import { FilesApiError } from './files'
import { createKey, KEYS_FORBIDDEN_MESSAGE, listKeys, revokeKey, rotateKey, type ApiKey } from './keys'

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

const key = (over: Partial<ApiKey> = {}): ApiKey => ({
  id: 'k1',
  name: 'ci',
  prefix: 'ab12',
  apps: ['*'],
  environments: ['*'],
  permission: 'read',
  createdAt: '2026-10-01T10:00:00Z',
  expiresAt: null,
  revokedAt: null,
  lastUsedAt: null,
  status: 'active',
  ...over,
})

describe('keys client', () => {
  it('lists keys from api/keys', async () => {
    const f = mock(res(200, [key(), key({ id: 'k2', status: 'revoked', revokedAt: '2026-10-02T00:00:00Z' })]))
    const list = await listKeys(opts(f))
    expect(list).toHaveLength(2)
    expect(list[1]).toMatchObject({ id: 'k2', status: 'revoked' })
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/keys')
  })

  it('normalizes numeric ids and missing nullable dates', async () => {
    const { expiresAt: _e, lastUsedAt: _l, ...rest } = key()
    const list = await listKeys(opts(mock(res(200, [{ ...rest, id: 7 }]))))
    expect(list[0]).toMatchObject({ id: '7', expiresAt: null, lastUsedAt: null })
  })

  it.each([
    ['not an array', { a: 1 }],
    ['bad status', [{ ...key(), status: 'paused' }]],
    ['bad permission', [{ ...key(), permission: 'admin' }]],
    ['apps not strings', [{ ...key(), apps: [1] }]],
    ['missing prefix', [{ ...key(), prefix: undefined }]],
  ])('rejects malformed list (%s)', async (_n, body) => {
    expect((await errOf(listKeys(opts(mock(res(200, body)))))).kind).toBe('invalid-response')
  })

  it('creates with a JSON body and returns the one-time secret', async () => {
    const f = mock(res(201, { key: key({ permission: 'write' }), secret: 'cfs_ab12_secret' }))
    const req = { name: 'ci', apps: ['billing'], environments: ['*'], permission: 'write' as const, expiresAt: '2027-01-01T00:00:00.000Z' }
    const out = await createKey(req, opts(f))
    expect(out.secret).toBe('cfs_ab12_secret')
    expect(out.key.permission).toBe('write')
    const init = f.mock.calls[0][1] as RequestInit
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/keys')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual(req)
  })

  it('rejects an issued key without a secret', async () => {
    const e = await errOf(createKey({ name: 'x' }, opts(mock(res(201, { key: key(), secret: '' })))))
    expect(e.kind).toBe('invalid-response')
  })

  it('revokes with DELETE and encodes the id', async () => {
    const f = mock(res(204))
    await revokeKey('a/b', opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/keys/a%2Fb')
    expect((f.mock.calls[0][1] as RequestInit).method).toBe('DELETE')
  })

  it('rotates with POST and returns the new secret', async () => {
    const f = mock(res(200, { key: key(), secret: 'cfs_ab12_new' }))
    const out = await rotateKey('k1', opts(f))
    expect(out.secret).toBe('cfs_ab12_new')
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/keys/k1/rotate')
    expect((f.mock.calls[0][1] as RequestInit).method).toBe('POST')
  })

  it('maps 400 {error}', async () => {
    const e = await errOf(createKey({ name: '' }, opts(mock(res(400, { error: 'Name is required.' })))))
    expect(e).toMatchObject({ kind: 'bad-request', message: 'Name is required.', status: 400 })
  })

  it('maps an empty 403 to the admin-role message (not the AllowWrite one)', async () => {
    const e = await errOf(listKeys(opts(mock(res(403)))))
    expect(e).toMatchObject({ kind: 'forbidden', message: KEYS_FORBIDDEN_MESSAGE })
  })

  it('keeps a specific 403 message from the server', async () => {
    const e = await errOf(revokeKey('k1', opts(mock(res(403, { error: 'Requires config:admin.' })))))
    expect(e.message).toBe('Requires config:admin.')
  })

  it.each([
    [401, 'unauthorized'],
    [404, 'not-found'],
    [500, 'server'],
  ])('maps status %i', async (status, kind) => {
    expect((await errOf(rotateKey('k1', opts(mock(res(status)))))).kind).toBe(kind)
  })

  it('maps network failures', async () => {
    const f = vi.fn(async () => {
      throw new TypeError('fail')
    })
    const e = await errOf(listKeys({ baseUrl: base, fetchImpl: f as unknown as typeof fetch }))
    expect(e.kind).toBe('network')
  })
})
