import { describe, expect, it } from 'vitest'
import type { SubjectGrant } from '../api/access'
import { grantFromRole } from '../auth/grants'
import {
  APPLY_NOTICE,
  broadWriteWarning,
  buildGrantRequest,
  coveringGrant,
  emptyGrantForm,
  grantedMessage,
  grantKey,
  grantSummary,
  groupSubtitle,
  isDuplicateGrant,
  mergePage,
  providerLabel,
  revokeConsequence,
  revokeDenial,
  revokedMessage,
  rolePreview,
  ROLE_FORMATS,
  groupItem,
  sameSubject,
  scopeLabel,
  userItem,
  sortGrants,
  sourceLabel,
  userSubtitle,
  userTitle,
} from './accessAdmin'

const g = (over: Partial<SubjectGrant> = {}): SubjectGrant => ({
  permission: 'read',
  app: '*',
  environment: '*',
  role: 'config:read',
  inherited: false,
  ...over,
})

describe('rolePreview', () => {
  it.each([
    [{ permission: 'read' as const, app: '*', environment: '*' }, 'config:read'],
    [{ permission: 'write' as const }, 'config:write'],
    [{ permission: 'read' as const, app: 'billing-service', environment: '*' }, 'config:read:billing-service'],
    [{ permission: 'write' as const, app: 'billing', environment: 'dev' }, 'config:write:billing:dev'],
    [{ permission: 'read' as const, app: '*', environment: 'prd' }, 'config:read:*:prd'],
    [{ permission: 'write' as const, app: null, environment: 'stg' }, 'config:write:*:stg'],
    [{ permission: 'admin' as const, app: 'x', environment: 'y' }, 'config:admin'],
  ])('%o -> %s', (scope, role) => {
    expect(rolePreview(scope)).toBe(role)
  })

  it('round-trips through the role parser', () => {
    for (const s of [
      { permission: 'write' as const, app: 'billing', environment: 'dev' },
      { permission: 'read' as const, app: '*', environment: 'prd' },
      { permission: 'read' as const, app: 'billing', environment: '*' },
    ])
      expect(grantFromRole(rolePreview(s))).toEqual(s)
  })
})

describe('grantSummary', () => {
  it.each([
    [{ permission: 'read' as const, app: 'billing-service', environment: '*' }, 'Can read every environment of billing-service'],
    [{ permission: 'write' as const, app: 'billing', environment: 'dev' }, 'Can read and write the dev environment of billing'],
    [{ permission: 'read' as const, app: '*', environment: '*' }, 'Can read every environment of every application'],
    [{ permission: 'write' as const, app: '*', environment: 'prd' }, 'Can read and write the prd environment of every application'],
    [{ permission: 'admin' as const }, 'Full access to every application and environment, plus administration'],
  ])('%o', (scope, text) => {
    expect(grantSummary(scope)).toBe(text)
  })
})

describe('labels', () => {
  it('shows "*" as all', () => {
    expect(scopeLabel('*')).toBe('all')
    expect(scopeLabel('')).toBe('all')
    expect(scopeLabel('dev')).toBe('dev')
  })

  it('labels the source', () => {
    expect(sourceLabel(g())).toBe('direct')
    expect(sourceLabel(g({ inherited: true, via: '/eng/devs' }))).toBe('via /eng/devs')
    expect(sourceLabel(g({ inherited: true }))).toBe('via a group')
  })

  it('labels subjects', () => {
    expect(userTitle({ id: '1', username: 'ana', displayName: 'Ana Lima', email: 'ana@x' })).toBe('Ana Lima')
    expect(userSubtitle({ id: '1', username: 'ana', displayName: 'Ana Lima', email: 'ana@x' })).toBe('ana · ana@x')
    expect(userTitle({ id: '1', username: 'ana', displayName: ' ', email: '' })).toBe('ana')
    expect(userSubtitle({ id: '1', username: 'ana', displayName: '', email: 'ana@x' })).toBe('ana@x')
    expect(groupSubtitle({ id: 'g', name: 'devs', path: '/devs' })).toBe('')
    expect(groupSubtitle({ id: 'g', name: 'devs', path: '/eng/devs' })).toBe('/eng/devs')
  })

  it('builds list items and compares subjects by type and id', () => {
    const u = userItem({ id: 'x', username: 'ana', displayName: '', email: 'a@x' })
    const gr = groupItem({ id: 'x', name: 'devs', path: '/eng/devs' })
    expect(u).toEqual({ type: 'user', id: 'x', title: 'ana', subtitle: 'a@x' })
    expect(gr).toEqual({ type: 'group', id: 'x', title: 'devs', subtitle: '/eng/devs' })
    expect(sameSubject(u, { ...u, title: 'other' })).toBe(true)
    expect(sameSubject(u, gr)).toBe(false)
    expect(sameSubject(u, null)).toBe(false)
  })

  it('names providers', () => {
    expect(providerLabel('keycloak')).toBe('Keycloak')
    expect(providerLabel('none')).toBe('none')
    expect(providerLabel('')).toBe('none')
  })

  it('builds the snackbar texts with the notice', () => {
    expect(grantedMessage('config:read:billing', 'group', 'devs')).toBe(
      `Granted config:read:billing to group devs. ${APPLY_NOTICE}.`,
    )
    expect(revokedMessage('config:write', 'user', 'ana')).toBe(`Revoked config:write from user ana. ${APPLY_NOTICE}.`)
  })

  it('documents the role formats with parseable examples', () => {
    expect(ROLE_FORMATS.map((r) => r.role)).toContain('config:write:<app>:<env>')
    expect(grantFromRole('config:write:billing:dev')).not.toBeNull()
  })
})

