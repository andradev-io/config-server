/**
 * Screen-level permission checks on top of the grants (`src/auth/grants.ts`). Each `...Denial` returns null
 * when the action is allowed, otherwise the reason shown in the disabled control's tooltip. The server is
 * still the authority: it answers 403 `{error}` and filters what the caller may see.
 */
import {
  canWriteApp,
  canWriteEnv,
  canWriteSomeEnv,
  hasAnyAccess,
  isGlobalWriter,
  type Grants,
} from '../auth/grants'
import { environmentForFile, isDefaultFile } from './fileNames'

export const NO_ACCESS_TITLE = 'No applications assigned'
export const NO_ACCESS_MESSAGE =
  'No applications are assigned to your account — ask an administrator to grant access in the identity provider.'

/** Signed in, but without a single grant. */
export const hasNoAccess = (grants: Grants) => !hasAnyAccess(grants)

const appWide = (what: string, app: string) =>
  `${what} applies to every environment: changing it needs write access to all environments of ${app}.`
const readOnlyEnv = (app: string, env: string) => `Read-only: you have no write access to ${app} · ${env}.`

/** Save / delete of a config file: `default.*` needs write on every environment, `{env}.*` on that one. */
export function fileWriteDenial(grants: Grants, app: string, file: string): string | null {
  if (isDefaultFile(file)) return canWriteApp(grants, app) ? null : appWide(file, app)
  const env = environmentForFile(file)
  if (!env) return canWriteApp(grants, app) ? null : appWide(file, app)
  return canWriteEnv(grants, app, env) ? null : readOnlyEnv(app, env)
}

/** "New file": needs write on at least one environment of the application. */
export const newFileDenial = (grants: Grants, app: string): string | null =>
  canWriteSomeEnv(grants, app) ? null : `Read-only: you have no write access to ${app}.`

/** Declared variables (Editor > Variables) apply to the whole application. */
export const variablesWriteDenial = (grants: Grants, app: string): string | null =>
  canWriteApp(grants, app) ? null : appWide('The variables declaration', app)

/** Environments are global: create / edit / delete need write on every application and environment. */
export const environmentsWriteDenial = (grants: Grants): string | null =>
  isGlobalWriter(grants) ? null : 'Managing environments needs write access to all applications and environments.'

/** An Overrides column: `default` needs write on every environment of the app, an environment on itself. */
export const scopeWriteDenial = (grants: Grants, app: string, scope: string): string | null =>
  scope === 'default'
    ? canWriteApp(grants, app)
      ? null
      : appWide('The default scope', app)
    : canWriteEnv(grants, app, scope)
      ? null
      : readOnlyEnv(app, scope)

export const writableScopes = (grants: Grants, app: string, scopes: readonly string[]): string[] =>
  scopes.filter((s) => scopeWriteDenial(grants, app, s) === null)

/** Overrides "Add variable": needs at least one writable scope. */
export const addOverrideDenial = (grants: Grants, app: string, scopes: readonly string[]): string | null =>
  writableScopes(grants, app, scopes).length ? null : `Read-only: you have no write access to ${app}.`

/**
 * Overview "Editor" quick action for one environment: an existing file opens (read-only when not writable);
 * without one the action creates it, which needs write on that environment.
 */
export function openEnvFileDenial(grants: Grants, app: string, env: string, hasFile: boolean): string | null {
  if (hasFile || canWriteEnv(grants, app, env)) return null
  return `No ${env} override file yet, and creating one needs write access to ${app} · ${env}.`
}
