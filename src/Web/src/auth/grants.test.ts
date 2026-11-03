import { describe, expect, it } from 'vitest'
import {
  can,
  canReadApp,
  canReadEnv,
  canReadEnvSomewhere,
  canSomewhere,
  canWriteApp,
  canWriteEnv,
  canWriteSomeEnv,
  compactGrants,
  effectiveGrants,
  flagsOf,
  grantFromRole,
  grantLabel,
  grantsFromRoles,
  hasAnyAccess,
  isGlobalAdmin,
  isGlobalReader,
  isGlobalWriter,
  readsAllApps,
  toGrant,
  type Grant,
} from './grants'

const g = (permission: Grant['permission'], app = '*', environment = '*'): Grant => ({ permission, app, environment })

describe('grants from roles', () => {
  it.each([
    ['config:read', g('read')],
    ['config:write', g('write')],
    ['config:admin', g('admin')],
    ['config:write:billing', g('write', 'billing')],
    ['config:write:billing:dev', g('write', 'billing', 'dev')],
    ['config:read:*:prd', g('read', '*', 'prd')],
  ])('%s', (role, expected) => {
    expect(grantFromRole(role)).toEqual(expected)
  })

  it.each([
    'other',
    'Config:Write',
    'config:Write',
    'config:owner',
    'config:',
    'config:read:',
    'config:read:billing:',
    'config:read:a:b:c',
    // admin is global only
    'config:admin:billing',
    'config:admin:*:dev',
  ])('ignores %s', (role) => {
    expect(grantFromRole(role)).toBeNull()
  })

  it('keeps only the config roles', () => {
    expect(grantsFromRoles(['openid', 'config:read:a', 'x'])).toEqual([g('read', 'a')])
    expect(grantsFromRoles(null)).toEqual([])
  })

  it('prefers the server grants, falls back to the roles', () => {
    expect(effectiveGrants({ roles: ['config:admin'], grants: [g('read', 'a', 'dev')] })).toEqual([g('read', 'a', 'dev')])
    expect(effectiveGrants({ roles: ['config:write:a'] })).toEqual([g('write', 'a')])
    // An explicit empty list means "nothing", not "derive".
    expect(effectiveGrants({ roles: ['config:admin'], grants: [] })).toEqual([])
    expect(effectiveGrants(null)).toEqual([])
  })

  it('reads server grants tolerantly but strictly typed', () => {
    expect(toGrant({ permission: 'Write', app: 'a', environment: 'dev' })).toEqual(g('write', 'a', 'dev'))
    expect(toGrant({ permission: 'owner', app: 'a', environment: 'dev' })).toBeNull()
    expect(toGrant({ permission: 'read', app: '', environment: '*' })).toBeNull()
    expect(toGrant({ permission: 'read', app: 'a' })).toBeNull()
    expect(toGrant(['read', 'a', 'b'])).toBeNull()
    expect(toGrant(null)).toBeNull()
  })
})

