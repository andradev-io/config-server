import { describe, expect, it, vi } from 'vitest'
import { FilesApiError } from './files'
import {
  getAppStatus,
  getAppVariables,
  getVariable,
  putAppVariables,
  searchVariables,
  variableKeyPath,
  type DeclaredVariable,
} from './variables'

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

const decl: DeclaredVariable = { key: 'Db.Host', type: 'string', required: true, secret: false, description: '', environments: null }
const used = { app: 'a', required: true, secret: false, declared: true, definedIn: ['default.json'], environments: ['dev'] }

describe('app variables client', () => {
  it('gets declared variables', async () => {
    const f = mock(res(200, { variables: [decl] }))
    const v = await getAppVariables('my app', opts(f))
    expect(v).toEqual([decl])
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/apps/my%20app/variables')
  })

  it('accepts the bare array the server actually returns', async () => {
    const v = await getAppVariables('a', opts(mock(res(200, [decl]))))
    expect(v).toEqual([decl])
  })

  it('rejects invalid type', async () => {
    const e = await errOf(getAppVariables('a', opts(mock(res(200, { variables: [{ ...decl, type: 'date' }] })))))
    expect(e.kind).toBe('invalid-response')
  })

  it('puts the variables body', async () => {
    const f = mock(res(204))
    await putAppVariables('a', [decl], opts(f))
    const init = f.mock.calls[0][1] as RequestInit
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body as string)).toEqual({ variables: [decl] })
  })

  it('put maps 400 message, 403 and 501', async () => {
    expect(await errOf(putAppVariables('a', [], opts(mock(res(400, { error: 'dup key' })))))).toMatchObject({
      kind: 'bad-request',
      message: 'dup key',
    })
    expect((await errOf(putAppVariables('a', [], opts(mock(res(403)))))).message).toBe(
      'Writes are disabled on this server (ConfigSource:AllowWrite)',
    )
    expect((await errOf(putAppVariables('a', [], opts(mock(res(501)))))).message).toBe('This config source is read-only')
  })
})

describe('status client', () => {
  const status = {
    app: 'a',
    environments: [
      { name: 'dev', ok: true, missing: [], typeMismatches: [], secretsInPlainText: [] },
      {
        name: 'prod',
        ok: false,
        missing: ['X'],
        typeMismatches: [{ key: 'P', expected: 'number', actual: 'string' }],
        secretsInPlainText: ['S'],
        error: 'boom',
      },
    ],
    undeclared: ['U'],
  }

  it('gets status', async () => {
    const f = mock(res(200, status))
    expect(await getAppStatus('a', opts(f))).toEqual(status)
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/apps/a/status')
  })

  it('rejects malformed status', async () => {
    expect((await errOf(getAppStatus('a', opts(mock(res(200, { app: 'a' })))))).kind).toBe('invalid-response')
  })

  it.each([
    [404, 'not-found'],
    [500, 'server'],
    [403, 'forbidden'],
    [501, 'read-only'],
  ])('maps %i', async (s, kind) => {
    expect((await errOf(getAppStatus('a', opts(mock(res(s)))))).kind).toBe(kind)
  })
})

describe('index client', () => {
  const body = { variables: [{ key: 'Db.Host', type: 'string', appCount: 1, usedBy: [used] }], warnings: [{ app: 'a', file: 'x.json', error: 'bad' }] }

  it('builds the query string', async () => {
    const f = mock(res(200, body))
    const r = await searchVariables({ search: 'db host', app: 'a', env: 'dev', declaredOnly: true }, opts(f))
    expect(r.warnings).toHaveLength(1)
    const url = new URL(f.mock.calls[0][0] as string)
    expect(url.pathname).toBe('/api/variables')
    expect(Object.fromEntries(url.searchParams)).toEqual({ search: 'db host', app: 'a', env: 'dev', declaredOnly: 'true' })
  })

  it('omits empty filters and defaults warnings', async () => {
    const f = mock(res(200, { variables: [] }))
    const r = await searchVariables({ search: '', declaredOnly: false }, opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/variables')
    expect(r.warnings).toEqual([])
  })

  it('rejects malformed bodies and maps errors', async () => {
    expect((await errOf(searchVariables({}, opts(mock(res(200, { variables: [{ key: 1 }] })))))).kind).toBe('invalid-response')
    expect((await errOf(searchVariables({}, opts(mock(res(500)))))).kind).toBe('server')
  })
})

describe('variable detail client', () => {
  it('encodes each dotted segment', () => {
    expect(variableKeyPath('Db.Host name.a/b')).toBe('Db.Host%20name.a%2Fb')
  })

  it('gets a variable with byEnvironment', async () => {
    const entry = { key: 'Db.Host', type: 'string', appCount: 1, usedBy: [{ ...used, byEnvironment: { dev: 'defined', prod: 'missing', qa: 'inherited' } }] }
    const f = mock(res(200, entry))
    const v = await getVariable('Db.Host', opts(f))
    expect(v.usedBy[0].byEnvironment?.qa).toBe('inherited')
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/variables/Db.Host')
  })

  it('maps 404 and rejects invalid cell states', async () => {
    expect((await errOf(getVariable('X', opts(mock(res(404)))))).kind).toBe('not-found')
    const bad = { key: 'k', type: 'string', appCount: 1, usedBy: [{ ...used, byEnvironment: { dev: 'weird' } }] }
    expect((await errOf(getVariable('k', opts(mock(res(200, bad)))))).kind).toBe('invalid-response')
  })

  it('maps network failures', async () => {
    const f = vi.fn(async () => {
      throw new TypeError('x')
    })
    expect((await errOf(getVariable('k', { baseUrl: base, fetchImpl: f as unknown as typeof fetch }))).kind).toBe('network')
  })
})
