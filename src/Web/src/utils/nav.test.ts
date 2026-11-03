import { describe, expect, it } from 'vitest'
import { permissionsOf, permissionsOfGrants, ROLE_ADMIN, ROLE_READ, ROLE_WRITE } from '../auth/roles'
import { allowedView, DEFAULT_VIEW, isViewAllowed, NAV_ITEMS, navSections, viewTitle, visibleNavItems } from './nav'

const reader = permissionsOf([ROLE_READ])
const writer = permissionsOf([ROLE_WRITE])
const admin = permissionsOf([ROLE_ADMIN])

describe('nav', () => {
  it('starts with Overview and defaults to it', () => {
    expect(NAV_ITEMS[0].id).toBe('overview')
    expect(DEFAULT_VIEW).toBe('overview')
  })

  it('hides API Keys without config:admin', () => {
    expect(visibleNavItems(reader).map((i) => i.id)).not.toContain('keys')
    expect(visibleNavItems(writer).map((i) => i.id)).not.toContain('keys')
    expect(visibleNavItems(admin).map((i) => i.id)).toContain('keys')
    expect(isViewAllowed('keys', writer)).toBe(false)
    expect(isViewAllowed('keys', admin)).toBe(true)
  })

  it('hides Access (identity administration) without a global config:admin', () => {
    expect(visibleNavItems(reader).map((i) => i.id)).not.toContain('access')
    expect(visibleNavItems(writer).map((i) => i.id)).not.toContain('access')
    expect(visibleNavItems(admin).map((i) => i.id)).toContain('access')
    expect(isViewAllowed('access', writer)).toBe(false)
    expect(isViewAllowed('access', admin)).toBe(true)
    expect(allowedView('access', reader)).toBe('overview')
    expect(allowedView('access', admin)).toBe('access')
    const item = NAV_ITEMS.find((i) => i.id === 'access')
    expect(item).toMatchObject({ group: 'access', requires: 'admin' })
    expect(item?.icon).toMatch(/^mdi-.+-outline$/)
  })

  it('shows every other entry to any signed-in role', () => {
    const ids = visibleNavItems(reader).map((i) => i.id)
    expect(ids).toEqual(['overview', 'view', 'compare', 'editor', 'environments', 'variables', 'overrides'])
  })

  it('groups entries and drops the Access group for non-admins', () => {
    const forReader = navSections(reader)
    expect(forReader.map((s) => s.group?.id ?? null)).toEqual([null, 'configuration', 'management'])
    expect(forReader[1].items.map((i) => i.id)).toEqual(['view', 'compare', 'editor'])
    expect(forReader[2].items.map((i) => i.id)).toEqual(['environments', 'variables', 'overrides'])

    const forAdmin = navSections(admin)
    expect(forAdmin.map((s) => s.group?.id ?? null)).toEqual([null, 'configuration', 'management', 'access'])
    expect(forAdmin[3].items.map((i) => i.id)).toEqual(['keys', 'access'])
  })

  it('falls back to the default view when the requested one is not allowed', () => {
    expect(allowedView('keys', reader)).toBe('overview')
    expect(allowedView('keys', admin)).toBe('keys')
    expect(allowedView('overrides', reader)).toBe('overrides')
  })

  it('gates API Keys on a global admin grant, not on scoped grants', () => {
    const scopedWriter = permissionsOfGrants([{ permission: 'write', app: 'billing', environment: 'dev' }])
    expect(isViewAllowed('keys', scopedWriter)).toBe(false)
    expect(isViewAllowed('access', scopedWriter)).toBe(false)
    expect(navSections(scopedWriter).map((s) => s.group?.id ?? null)).toEqual([null, 'configuration', 'management'])
    expect(allowedView('keys', scopedWriter)).toBe('overview')

    const globalWriter = permissionsOfGrants([{ permission: 'write', app: '*', environment: '*' }])
    expect(isViewAllowed('keys', globalWriter)).toBe(false)
    expect(isViewAllowed('access', globalWriter)).toBe(false)

    const globalAdmin = permissionsOfGrants([{ permission: 'admin', app: '*', environment: '*' }])
    expect(visibleNavItems(globalAdmin).map((i) => i.id)).toContain('keys')
    expect(visibleNavItems(globalAdmin).map((i) => i.id)).toContain('access')
    expect(globalAdmin).toEqual(admin)
  })

  it('keeps the screens for a user without grants (they show the "no applications assigned" state)', () => {
    const none = permissionsOfGrants([])
    expect(visibleNavItems(none).map((i) => i.id)).toEqual([
      'overview',
      'view',
      'compare',
      'editor',
      'environments',
      'variables',
      'overrides',
    ])
  })

  it('has titles', () => {
    expect(viewTitle('keys')).toBe('API Keys')
    expect(viewTitle('access')).toBe('Access')
    expect(viewTitle('overview')).toBe('Overview')
  })
})
