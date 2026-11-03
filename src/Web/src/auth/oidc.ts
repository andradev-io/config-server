import { UserManager, WebStorageStateStore } from 'oidc-client-ts'
import type { OidcInfo } from './api'
import { oidcSettings } from './oidcConfig'

/** User manager with the user and the PKCE state in sessionStorage (cleared with the tab, never localStorage). */
export function createUserManager(info: OidcInfo): UserManager {
  // Distinct prefixes: stale-state cleanup scans the state prefix and must never see the user entry.
  const store = (prefix: string) => new WebStorageStateStore({ store: window.sessionStorage, prefix })
  return new UserManager({
    ...oidcSettings(info, window.location.origin, import.meta.env.BASE_URL),
    userStore: store('config-server.oidc-user.'),
    stateStore: store('config-server.oidc-state.'),
  })
}
