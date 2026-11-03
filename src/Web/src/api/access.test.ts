import { describe, expect, it, vi } from 'vitest'
import { FilesApiError } from './files'
import {
  ACCESS_FORBIDDEN_MESSAGE,
  ACCESS_NOT_CONFIGURED_MESSAGE,
  ACCESS_UNSUPPORTED_NOTE,
  addGrant,
  getAccessInfo,
  listGrants,
  listGroups,
  listUsers,
  removeGrant,
  subjectQuery,
  type SubjectGrant,
} from './access'

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

const grant = (over: Partial<SubjectGrant> = {}): SubjectGrant => ({
  permission: 'write',
  app: 'billing',
  environment: 'dev',
  role: 'config:write:billing:dev',
  inherited: false,
  ...over,
})

describe('subjectQuery', () => {
  it('builds search / first / max and leaves empty values out', () => {
    expect(subjectQuery()).toBe('')
    expect(subjectQuery({ search: '  ' })).toBe('')
    expect(subjectQuery({ search: ' ana maria ', first: 50, max: 25 })).toBe('?search=ana+maria&first=50&max=25')
    expect(subjectQuery({ first: 0, max: 10 })).toBe('?max=10')
    expect(subjectQuery({ search: 'a&b=c' })).toBe('?search=a%26b%3Dc')
    expect(subjectQuery({ first: Number.NaN, max: -1 })).toBe('')
  })
})