describe('revokeConsequence', () => {
  it('explains what is lost for a user and for group members', () => {
    expect(revokeConsequence({ permission: 'write', app: 'billing', environment: 'dev' }, 'user')).toBe(
      'The user will no longer be able to read and write the dev environment of billing, unless another grant covers it.',
    )
    expect(revokeConsequence({ permission: 'read', app: '*', environment: '*' }, 'group')).toBe(
      'Members will no longer be able to read every environment of every application through this group, unless another grant covers it.',
    )
  })
})

describe('revokeDenial', () => {
  it('allows direct read / write grants', () => {
    expect(revokeDenial(g(), 'user')).toBeNull()
    expect(revokeDenial(g({ permission: 'write' }), 'group')).toBeNull()
  })

  it('refuses inherited grants with the group in the reason', () => {
    expect(revokeDenial(g({ inherited: true, via: 'devs' }), 'user')).toMatch(/^Inherited from group devs\. .*remove the user/)
    expect(revokeDenial(g({ inherited: true, via: '/eng' }), 'group')).toBe('Inherited from group /eng. Revoke it on that group.')
    expect(revokeDenial(g({ inherited: true }), 'user')).toMatch(/^Inherited from a group\./)
  })

  it('refuses admin (managed in the identity provider)', () => {
    expect(revokeDenial(g({ permission: 'admin', role: 'config:admin' }), 'user')).toMatch(/identity provider/)
  })
})

describe('sortGrants / grantKey', () => {
  it('orders strongest first, wildcards first, direct before inherited', () => {
    const list = [
      g({ app: 'billing', role: 'config:read:billing' }),
      g({ permission: 'write', app: 'billing', environment: 'dev', role: 'config:write:billing:dev' }),
      g({ inherited: true, via: 'devs' }),
      g(),
      g({ permission: 'admin', role: 'config:admin', inherited: true, via: 'admins' }),
    ]
    expect(sortGrants(list).map((x) => `${x.role}/${sourceLabel(x)}`)).toEqual([
      'config:admin/via admins',
      'config:write:billing:dev/direct',
      'config:read/direct',
      'config:read/via devs',
      'config:read:billing/direct',
    ])
  })

  it('keys rows by role and source', () => {
    const keys = [g(), g({ inherited: true, via: 'a' }), g({ inherited: true, via: 'b' })].map(grantKey)
    expect(new Set(keys).size).toBe(3)
  })
})

describe('grant form', () => {
  it('starts as read on everything', () => {
    expect(emptyGrantForm()).toEqual({ permission: 'read', app: '*', environment: '*' })
  })

  it('leaves "all" scopes out of the request', () => {
    expect(buildGrantRequest({ permission: 'read', app: '*', environment: '*' })).toEqual({ permission: 'read' })
    expect(buildGrantRequest({ permission: 'write', app: 'billing', environment: '*' })).toEqual({ permission: 'write', app: 'billing' })
    expect(buildGrantRequest({ permission: 'write', app: '*', environment: 'dev' })).toEqual({ permission: 'write', environment: 'dev' })
    expect(buildGrantRequest({ permission: 'read', app: 'a', environment: 'b' })).toEqual({ permission: 'read', app: 'a', environment: 'b' })
  })

  it('warns about write on every application only', () => {
    expect(broadWriteWarning({ permission: 'write', app: '*', environment: '*' })).toMatch(/every application and environment/)
    expect(broadWriteWarning({ permission: 'write', app: '*', environment: 'prd' })).toMatch(/prd environment of every application/)
    expect(broadWriteWarning({ permission: 'write', app: 'billing', environment: '*' })).toBeNull()
    expect(broadWriteWarning({ permission: 'read', app: '*', environment: '*' })).toBeNull()
  })

  it('finds an existing grant that already covers the form (direct first)', () => {
    const existing = [
      g({ permission: 'write', inherited: true, via: 'devs', role: 'config:write' }),
      g({ app: 'billing', role: 'config:read:billing' }),
    ]
    expect(coveringGrant(existing, { permission: 'read', app: 'billing', environment: 'dev' })?.role).toBe('config:read:billing')
    expect(coveringGrant(existing, { permission: 'write', app: 'billing', environment: 'dev' })?.via).toBe('devs')
    expect(coveringGrant([g({ app: 'billing', role: 'config:read:billing' })], { permission: 'read', app: '*', environment: '*' })).toBeNull()
    expect(coveringGrant([], emptyGrantForm())).toBeNull()
  })

  it('detects an exact direct duplicate (inherited ones do not count)', () => {
    const existing = [g({ app: 'billing', role: 'config:read:billing' }), g({ permission: 'write', inherited: true, via: 'x' })]
    expect(isDuplicateGrant(existing, { permission: 'read', app: 'billing', environment: '*' })).toBe(true)
    expect(isDuplicateGrant(existing, { permission: 'write', app: '*', environment: '*' })).toBe(false)
    expect(isDuplicateGrant(existing, { permission: 'read', app: 'billing', environment: 'dev' })).toBe(false)
  })
})

describe('mergePage', () => {
  const items = (n: number, from = 0) => Array.from({ length: n }, (_, i) => ({ id: String(from + i) }))

  it('replaces on the first page and appends (deduped) on the next ones', () => {
    const first = mergePage([], items(3), 0, 3)
    expect(first).toEqual({ items: items(3), hasMore: true })
    const next = mergePage(first.items, items(2, 2), 3, 3)
    expect(next.items.map((x) => x.id)).toEqual(['0', '1', '2', '3'])
    expect(next.hasMore).toBe(false)
    expect(mergePage(next.items, items(1), 0, 3).items).toEqual(items(1))
  })
})