describe('can', () => {
  const scoped = [g('write', 'billing', 'dev'), g('read', 'billing'), g('read', 'orders', 'prd')]

  it('write implies read, read does not imply write', () => {
    expect(can(scoped, 'read', 'billing', 'dev')).toBe(true)
    expect(can(scoped, 'write', 'billing', 'dev')).toBe(true)
    expect(can(scoped, 'write', 'billing', 'prd')).toBe(false)
    expect(can(scoped, 'read', 'billing', 'prd')).toBe(true)
    expect(can(scoped, 'admin', 'billing', 'dev')).toBe(false)
  })

  it('an omitted or "*" scope means all of it: only wildcard grants cover it', () => {
    expect(can(scoped, 'read', 'billing')).toBe(true) // read:billing:*
    expect(can(scoped, 'write', 'billing')).toBe(false) // only dev
    expect(can(scoped, 'read', 'orders')).toBe(false) // only prd
    expect(can(scoped, 'read')).toBe(false)
    expect(can([g('read', '*', 'dev')], 'read', 'any-app', 'dev')).toBe(true)
    expect(can([g('read', '*', 'dev')], 'read', '*', 'dev')).toBe(true)
  })

  it('global admin implies everything', () => {
    const admin = [g('admin')]
    expect(can(admin, 'write', 'x', 'y')).toBe(true)
    expect(isGlobalAdmin(admin) && isGlobalWriter(admin) && isGlobalReader(admin)).toBe(true)
  })

  it('application and environment names match exactly (like the server for user roles)', () => {
    expect(can([g('write', 'billing', 'dev')], 'write', 'billing', 'dev')).toBe(true)
    expect(can([g('write', 'billing', 'Dev')], 'write', 'billing', 'dev')).toBe(false)
    expect(can([g('write', 'Billing', 'dev')], 'write', 'billing', 'dev')).toBe(false)
  })

  it('nothing for no grants', () => {
    expect(can([], 'read', 'a', 'b')).toBe(false)
    expect(can(null, 'read')).toBe(false)
    expect(canSomewhere(undefined, 'read')).toBe(false)
    expect(hasAnyAccess([])).toBe(false)
  })

  it('somewhere: given parts must be covered, omitted parts can be anything', () => {
    expect(canSomewhere(scoped, 'write')).toBe(true)
    expect(canSomewhere(scoped, 'write', { app: 'billing' })).toBe(true)
    expect(canSomewhere(scoped, 'write', { app: 'orders' })).toBe(false)
    expect(canSomewhere(scoped, 'read', { env: 'prd' })).toBe(true)
    expect(canSomewhere(scoped, 'read', { app: 'orders', env: 'dev' })).toBe(false)
  })

  it('named helpers', () => {
    expect(canWriteApp(scoped, 'billing')).toBe(false)
    expect(canWriteApp([g('write', 'billing')], 'billing')).toBe(true)
    expect(canWriteApp([g('write')], 'billing')).toBe(true)
    expect(canWriteEnv(scoped, 'billing', 'dev')).toBe(true)
    expect(canWriteEnv(scoped, 'orders', 'prd')).toBe(false)
    expect(canWriteSomeEnv(scoped, 'billing')).toBe(true)
    expect(canWriteSomeEnv(scoped, 'orders')).toBe(false)
    expect(canReadApp(scoped, 'orders')).toBe(true)
    expect(canReadApp(scoped, 'shipping')).toBe(false)
    expect(canReadEnv(scoped, 'orders', 'dev')).toBe(false)
    expect(canReadEnvSomewhere(scoped, 'prd')).toBe(true)
    expect(canReadEnvSomewhere(scoped, 'stg')).toBe(true) // read:billing:* covers every environment
    expect(canReadEnvSomewhere([g('read', 'orders', 'prd')], 'stg')).toBe(false)
    expect(readsAllApps(scoped)).toBe(false)
    expect(readsAllApps([g('read', '*', 'dev')])).toBe(true)
    expect(isGlobalWriter([g('write', 'billing')])).toBe(false)
  })

  it('coarse flags', () => {
    expect(flagsOf([])).toEqual({ read: false, write: false, admin: false })
    expect(flagsOf([g('read', 'a', 'dev')])).toEqual({ read: true, write: false, admin: false })
    expect(flagsOf([g('write', 'a', 'dev')])).toEqual({ read: true, write: true, admin: false })
    expect(flagsOf([g('admin')])).toEqual({ read: true, write: true, admin: true })
  })
})

describe('grants summary', () => {
  it('labels', () => {
    expect(grantLabel(g('write', 'billing-service', 'dev'))).toBe('write · billing-service · dev')
    expect(grantLabel(g('read'))).toBe('read · *')
    expect(grantLabel(g('write', 'billing'))).toBe('write · billing')
    expect(grantLabel(g('read', '*', 'prd'))).toBe('read · * · prd')
  })

  it('drops duplicates and implied grants, strongest first', () => {
    const list = [
      g('read', 'billing', 'dev'),
      g('write', 'billing'),
      g('read', 'orders', 'prd'),
      g('read', 'orders', 'prd'),
      g('read', '*', 'stg'),
      g('read', 'billing', 'stg'),
    ]
    expect(compactGrants(list).map(grantLabel)).toEqual(['write · billing', 'read · * · stg', 'read · orders · prd'])
    expect(compactGrants([g('read'), g('admin'), g('write', 'a', 'b')]).map(grantLabel)).toEqual(['admin · *'])
    expect(compactGrants(null)).toEqual([])
  })
})
