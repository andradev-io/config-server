import { describe, expect, it, vi } from 'vitest'
import { FilesApiError } from './files'
import { createEnvironment, deleteEnvironment, listEnvironments, updateEnvironment } from './environments'

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

describe('environments client', () => {
  it('lists environments', async () => {
    const f = mock(res(200, [{ name: 'dev', description: 'd', order: 1, declared: true, appCount: 2 }]))
    const list = await listEnvironments(opts(f))
    expect(list[0]).toMatchObject({ name: 'dev', appCount: 2, declared: true })
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/environments')
  })

  it('rejects malformed bodies', async () => {
    expect((await errOf(listEnvironments(opts(mock(res(200, [{ name: 1 }])))))).kind).toBe('invalid-response')
    expect((await errOf(listEnvironments(opts(mock(res(200, { a: 1 })))))).kind).toBe('invalid-response')
  })

  it('creates with a JSON body (201)', async () => {
    const f = mock(res(201))
    const req = { name: 'qa', order: 3, scaffold: { apps: ['a'], copyFrom: 'dev' } }
    await createEnvironment(req, opts(f))
    const init = f.mock.calls[0][1] as RequestInit
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual(req)
  })

  it.each([
    [409, 'conflict', 'exists'],
    [400, 'bad-request', 'invalid name'],
  ])('create maps %i', async (status, kind, error) => {
    const e = await errOf(createEnvironment({ name: 'x' }, opts(mock(res(status, { error })))))
    expect(e).toMatchObject({ kind, message: error, status })
  })

  it('updates with PUT and encodes the name', async () => {
    const f = mock(res(204))
    await updateEnvironment('a b', { description: 'x', order: 2 }, opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/environments/a%20b')
    expect((f.mock.calls[0][1] as RequestInit).method).toBe('PUT')
  })

  it('deletes with the deleteFiles flag', async () => {
    const f = mock(res(204))
    await deleteEnvironment('qa', true, opts(f))
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/environments/qa?deleteFiles=true')
    const g = mock(res(204))
    await deleteEnvironment('qa', false, opts(g))
    expect(g.mock.calls[0][0]).toBe('http://api.test/api/environments/qa?deleteFiles=false')
    expect((g.mock.calls[0][1] as RequestInit).method).toBe('DELETE')
  })

  it.each([
    [403, 'forbidden', 'Writes are disabled on this server (ConfigSource:AllowWrite)'],
    [501, 'read-only', 'This config source is read-only'],
    [404, 'not-found', undefined],
    [500, 'server', undefined],
  ])('maps status %i on write', async (status, kind, message) => {
    const e = await errOf(deleteEnvironment('qa', false, opts(mock(res(status)))))
    expect(e.kind).toBe(kind)
    if (message) expect(e.message).toBe(message)
  })

  it('delete maps 409 (implicit-only)', async () => {
    const e = await errOf(deleteEnvironment('qa', false, opts(mock(res(409, { error: 'implicit only' })))))
    expect(e).toMatchObject({ kind: 'conflict', message: 'implicit only' })
  })

  it('maps network failures', async () => {
    const f = vi.fn(async () => {
      throw new TypeError('fail')
    })
    const e = await errOf(listEnvironments({ baseUrl: base, fetchImpl: f as unknown as typeof fetch }))
    expect(e.kind).toBe('network')
  })
})
