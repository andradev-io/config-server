import { computed, reactive, readonly } from 'vue'
import type { UserManager } from 'oidc-client-ts'
import { FilesApiError } from '../api/files'
import { setAuthHooks } from '../api/http'
import { getAuthInfo, getMe, type AuthInfo, type AuthMode, type DemoInfo, type Me } from './api'
import { ANY, effectiveGrants, type Grant } from './grants'
import { isAuthCallback, stripAuthParams } from './oidcConfig'
import { permissionsOfGrants, ROLE_ADMIN } from './roles'

export type AuthStatus = 'loading' | 'error' | 'unauthenticated' | 'authenticated'

export interface AuthState {
  status: AuthStatus
  mode: AuthMode
  me: Me | null
  /** Bootstrap failure (server unreachable, unexpected response). */
  error: string | null
  /** Shown on the login screen (sign-in failed, session expired). */
  notice: string | null
  /** Set when the server is a public sandbox. */
  demo: DemoInfo | null
}

/** Servers without the auth endpoints (404) behave as mode "none". */
const NO_AUTH: AuthInfo = { mode: 'none', oidc: null, apiKeys: false }
const ANONYMOUS_ADMIN: Me = {
  name: '',
  roles: [ROLE_ADMIN],
  via: 'none',
  grants: [{ permission: 'admin', app: ANY, environment: ANY }],
}

const state = reactive<AuthState>({ status: 'loading', mode: 'none', me: null, error: null, notice: null, demo: null })
let manager: UserManager | null = null

export const authState = readonly(state)
/** The caller's grants (server list, or derived from the roles on older servers); empty until signed in. */
export const grants = computed<readonly Grant[]>(() => effectiveGrants(state.me))
/** Coarse flags: read / write somewhere, admin = global admin. */
export const permissions = computed(() => permissionsOfGrants(grants.value))

const messageOf = (e: unknown, fallback: string) =>
  e instanceof Error && e.message ? e.message : fallback

function unauthenticated(notice: string | null) {
  state.status = 'unauthenticated'
  state.me = null
  state.notice = notice
}

async function loadMe(): Promise<void> {
  try {
    state.me = await getMe()
    state.status = 'authenticated'
    state.error = null
    state.notice = null
  } catch (e) {
    if (e instanceof FilesApiError && e.kind === 'unauthorized') return unauthenticated(state.notice)
    if (state.mode === 'none' && e instanceof FilesApiError && e.kind === 'not-found') {
      state.me = ANONYMOUS_ADMIN
      state.status = 'authenticated'
      return
    }
    state.status = 'error'
    state.error = messageOf(e, 'Could not load the current user.')
  }
}

async function accessToken(): Promise<string | null> {
  const user = manager ? await manager.getUser() : null
  return user?.access_token ?? null
}

async function renew(): Promise<boolean> {
  if (!manager) return false
  try {
    const user = await manager.signinSilent()
    return !!user?.access_token
  } catch {
    return false
  }
}

/** Loads `api/auth/info`, completes a pending redirect callback, then loads `api/auth/me`. */
export async function initAuth(): Promise<void> {
  state.status = 'loading'
  state.error = null
  let info: AuthInfo
  try {
    info = await getAuthInfo()
  } catch (e) {
    if (e instanceof FilesApiError && e.kind === 'not-found') info = NO_AUTH
    else {
      state.status = 'error'
      state.error = messageOf(e, 'Could not reach the config server.')
      return
    }
  }
  state.mode = info.mode
  state.demo = info.demo ?? null

  if (info.mode === 'none' || !info.oidc) {
    manager = null
    setAuthHooks(null)
    return loadMe()
  }

  const { createUserManager } = await import('./oidc')
  manager = createUserManager(info.oidc)
  setAuthHooks({
    getAccessToken: accessToken,
    renew,
    onUnauthenticated: () => {
      if (state.status !== 'unauthenticated') {
        unauthenticated(state.status === 'authenticated' ? 'Your session has expired. Sign in again.' : state.notice)
      }
    },
  })

  if (isAuthCallback(window.location.search)) {
    try {
      await manager.signinCallback()
    } catch (e) {
      state.notice = `Sign-in failed: ${messageOf(e, 'unknown error')}`
    } finally {
      window.history.replaceState(window.history.state, '', stripAuthParams(window.location.href))
    }
  }

  let user = await manager.getUser().catch(() => null)
  if (user?.expired && (await renew())) user = await manager.getUser().catch(() => null)
  if (!user || user.expired) {
    unauthenticated(takeSignedOutNotice() ?? state.notice)
    // Not signed in and nothing to explain (no failure, no explicit sign-out): go straight to the realm login.
    if (!state.notice && shouldAutoLogin()) await login()
    return
  }
  return loadMe()
}

const SIGNED_OUT_KEY = 'config-server.signed-out'
const AUTO_LOGIN_KEY = 'config-server.auto-login-at'
/** Minimum gap between automatic redirects, so a failing provider can never cause a redirect loop. */
const AUTO_LOGIN_GAP_MS = 15_000

function takeSignedOutNotice(): string | null {
  try {
    if (sessionStorage.getItem(SIGNED_OUT_KEY) === null) return null
    sessionStorage.removeItem(SIGNED_OUT_KEY)
    return 'You are signed out.'
  } catch {
    return null
  }
}

function shouldAutoLogin(): boolean {
  try {
    const last = Number(sessionStorage.getItem(AUTO_LOGIN_KEY) ?? 0)
    if (Date.now() - last < AUTO_LOGIN_GAP_MS) return false
    sessionStorage.setItem(AUTO_LOGIN_KEY, String(Date.now()))
  } catch {
    /* storage unavailable: redirect anyway */
  }
  return true
}

/** Inside the hidden silent-renew iframe: hand the response to the parent window and stop. */
export async function completeSilentCallback(): Promise<void> {
  const info = await getAuthInfo()
  if (info.mode !== 'oidc' || !info.oidc) return
  const { createUserManager } = await import('./oidc')
  await createUserManager(info.oidc).signinSilentCallback()
}

export async function login(): Promise<void> {
  if (!manager) return
  try {
    await manager.signinRedirect()
  } catch (e) {
    state.notice = `Could not start sign-in: ${messageOf(e, 'unknown error')}`
  }
}

/** RP-initiated logout when the provider supports it; otherwise the local session is dropped. */
export async function logout(): Promise<void> {
  if (!manager) return
  try {
    sessionStorage.setItem(SIGNED_OUT_KEY, '1')
  } catch {
    /* the sign-in screen just won't show the "signed out" note */
  }
  try {
    await manager.signoutRedirect()
  } catch {
    try {
      sessionStorage.removeItem(SIGNED_OUT_KEY)
    } catch {
      /* ignore */
    }
    await manager.removeUser().catch(() => undefined)
    unauthenticated('You are signed out.')
  }
}

export function useAuth() {
  return { state: authState, permissions, grants, login, logout, retry: initAuth }
}

/** Test hook: reset the module state. */
export function resetAuthForTests(): void {
  manager = null
  setAuthHooks(null)
  Object.assign(state, { status: 'loading', mode: 'none', me: null, error: null, notice: null, demo: null })
}
