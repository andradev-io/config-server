/**
 * Single fetch path for every API call: attaches `Authorization: Bearer <token>` when an auth provider is
 * registered, and on 401 tries one token renewal before giving up. Provider-agnostic: the auth layer
 * (`src/auth/session.ts`) registers the hooks; with no hooks (auth mode "none", tests) it is plain fetch.
 */
export interface AuthHooks {
  /** Current access token, or null when there is none. */
  getAccessToken: () => string | null | Promise<string | null>
  /** Called once on 401; resolve true when a new token was obtained (the request is then retried once). */
  renew?: () => Promise<boolean>
  /** Called when the request is still 401 after the renew attempt (e.g. send the user to the login screen). */
  onUnauthenticated?: () => void
}

let hooks: AuthHooks | null = null
let renewing: Promise<boolean> | null = null

export function setAuthHooks(h: AuthHooks | null): void {
  hooks = h
  renewing = null
}

/** Concurrent 401s share one renewal. */
function renewOnce(h: AuthHooks): Promise<boolean> {
  if (!h.renew) return Promise.resolve(false)
  if (!renewing) {
    renewing = h
      .renew()
      .catch(() => false)
      .finally(() => {
        renewing = null
      })
  }
  return renewing
}

export interface AuthorizedFetchOptions {
  /** Skip the token (anonymous endpoints such as `api/auth/info`). */
  anonymous?: boolean
}

export async function authorizedFetch(
  doFetch: typeof fetch,
  url: string,
  init: RequestInit = {},
  options: AuthorizedFetchOptions = {},
): Promise<Response> {
  const h = options.anonymous ? null : hooks
  const attempt = async () => {
    const token = h ? await h.getAccessToken() : null
    if (!token) return doFetch(url, init)
    const headers = new Headers(init.headers)
    headers.set('Authorization', `Bearer ${token}`)
    return doFetch(url, { ...init, headers })
  }
  let res = await attempt()
  if (res.status !== 401 || !h) return res
  if (await renewOnce(h)) res = await attempt()
  if (res.status === 401) h.onUnauthenticated?.()
  return res
}
