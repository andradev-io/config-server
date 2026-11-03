import type { UserManagerSettings } from 'oidc-client-ts'
import type { OidcInfo } from './api'

/**
 * Pure OpenID Connect helpers (no runtime import of oidc-client-ts, so the library stays in its own
 * chunk, loaded only in mode "oidc").
 *
 * Generic settings (authorization code + PKCE) built only from `api/auth/info`. No provider-specific
 * code: any compliant IdP (Keycloak, Auth0, Entra ID, ...) works with its authority, a public client id
 * and the SPA base URL registered as redirect / post-logout URI.
 */
export type OidcBaseSettings = Pick<
  UserManagerSettings,
  | 'authority'
  | 'client_id'
  | 'redirect_uri'
  | 'post_logout_redirect_uri'
  | 'silent_redirect_uri'
  | 'response_type'
  | 'scope'
  | 'automaticSilentRenew'
  | 'monitorSession'
  | 'loadUserInfo'
>

export function oidcSettings(info: OidcInfo, origin: string, basePath = '/'): OidcBaseSettings {
  const appUrl = new URL(basePath || '/', origin).toString()
  const scope = [...new Set(['openid', ...info.scopes.map((s) => s.trim()).filter(Boolean)])].join(' ')
  return {
    authority: info.authority,
    client_id: info.clientId,
    redirect_uri: appUrl,
    post_logout_redirect_uri: appUrl,
    // The SPA itself handles the silent callback (detected in an iframe before the app mounts).
    silent_redirect_uri: appUrl,
    response_type: 'code',
    scope,
    automaticSilentRenew: true,
    monitorSession: false,
    loadUserInfo: false,
  }
}

const CALLBACK_PARAMS = ['code', 'state', 'session_state', 'error', 'error_description', 'error_uri', 'iss']

/** True when the query string is an authorization response (`?code=&state=` or `?error=&state=`). */
export function isAuthCallback(search: string): boolean {
  const p = new URLSearchParams(search)
  return p.has('state') && (p.has('code') || p.has('error'))
}

/** The URL without the authorization-response parameters (keeps any other query and the hash). */
export function stripAuthParams(href: string): string {
  const u = new URL(href)
  for (const k of CALLBACK_PARAMS) u.searchParams.delete(k)
  return u.toString()
}
