import { describe, expect, it, vi } from 'vitest'
import { CONFIG_FORBIDDEN_MESSAGE, fetchConfig, fetchConfigText } from './config'
import { FilesApiError, putFile, UNAUTHORIZED_MESSAGE, WRITES_DISABLED_MESSAGE } from './files'

const base = 'http://api.test'
const res = (status: number, body?: unknown) => new Response(body === undefined ? null : JSON.stringify(body), { status })
const f = (r: Response) => vi.fn(async (..._a: unknown[]) => r) as unknown as typeof fetch

async function errOf(p: Promise<unknown>) {
  try {
    await p
  } catch (e) {
    return e as FilesApiError
  }
  throw new Error('expected rejection')
}

describe('401 / 403 mapping', () => {
  it('files: 401 is unauthorized', async () => {
    const e = await errOf(putFile('a', 'x.json', '{}', { baseUrl: base, fetchImpl: f(res(401)) }))
    expect(e).toMatchObject({ kind: 'unauthorized', message: UNAUTHORIZED_MESSAGE, status: 401 })
  })

  it.each([
    ['empty body', undefined],
    ['AllowWrite body', { error: 'Writing configuration files is disabled (ConfigSource:AllowWrite=false).' }],
    ['generic ProblemDetails', { title: 'Forbidden', status: 403 }],
  ])('files: 403 with %s keeps the AllowWrite message', async (_n, body) => {
    const e = await errOf(putFile('a', 'x.json', '{}', { baseUrl: base, fetchImpl: f(res(403, body)) }))
    expect(e).toMatchObject({ kind: 'forbidden', message: WRITES_DISABLED_MESSAGE })
  })

  it('files: 403 with another explanation (missing role) shows it', async () => {
    const e = await errOf(
      putFile('a', 'x.json', '{}', { baseUrl: base, fetchImpl: f(res(403, { error: 'Requires the config:write role.' })) }),
    )
    expect(e).toMatchObject({ kind: 'forbidden', message: 'Requires the config:write role.' })
  })

  it('config: 401 and 403', async () => {
    await expect(fetchConfig('a', 'b', { baseUrl: base, fetchImpl: f(res(401)) })).rejects.toMatchObject({
      kind: 'unauthorized',
      status: 401,
    })
    await expect(fetchConfigText('a', 'b', 'json', { baseUrl: base, fetchImpl: f(res(403)) })).rejects.toMatchObject({
      kind: 'forbidden',
      message: CONFIG_FORBIDDEN_MESSAGE,
    })
    await expect(
      fetchConfig('a', 'b', { baseUrl: base, fetchImpl: f(res(403, { error: 'No access to app a.' })) }),
    ).rejects.toMatchObject({ kind: 'forbidden', message: 'No access to app a.' })
  })
})
