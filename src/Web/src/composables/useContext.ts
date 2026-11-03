import { reactive } from 'vue'
import { listEnvironments, type EnvironmentInfo } from '../api/environments'
import { listApps } from '../api/files'
import { canReadApp, canReadEnv, canReadEnvSomewhere, readsAllApps, type Grants } from '../auth/grants'
import { authState, grants as sessionGrants } from '../auth/session'

/**
 * Global working context (selected application + environment) shared by every view and persisted in
 * localStorage. Views with unsaved edits register themselves so an application switch asks first.
 * With grants, the context only holds what the caller may read (see `reconcileContext`).
 */

export const CONTEXT_STORAGE_KEY = 'config-server-context'

export interface AppContext {
  app: string | null
  env: string | null
}

/** The part of `Storage` the store needs (injectable for tests). */
export interface KeyValueStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

export interface ContextLoaders {
  apps: () => Promise<string[]>
  environments: () => Promise<EnvironmentInfo[]>
}

const EMPTY: AppContext = { app: null, env: null }
const name = (v: unknown): string | null => (typeof v === 'string' && v.trim() ? v.trim() : null)

/** Tolerant parse of the stored value: anything unexpected yields an empty context. */
export function parseContext(raw: string | null | undefined): AppContext {
  if (!raw) return { ...EMPTY }
  try {
    const v = JSON.parse(raw) as unknown
    if (v && typeof v === 'object' && !Array.isArray(v)) {
      const o = v as Record<string, unknown>
      return { app: name(o.app), env: name(o.env) }
    }
  } catch {
    /* corrupt value */
  }
  return { ...EMPTY }
}

export const serializeContext = (c: AppContext): string => JSON.stringify({ app: c.app, env: c.env })

/** Never throws (private mode, blocked storage, quota). */
export function readContext(storage: KeyValueStorage | null | undefined): AppContext {
  if (!storage) return { ...EMPTY }
  try {
    return parseContext(storage.getItem(CONTEXT_STORAGE_KEY))
  } catch {
    return { ...EMPTY }
  }
}

/** Never throws; returns false when the value could not be stored. */
export function writeContext(storage: KeyValueStorage | null | undefined, c: AppContext): boolean {
  if (!storage) return false
  try {
    storage.setItem(CONTEXT_STORAGE_KEY, serializeContext(c))
    return true
  } catch {
    return false
  }
}

/** Applications the grants let the caller read (all of them without grants). */
export const readableApps = (apps: readonly string[], grants?: Grants): string[] =>
  grants ? apps.filter((a) => canReadApp(grants, a)) : [...apps]

/** Environments readable in `app`, or in some application when `app` is null (all of them without grants). */
export function readableEnvs<T extends string | { name: string }>(envs: readonly T[], app: string | null, grants?: Grants): T[] {
  if (!grants) return [...envs]
  const nameOf = (e: T) => (typeof e === 'string' ? e : e.name)
  return envs.filter((e) => (app === null ? canReadEnvSomewhere(grants, nameOf(e)) : canReadEnv(grants, app, nameOf(e))))
}

/**
 * Aligns a (stored) context with what the server has: an application that no longer exists is dropped,
 * an unknown or missing environment falls back to the first one. A list that failed to load (null)
 * keeps the current value.
 *
 * With `grants` the context must also be readable: an application the caller may no longer read falls back
 * to the first readable one when the grants are scoped to specific applications (a caller who reads every
 * application gets the picker instead, as above), and the environment to the first one readable in it.
 */
export function reconcileContext(
  c: AppContext,
  apps: readonly string[] | null,
  envs: readonly string[] | null,
  grants?: Grants,
): AppContext {
  if (!grants) {
    const app = apps === null || c.app === null || apps.includes(c.app) ? c.app : null
    const env = envs === null ? c.env : c.env !== null && envs.includes(c.env) ? c.env : (envs[0] ?? null)
    return { app, env }
  }
  let app = c.app
  if (app !== null) {
    const allowed = apps === null ? null : readableApps(apps, grants)
    const ok = allowed === null ? canReadApp(grants, app) : allowed.includes(app)
    if (!ok) app = readsAllApps(grants) ? null : (allowed?.[0] ?? null)
  }
  if (envs === null) return { app, env: c.env !== null && app !== null && !canReadEnv(grants, app, c.env) ? null : c.env }
  const allowedEnvs = readableEnvs(envs, app, grants)
  const env = c.env !== null && allowedEnvs.includes(c.env) ? c.env : (allowedEnvs[0] ?? null)
  return { app, env }
}

export const sortEnvironments = (list: readonly EnvironmentInfo[]): EnvironmentInfo[] =>
  [...list].sort((a, b) => a.order - b.order || a.name.localeCompare(b.name))

