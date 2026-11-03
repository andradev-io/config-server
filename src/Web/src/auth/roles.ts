import { flagsOf, grantsFromRoles, type GrantFlags, type Grants } from './grants'

/**
 * Server roles: `config:<perm>[:<app>[:<env>]]` (see `grants.ts`). Hierarchical: admin (global only) includes
 * write, write includes read. These helpers answer "anywhere?"; scoped checks use the grants.
 */
export const ROLE_READ = 'config:read'
export const ROLE_WRITE = 'config:write'
export const ROLE_ADMIN = 'config:admin'

export type Permissions = GrantFlags

export const canAdmin = (roles: readonly string[] | null | undefined) => permissionsOf(roles).admin
export const canWrite = (roles: readonly string[] | null | undefined) => permissionsOf(roles).write
export const canRead = (roles: readonly string[] | null | undefined) => permissionsOf(roles).read

export const permissionsOf = (roles: readonly string[] | null | undefined): Permissions => flagsOf(grantsFromRoles(roles))

/** Coarse flags from grants: read / write somewhere, admin = global admin. */
export const permissionsOfGrants = (grants: Grants): Permissions => flagsOf(grants)
