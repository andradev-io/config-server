import { describe, expect, it } from 'vitest'
import type { EnvironmentInfo } from '../api/environments'
import type { Grant } from '../auth/grants'
import {
  CONTEXT_STORAGE_KEY,
  createContextStore,
  parseContext,
  readableApps,
  readableEnvs,
  readContext,
  reconcileContext,
  serializeContext,
  writeContext,
  type KeyValueStorage,
} from './useContext'

function memory(initial?: string): KeyValueStorage & { data: Map<string, string> } {
  const data = new Map<string, string>()
  if (initial !== undefined) data.set(CONTEXT_STORAGE_KEY, initial)
  return { data, getItem: (k) => data.get(k) ?? null, setItem: (k, v) => void data.set(k, v) }
}
const broken: KeyValueStorage = {
  getItem: () => {
    throw new Error('SecurityError')
  },
  setItem: () => {
    throw new Error('QuotaExceededError')
  },
}
const env = (name: string, order: number): EnvironmentInfo => ({ name, order, description: '', declared: true, appCount: 1 })
const loaders = (apps: string[] | Error, envs: EnvironmentInfo[] | Error) => ({
  apps: async () => {
    if (apps instanceof Error) throw apps
    return apps
  },
  environments: async () => {
    if (envs instanceof Error) throw envs
    return envs
  },
})

describe('context persistence', () => {
  it('parses stored values tolerantly', () => {
    expect(parseContext(null)).toEqual({ app: null, env: null })
    expect(parseContext('')).toEqual({ app: null, env: null })
    expect(parseContext('not json')).toEqual({ app: null, env: null })
    expect(parseContext('[1,2]')).toEqual({ app: null, env: null })
    expect(parseContext('{"app":1,"env":"  "}')).toEqual({ app: null, env: null })
    expect(parseContext('{"app":" demo ","env":"dev"}')).toEqual({ app: 'demo', env: 'dev' })
  })

  it('round-trips through serialize/parse', () => {
    expect(parseContext(serializeContext({ app: 'a', env: null }))).toEqual({ app: 'a', env: null })
  })

  it('never throws when storage is missing or blocked', () => {
    expect(readContext(null)).toEqual({ app: null, env: null })
    expect(readContext(broken)).toEqual({ app: null, env: null })
    expect(writeContext(broken, { app: 'a', env: 'b' })).toBe(false)
    expect(writeContext(null, { app: 'a', env: 'b' })).toBe(false)
    const m = memory()
    expect(writeContext(m, { app: 'a', env: 'b' })).toBe(true)
    expect(readContext(m)).toEqual({ app: 'a', env: 'b' })
  })

  it('reconciles with the server lists', () => {
    expect(reconcileContext({ app: 'gone', env: 'dev' }, ['a', 'b'], ['dev', 'prd'])).toEqual({ app: null, env: 'dev' })
    expect(reconcileContext({ app: 'a', env: 'old' }, ['a'], ['dev', 'prd'])).toEqual({ app: 'a', env: 'dev' })
    expect(reconcileContext({ app: null, env: null }, ['a'], ['stg'])).toEqual({ app: null, env: 'stg' })
    expect(reconcileContext({ app: 'a', env: 'x' }, ['a'], [])).toEqual({ app: 'a', env: null })
    // A list that failed to load keeps the value.
    expect(reconcileContext({ app: 'a', env: 'x' }, null, null)).toEqual({ app: 'a', env: 'x' })
  })

  it('reconciles with the grants: reset to the first readable app / environment', () => {
    const scoped: Grant[] = [
      { permission: 'write', app: 'billing', environment: 'dev' },
      { permission: 'read', app: 'orders', environment: 'prd' },
    ]
    const apps = ['billing', 'orders', 'shipping']
    const envs = ['dev', 'stg', 'prd']
    // App no longer permitted -> first readable app; env not readable in it -> first readable env.
    expect(reconcileContext({ app: 'shipping', env: 'prd' }, apps, envs, scoped)).toEqual({ app: 'billing', env: 'dev' })
    expect(reconcileContext({ app: 'orders', env: 'dev' }, apps, envs, scoped)).toEqual({ app: 'orders', env: 'prd' })
    expect(reconcileContext({ app: 'orders', env: 'prd' }, apps, envs, scoped)).toEqual({ app: 'orders', env: 'prd' })
    // No app selected (picker): an env readable somewhere is kept.
    expect(reconcileContext({ app: null, env: 'prd' }, apps, envs, scoped)).toEqual({ app: null, env: 'prd' })
    expect(reconcileContext({ app: null, env: 'stg' }, apps, envs, scoped)).toEqual({ app: null, env: 'dev' })
    // A caller who reads every application gets the picker for a vanished app, as without grants.
    const global: Grant[] = [{ permission: 'read', app: '*', environment: '*' }]
    expect(reconcileContext({ app: 'gone', env: 'dev' }, ['a', 'b'], ['dev', 'prd'], global)).toEqual({ app: null, env: 'dev' })
    // Nothing readable.
    expect(reconcileContext({ app: 'a', env: 'dev' }, ['a'], ['dev'], [])).toEqual({ app: null, env: null })
    // Lists that failed to load: keep what is still readable.
    expect(reconcileContext({ app: 'orders', env: 'prd' }, null, null, scoped)).toEqual({ app: 'orders', env: 'prd' })
    expect(reconcileContext({ app: 'orders', env: 'dev' }, null, null, scoped)).toEqual({ app: 'orders', env: null })
    expect(reconcileContext({ app: 'shipping', env: null }, null, ['dev'], scoped)).toEqual({ app: null, env: 'dev' })
  })

  it('filters lists by the grants', () => {
    const scoped: Grant[] = [{ permission: 'read', app: 'a', environment: 'dev' }]
    expect(readableApps(['a', 'b'], scoped)).toEqual(['a'])
    expect(readableApps(['a', 'b'])).toEqual(['a', 'b'])
    expect(readableEnvs(['dev', 'prd'], 'a', scoped)).toEqual(['dev'])
    expect(readableEnvs(['dev', 'prd'], 'b', scoped)).toEqual([])
    expect(readableEnvs([env('dev', 1), env('prd', 2)], null, scoped).map((e) => e.name)).toEqual(['dev'])
  })
})