describe('access client', () => {
  it('reads the info', async () => {
    const f = mock(res(200, { provider: 'keycloak', enabled: true, configClientId: 'config-server' }))
    expect(await getAccessInfo(opts(f))).toEqual({ provider: 'keycloak', enabled: true, configClientId: 'config-server' })
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/access/info')
  })

  it('drops null optional info fields', async () => {
    const f = mock(res(200, { provider: 'none', enabled: false, configClientId: null, note: null }))
    expect(await getAccessInfo(opts(f))).toEqual({ provider: 'none', enabled: false })
  })

  it('treats a server without api/access (404) as disabled', async () => {
    expect(await getAccessInfo(opts(mock(res(404))))).toEqual({
      provider: 'none',
      enabled: false,
      note: ACCESS_UNSUPPORTED_NOTE,
    })
  })

  it('rejects a malformed info', async () => {
    expect((await errOf(getAccessInfo(opts(mock(res(200, { provider: 'none' }))))))).toMatchObject({
      kind: 'invalid-response',
    })
  })

  it('lists users with the query and normalizes ids and missing fields', async () => {
    const f = mock(res(200, [{ id: 7, username: 'ana', displayName: null }, { id: 'u2', username: 'bo', displayName: 'Bo', email: 'b@x' }]))
    const list = await listUsers({ search: 'an', first: 0, max: 50 }, opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/access/users?search=an&max=50')
    expect(list).toEqual([
      { id: '7', username: 'ana', displayName: '', email: '' },
      { id: 'u2', username: 'bo', displayName: 'Bo', email: 'b@x' },
    ])
  })

  it('lists groups', async () => {
    const f = mock(res(200, [{ id: 'g1', name: 'devs', path: '/eng/devs' }, { id: 'g2', name: 'ops' }]))
    const list = await listGroups({ search: 'd', first: 25, max: 25 }, opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/access/groups?search=d&first=25&max=25')
    expect(list[1]).toEqual({ id: 'g2', name: 'ops', path: '' })
  })

  it.each([
    ['not an array', { a: 1 }],
    ['user without username', [{ id: 'u1' }]],
  ])('rejects malformed user lists (%s)', async (_n, body) => {
    expect((await errOf(listUsers({}, opts(mock(res(200, body)))))).kind).toBe('invalid-response')
  })

  it('lists the grants of a subject (encoded id)', async () => {
    const f = mock(
      res(200, [grant(), grant({ permission: 'read', app: '*', environment: '*', role: 'config:read', inherited: true, via: '/eng' })]),
    )
    const list = await listGrants('user', 'a/b c', opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/access/subjects/user/a%2Fb%20c/grants')
    expect(list).toHaveLength(2)
    expect(list[1]).toMatchObject({ inherited: true, via: '/eng' })
    expect(list[0]).not.toHaveProperty('via')
  })

  it.each([
    ['bad permission', [{ ...grant(), permission: 'owner' }]],
    ['empty app', [{ ...grant(), app: '' }]],
    ['missing inherited', [{ ...grant(), inherited: undefined }]],
  ])('rejects malformed grants (%s)', async (_n, body) => {
    expect((await errOf(listGrants('group', 'g1', opts(mock(res(200, body)))))).kind).toBe('invalid-response')
  })

  it('adds a grant with a JSON body (201)', async () => {
    const f = mock(res(201, grant({ permission: 'read', environment: '*', role: 'config:read:billing' })))
    const out = await addGrant('group', 'g1', { permission: 'read', app: 'billing' }, opts(f))
    expect(out.role).toBe('config:read:billing')
    const init = f.mock.calls[0][1] as RequestInit
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/access/subjects/group/g1/grants')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ permission: 'read', app: 'billing' })
  })

  it('removes a grant with DELETE and the scope in the query ("*" kept)', async () => {
    const f = mock(res(204))
    await removeGrant('user', 'u1', { permission: 'read', app: '*', environment: 'prd' }, opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/access/subjects/user/u1/grants?permission=read&app=*&environment=prd')
    expect((f.mock.calls[0][1] as RequestInit).method).toBe('DELETE')
  })

  it('maps 501 to "not configured" on every endpoint', async () => {
    for (const call of [
      (o: ReturnType<typeof opts>) => getAccessInfo(o),
      (o: ReturnType<typeof opts>) => listUsers({}, o),
      (o: ReturnType<typeof opts>) => listGroups({}, o),
      (o: ReturnType<typeof opts>) => listGrants('user', 'u1', o),
      (o: ReturnType<typeof opts>) => addGrant('user', 'u1', { permission: 'read' }, o),
      (o: ReturnType<typeof opts>) => removeGrant('user', 'u1', grant(), o),
    ]) {
      const e = await errOf(call(opts(mock(res(501, { error: 'IdentityAdmin is not configured' })))))
      expect(e).toBeInstanceOf(FilesApiError)
      expect(e).toMatchObject({ status: 501, message: ACCESS_NOT_CONFIGURED_MESSAGE })
    }
  })

  it('uses the admin message for a bare 403 and keeps a specific one', async () => {
    expect((await errOf(listUsers({}, opts(mock(res(403))))))).toMatchObject({ kind: 'forbidden', message: ACCESS_FORBIDDEN_MESSAGE })
    const e = await errOf(listGroups({}, opts(mock(res(403, { error: 'Missing role config:admin' })))))
    expect(e.message).toBe('Missing role config:admin')
  })

  it('surfaces the server {error} on 400 and 409', async () => {
    const bad = await errOf(addGrant('group', 'g1', { permission: 'write', app: 'nope' }, opts(mock(res(400, { error: 'Unknown application nope' })))))
    expect(bad).toMatchObject({ kind: 'bad-request', message: 'Unknown application nope' })
    const conflict = await errOf(addGrant('group', 'g1', { permission: 'read' }, opts(mock(res(409, { error: 'Already granted' })))))
    expect(conflict).toMatchObject({ kind: 'conflict', message: 'Already granted' })
  })

  it('maps a 404 subject and a 502 from the identity provider', async () => {
    expect((await errOf(listGrants('user', 'gone', opts(mock(res(404))))))).toMatchObject({
      kind: 'not-found',
      message: 'Not found: user "gone".',
    })
    expect((await errOf(listUsers({}, opts(mock(res(502))))))).toMatchObject({ kind: 'server', status: 502 })
  })

  it('reports network failures', async () => {
    const f = vi.fn(async () => {
      throw new TypeError('Failed to fetch')
    })
    const e = await errOf(listUsers({}, { baseUrl: base, fetchImpl: f as unknown as typeof fetch }))
    expect(e.kind).toBe('network')
  })
})
