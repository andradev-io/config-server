import { describe, expect, it, vi } from 'vitest'
import { FilesApiError } from './files'
import { getOverrides, putOverrides } from './overrides'

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

const doc = {
  app: 'my app',
  etag: 'e1',
  scopes: ['default', 'dev'],
  variables: [
    {
      key: 'Db.Host',
      type: 'string',
      secret: false,
      declared: true,
      values: { default: { value: 'x', format: 'json', file: 'default.json' } },
      effective: { dev: { value: 'x', from: 'default' } },
    },
    {
      key: 'Db.Pass',
      type: 'string',
      secret: true,
      declared: false,
      values: { default: { masked: true, file: 'default.json' } },
      effective: { dev: { masked: true, from: 'default' } },
    },
  ],
  warnings: ['w'],
}

describe('getOverrides', () => {
  it('gets and encodes the app', async () => {
    const f = mock(res(200, doc))
    const r = await getOverrides('my app', opts(f))
    expect(r.etag).toBe('e1')
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/apps/my%20app/overrides')
  })

  it('defaults missing warnings', async () => {
    const { warnings: _w, ...noWarn } = doc
    expect((await getOverrides('a', opts(mock(res(200, noWarn))))).warnings).toEqual([])
  })

  it.each([
    ['non-object', []],
    ['missing etag', { ...doc, etag: undefined }],
    ['bad type', { ...doc, variables: [{ ...doc.variables[0], type: 'date' }] }],
    ['bad value entry', { ...doc, variables: [{ ...doc.variables[0], values: { default: { value: 1, format: 'toml', file: 'f' } } }] }],
    ['bad effective', { ...doc, variables: [{ ...doc.variables[0], effective: { dev: { value: 1 } } }] }],
    ['bad scopes', { ...doc, scopes: [1] }],
  ])('rejects %s', async (_n, body) => {
    expect((await errOf(getOverrides('a', opts(mock(res(200, body)))))).kind).toBe('invalid-response')
  })

  it.each([
    [400, 'bad-request'],
    [403, 'forbidden'],
    [404, 'not-found'],
    [409, 'conflict'],
    [501, 'read-only'],
    [500, 'server'],
  ])('maps %i', async (s, kind) => {
    expect((await errOf(getOverrides('a', opts(mock(res(s)))))).kind).toBe(kind)
  })

  it('maps network failures', async () => {
    const f = vi.fn(async () => {
      throw new TypeError('x')
    })
    expect((await errOf(getOverrides('a', { baseUrl: base, fetchImpl: f as unknown as typeof fetch }))).kind).toBe('network')
  })
})

describe('putOverrides', () => {
  const ok = { files: [{ name: 'dev.json', created: true, changedKeys: ['A'] }], warnings: ['comments lost'] }
  const body = { ifMatch: 'e1', changes: [{ key: 'A', scope: 'dev', value: 1 }, { key: 'B', scope: 'dev', remove: true }] }

  it('sends dryRun=true with the body', async () => {
    const f = mock(res(200, ok))
    const r = await putOverrides('a', body, true, opts(f))
    expect(r.files[0].created).toBe(true)
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/apps/a/overrides?dryRun=true')
    const init = f.mock.calls[0][1] as RequestInit
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body as string)).toEqual(body)
  })

  it('sends dryRun=false', async () => {
    const f = mock(res(200, ok))
    await putOverrides('a', body, false, opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/apps/a/overrides?dryRun=false')
  })

  it('defaults warnings and rejects malformed results', async () => {
    expect((await putOverrides('a', body, true, opts(mock(res(200, { files: [] }))))).warnings).toEqual([])
    expect((await errOf(putOverrides('a', body, true, opts(mock(res(200, { files: [{ name: 1 }] })))))).kind).toBe(
      'invalid-response',
    )
  })

  it('maps 400 with message, 403, 409, 501', async () => {
    expect(await errOf(putOverrides('a', body, true, opts(mock(res(400, { error: 'bad key', key: 'A', scope: 'dev' })))))).toMatchObject({
      kind: 'bad-request',
      message: 'bad key',
    })
    expect((await errOf(putOverrides('a', body, false, opts(mock(res(403)))))).message).toBe(
      'Writes are disabled on this server (ConfigSource:AllowWrite)',
    )
    expect(await errOf(putOverrides('a', body, false, opts(mock(res(409, { error: 'etag mismatch' })))))).toMatchObject({
      kind: 'conflict',
      message: 'etag mismatch',
    })
    expect((await errOf(putOverrides('a', body, false, opts(mock(res(501)))))).message).toBe('This config source is read-only')
  })
})