describe('context store', () => {
  it('starts from storage and persists changes', () => {
    const m = memory('{"app":"demo","env":"dev"}')
    const s = createContextStore(m, loaders([], []))
    expect(s.state.app).toBe('demo')
    expect(s.state.env).toBe('dev')
    s.setEnv('prd')
    s.setApp('billing')
    expect(parseContext(m.data.get(CONTEXT_STORAGE_KEY))).toEqual({ app: 'billing', env: 'prd' })
    s.setApp(null)
    expect(s.state.app).toBeNull()
  })

  it('works without storage', () => {
    const s = createContextStore(broken, loaders([], []))
    s.setApp('demo')
    expect(s.state.app).toBe('demo')
  })

  it('asks before switching the app while a view has unsaved changes', () => {
    const s = createContextStore(memory(), loaders([], []))
    let dirty = false
    const off = s.registerUnsaved('Overrides', () => dirty)
    expect(s.requestApp('a')).toBe(true)
    expect(s.state.app).toBe('a')

    dirty = true
    expect(s.unsavedLabels()).toEqual(['Overrides'])
    expect(s.requestApp('b')).toBe(false)
    expect(s.state.app).toBe('a')
    expect(s.state.pendingApp).toBe('b')
    s.cancelPendingApp()
    expect(s.state.pendingApp).toBeUndefined()
    expect(s.state.app).toBe('a')

    s.requestApp(null)
    expect(s.state.pendingApp).toBeNull()
    s.confirmPendingApp()
    expect(s.state.app).toBeNull()
    expect(s.state.pendingApp).toBeUndefined()

    off()
    expect(s.unsavedLabels()).toEqual([])
    expect(s.requestApp('c')).toBe(true)
  })

  it('loads apps and sorted environments, then reconciles the context', async () => {
    const m = memory('{"app":"gone","env":"nope"}')
    const s = createContextStore(m, loaders(['a', 'b'], [env('prd', 3), env('dev', 1), env('stg', 2)]))
    await s.refresh()
    expect(s.state.apps).toEqual(['a', 'b'])
    expect(s.state.environments.map((e) => e.name)).toEqual(['dev', 'stg', 'prd'])
    expect(s.state.app).toBeNull()
    expect(s.state.env).toBe('dev')
    expect(s.state.loaded).toBe(true)
    expect(s.state.error).toBeNull()
    expect(parseContext(m.data.get(CONTEXT_STORAGE_KEY))).toEqual({ app: null, env: 'dev' })
  })

  it('resets a stored app / env the caller may no longer read to the first allowed ones', async () => {
    const m = memory('{"app":"orders","env":"prd"}')
    const grants: Grant[] = [
      { permission: 'write', app: 'billing', environment: 'dev' },
      { permission: 'read', app: 'shipping', environment: '*' },
    ]
    // An older server that does not filter: the store filters too.
    const s = createContextStore(
      m,
      loaders(['billing', 'orders', 'shipping'], [env('prd', 3), env('dev', 1), env('stg', 2)]),
      () => grants,
    )
    await s.refresh()
    expect(s.state.apps).toEqual(['billing', 'shipping'])
    expect(s.state.environments.map((e) => e.name)).toEqual(['dev', 'stg', 'prd'])
    expect(s.state.app).toBe('billing')
    expect(s.state.env).toBe('dev')
    expect(parseContext(m.data.get(CONTEXT_STORAGE_KEY))).toEqual({ app: 'billing', env: 'dev' })
    expect(s.environmentsFor('billing').map((e) => e.name)).toEqual(['dev'])
    expect(s.environmentsFor(null).map((e) => e.name)).toEqual(['dev', 'stg', 'prd'])
  })

  it('keeps the environment readable when the app changes', async () => {
    const grants: Grant[] = [
      { permission: 'read', app: 'billing', environment: 'dev' },
      { permission: 'read', app: 'orders', environment: 'prd' },
    ]
    const s = createContextStore(memory('{"app":"orders","env":"prd"}'), loaders(['billing', 'orders'], [env('dev', 1), env('prd', 2)]), () => grants)
    await s.refresh()
    expect(s.state.app).toBe('orders')
    expect(s.state.env).toBe('prd')
    s.requestApp('billing')
    expect(s.state.env).toBe('dev')
    // Back to the picker: any environment readable somewhere stays.
    s.requestApp(null)
    expect(s.state.env).toBe('dev')
  })

  it('without grants nothing can be selected', async () => {
    const s = createContextStore(memory('{"app":"a","env":"dev"}'), loaders(['a'], [env('dev', 1)]), () => [])
    await s.refresh()
    expect(s.state.apps).toEqual([])
    expect(s.state.environments).toEqual([])
    expect(s.state.app).toBeNull()
    expect(s.state.env).toBeNull()
  })

  it('unknown grants (null) do not filter', async () => {
    const s = createContextStore(memory(), loaders(['a'], [env('dev', 1)]), () => null)
    await s.refresh()
    expect(s.state.apps).toEqual(['a'])
    expect(s.environmentsFor('a').map((e) => e.name)).toEqual(['dev'])
  })

  it('keeps the context and reports an error when a list fails to load', async () => {
    const s = createContextStore(memory('{"app":"a","env":"x"}'), loaders(new Error('offline'), [env('dev', 1)]))
    await s.refresh()
    expect(s.state.error).toBe('offline')
    expect(s.state.app).toBe('a')
    expect(s.state.env).toBe('dev')
  })
})