export function browserStorage(): KeyValueStorage | null {
  try {
    return typeof window !== 'undefined' && window.localStorage ? window.localStorage : null
  } catch {
    return null
  }
}

const defaultLoaders: ContextLoaders = { apps: () => listApps(), environments: () => listEnvironments() }

export interface ContextState {
  app: string | null
  env: string | null
  apps: string[]
  environments: EnvironmentInfo[]
  loading: boolean
  loaded: boolean
  error: string | null
  /** Application switch waiting for the "unsaved changes" confirmation (undefined = none). */
  pendingApp: string | null | undefined
}

/**
 * `grants` returns the caller's grants, or null/undefined while unknown (no filtering). The server already
 * filters the lists; the store filters again so an older server or a stored context cannot select something
 * the caller may not read.
 */
export function createContextStore(
  storage: KeyValueStorage | null = browserStorage(),
  loaders: ContextLoaders = defaultLoaders,
  grants: () => Grants = () => null,
) {
  const initial = readContext(storage)
  const state = reactive<ContextState>({
    app: initial.app,
    env: initial.env,
    apps: [],
    environments: [],
    loading: false,
    loaded: false,
    error: null,
    pendingApp: undefined,
  })
  const unsaved = new Map<symbol, { label: string; dirty: () => boolean }>()
  const persist = () => writeContext(storage, { app: state.app, env: state.env })

  /** Environments the caller may read in `app` (or in some application when null), in order. */
  const environmentsFor = (app: string | null): EnvironmentInfo[] => readableEnvs(state.environments, app, grants())

  function setApp(app: string | null) {
    const v = name(app)
    if (v === state.app) return
    state.app = v
    // The environment must stay readable in the new application.
    const g = grants()
    if (g && state.loaded) {
      const envs = environmentsFor(v).map((e) => e.name)
      if (state.env === null || !envs.includes(state.env)) state.env = envs[0] ?? null
    }
    persist()
  }
  function setEnv(env: string | null) {
    const v = name(env)
    if (v === state.env) return
    state.env = v
    persist()
  }

  /** A view with unsaved edits; returns the unregister function. */
  function registerUnsaved(label: string, dirty: () => boolean): () => void {
    const id = Symbol(label)
    unsaved.set(id, { label, dirty })
    return () => void unsaved.delete(id)
  }
  const unsavedLabels = (): string[] => [...unsaved.values()].filter((s) => s.dirty()).map((s) => s.label)

  /** Switches the application, or parks the request in `pendingApp` when some view has unsaved edits. */
  function requestApp(app: string | null): boolean {
    const v = name(app)
    if (v === state.app) {
      state.pendingApp = undefined
      return true
    }
    if (unsavedLabels().length) {
      state.pendingApp = v
      return false
    }
    setApp(v)
    return true
  }
  function confirmPendingApp() {
    if (state.pendingApp === undefined) return
    const v = state.pendingApp
    state.pendingApp = undefined
    setApp(v)
  }
  function cancelPendingApp() {
    state.pendingApp = undefined
  }

  let seq = 0
  async function refresh(): Promise<void> {
    const n = ++seq
    state.loading = true
    const [a, e] = await Promise.allSettled([loaders.apps(), loaders.environments()])
    if (n !== seq) return
    const g = grants()
    if (a.status === 'fulfilled') state.apps = readableApps(a.value, g)
    if (e.status === 'fulfilled') state.environments = readableEnvs(sortEnvironments(e.value), null, g)
    const failed = [a, e].find((r): r is PromiseRejectedResult => r.status === 'rejected')
    state.error = failed
      ? failed.reason instanceof Error && failed.reason.message
        ? failed.reason.message
        : 'Could not load applications or environments.'
      : null
    const next = reconcileContext(
      { app: state.app, env: state.env },
      a.status === 'fulfilled' ? state.apps : null,
      e.status === 'fulfilled' ? state.environments.map((x) => x.name) : null,
      g,
    )
    state.app = next.app
    state.env = next.env
    persist()
    state.loaded = true
    state.loading = false
  }

  return {
    /** Read it, change it only through the functions below. */
    state: state as Readonly<ContextState>,
    environmentsFor,
    setApp,
    setEnv,
    requestApp,
    confirmPendingApp,
    cancelPendingApp,
    registerUnsaved,
    unsavedLabels,
    refresh,
  }
}

export type ContextStore = ReturnType<typeof createContextStore>

let store: ContextStore | null = null
export function useContext(): ContextStore {
  // Grants are unknown (no filtering) until the current user is loaded.
  return (store ??= createContextStore(browserStorage(), defaultLoaders, () => (authState.me ? sessionGrants.value : null)))
}
