import { describe, expect, it, vi } from 'vitest'
import { deleteFile, FilesApiError, getFile, listApps, listFiles, putFile } from './files'

const base = 'http://api.test/'
const res = (status: number, body?: unknown, json = true) =>
  new Response(body === undefined ? null : json ? JSON.stringify(body) : String(body), { status })
const mock = (r: Response) => vi.fn(async (..._a: unknown[]) => r)
const asFetch = (f: ReturnType<typeof mock>) => f as unknown as typeof fetch

async function errOf(p: Promise<unknown>) {
  try {
    await p
  } catch (e) {
    return e as FilesApiError
  }
  throw new Error('expected rejection')
}

describe('files client', () => {
  it('lists apps', async () => {
    const f = mock(res(200, ['a', 'b']))
    expect(await listApps({ baseUrl: base, fetchImpl: asFetch(f) })).toEqual(['a', 'b'])
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/apps')
  })

  it('lists files and encodes the app name', async () => {
    const f = mock(res(200, [{ name: 'default.json', format: 'json', size: 3 }]))
    const files = await listFiles('my app', { baseUrl: base, fetchImpl: asFetch(f) })
    expect(files[0].format).toBe('json')
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/apps/my%20app/files')
  })

  it('rejects malformed list bodies', async () => {
    const e = await errOf(listFiles('a', { baseUrl: base, fetchImpl: asFetch(mock(res(200, [{ name: 1 }]))) }))
    expect(e.kind).toBe('invalid-response')
  })

  it('gets file text', async () => {
    const f = mock(res(200, 'a: 1', false))
    expect(await getFile('a', 'default.yaml', { baseUrl: base, fetchImpl: asFetch(f) })).toBe('a: 1')
    expect(f.mock.calls[0][0]).toBe('http://api.test/api/apps/a/files/default.yaml')
  })

  it('put maps 201 to created and 204 to updated', async () => {
    expect(await putFile('a', 'x.json', '{}', { baseUrl: base, fetchImpl: asFetch(mock(res(201))) })).toBe('created')
    const f = mock(res(204))
    expect(await putFile('a', 'x.json', '{}', { baseUrl: base, fetchImpl: asFetch(f) })).toBe('updated')
    const init = f.mock.calls[0][1] as RequestInit
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body as string)).toEqual({ content: '{}' })
  })

  it('put maps 400 with line and column', async () => {
    const e = await errOf(
      putFile('a', 'x.json', '{', {
        baseUrl: base,
        fetchImpl: asFetch(mock(res(400, { error: 'bad', line: 2, column: 5 }))),
      }),
    )
    expect(e).toMatchObject({ kind: 'bad-request', message: 'bad', line: 2, column: 5, status: 400 })
  })

  it.each([
    [403, 'forbidden', 'Writes are disabled on this server (ConfigSource:AllowWrite)'],
    [501, 'read-only', 'This config source is read-only'],
    [404, 'not-found', undefined],
    [500, 'server', undefined],
  ])('maps status %i', async (status, kind, message) => {
    const e = await errOf(putFile('a', 'x.json', '', { baseUrl: base, fetchImpl: asFetch(mock(res(status))) }))
    expect(e.kind).toBe(kind)
    if (message) expect(e.message).toBe(message)
  })

  it('maps 409 with error body', async () => {
    const e = await errOf(
      deleteFile('a', 'default.json', { baseUrl: base, fetchImpl: asFetch(mock(res(409, { error: 'in use' }))) }),
    )
    expect(e).toMatchObject({ kind: 'conflict', message: 'in use' })
  })

  it('delete succeeds on 204 and maps 404', async () => {
    const f = mock(res(204))
    await deleteFile('a', 'x.json', { baseUrl: base, fetchImpl: asFetch(f) })
    expect((f.mock.calls[0][1] as RequestInit).method).toBe('DELETE')
    const e = await errOf(deleteFile('a', 'x.json', { baseUrl: base, fetchImpl: asFetch(mock(res(404))) }))
    expect(e.kind).toBe('not-found')
  })

  it('maps network failures', async () => {
    const f = vi.fn(async () => {
      throw new TypeError('fail')
    })
    const e = await errOf(listApps({ baseUrl: base, fetchImpl: f as unknown as typeof fetch }))
    expect(e.kind).toBe('network')
  })
})
