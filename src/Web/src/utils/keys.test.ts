import { describe, expect, it } from 'vitest'
import type { ApiKey } from '../api/keys'
import {
  buildCreateRequest,
  canChangeKey,
  emptyKeyForm,
  expiryError,
  expiryToIso,
  filterKeys,
  formatInstant,
  keyDisplay,
  normalizeAllSelection,
  scopeChips,
  STATUS_COLOR,
  usageSnippet,
  validateKeyName,
} from './keys'

const key = (over: Partial<ApiKey> = {}): ApiKey => ({
  id: 'k',
  name: 'ci',
  prefix: 'ab12',
  apps: ['*'],
  environments: ['*'],
  permission: 'read',
  createdAt: '2026-10-01T10:00:00Z',
  expiresAt: null,
  revokedAt: null,
  lastUsedAt: null,
  status: 'active',
  ...over,
})

describe('key display helpers', () => {
  it('shows only the prefix', () => {
    expect(keyDisplay('ab12')).toBe('cfs_ab12_…')
  })

  it('shows * (or empty) as "all"', () => {
    expect(scopeChips(['*'])).toEqual(['all'])
    expect(scopeChips([])).toEqual(['all'])
    expect(scopeChips(['billing', 'web'])).toEqual(['billing', 'web'])
  })

  it('maps statuses to theme colors', () => {
    expect(STATUS_COLOR).toEqual({ active: 'success', expired: 'warning', revoked: 'error' })
  })

  it('formats instants and empties', () => {
    expect(formatInstant(null, 'never')).toBe('never')
    expect(formatInstant(undefined)).toBe('-')
    expect(formatInstant('not a date')).toBe('not a date')
    expect(formatInstant('2026-10-01T10:00:00Z')).toBe(new Date('2026-10-01T10:00:00Z').toLocaleString())
  })

  it('revoked keys cannot be revoked or rotated', () => {
    expect(canChangeKey({ status: 'active' })).toBe(true)
    expect(canChangeKey({ status: 'expired' })).toBe(true)
    expect(canChangeKey({ status: 'revoked' })).toBe(false)
  })
})

describe('"All" multi-select', () => {
  it.each([
    [['*'], ['*', 'billing'], ['billing']],
    [['billing'], ['billing', '*'], ['*']],
    [['billing'], ['billing', 'web'], ['billing', 'web']],
    [['billing'], [], ['*']],
    [['*'], ['*'], ['*']],
  ])('%j + %j -> %j', (prev, next, expected) => {
    expect(normalizeAllSelection(prev, next)).toEqual(expected)
  })
})

describe('create form', () => {
  it('validates the name', () => {
    expect(validateKeyName('  ')).toBe('Required')
    expect(validateKeyName('x'.repeat(101))).toMatch(/100/)
    expect(validateKeyName(' ci ')).toBeNull()
  })

  it('expires at the end of the chosen local day', () => {
    expect(expiryToIso('')).toBeUndefined()
    expect(expiryToIso('31/12/2026')).toBeUndefined()
    expect(expiryToIso('2026-12-31')).toBe(new Date(2026, 11, 31, 23, 59, 59, 999).toISOString())
  })

  it('rejects past or invalid dates', () => {
    const now = new Date(2026, 10, 2, 12)
    expect(expiryError('', now)).toBeNull()
    expect(expiryError('2026-11-01', now)).toBe('Pick a future date')
    expect(expiryError('2026-11-02', now)).toBeNull()
    expect(expiryError('nope', now)).toBe('Invalid date')
  })

  it('builds the request with defaults', () => {
    expect(buildCreateRequest({ ...emptyKeyForm(), name: ' ci ' })).toEqual({
      name: 'ci',
      permission: 'read',
      apps: ['*'],
      environments: ['*'],
    })
    const req = buildCreateRequest({
      name: 'svc',
      permission: 'write',
      apps: ['billing'],
      environments: [],
      expires: '2027-01-15',
    })
    expect(req).toEqual({
      name: 'svc',
      permission: 'write',
      apps: ['billing'],
      environments: ['*'],
      expiresAt: new Date(2027, 0, 15, 23, 59, 59, 999).toISOString(),
    })
  })
})

describe('usage snippet', () => {
  it('uses placeholders for "all"', () => {
    expect(usageSnippet('cfs_ab_x', 'http://localhost:5000/', key())).toBe(
      'curl -H "X-Api-Key: cfs_ab_x" http://localhost:5000/config/<app>/<env>',
    )
  })
  it('uses the first scoped app and environment', () => {
    expect(usageSnippet('s', 'https://cfg', key({ apps: ['billing', 'web'], environments: ['prod'] }))).toBe(
      'curl -H "X-Api-Key: s" https://cfg/config/billing/prod',
    )
  })
})

describe('filterKeys', () => {
  const keys = [
    key({ id: '1', name: 'old', prefix: 'aa', createdAt: '2026-01-01T00:00:00Z' }),
    key({ id: '2', name: 'new', prefix: 'bb', createdAt: '2026-06-01T00:00:00Z', status: 'revoked' }),
    key({ id: '3', name: 'Billing', prefix: 'cc', createdAt: '2026-03-01T00:00:00Z' }),
  ]
  it('sorts newest first', () => {
    expect(filterKeys(keys).map((k) => k.id)).toEqual(['2', '3', '1'])
  })
  it('hides revoked', () => {
    expect(filterKeys(keys, { hideRevoked: true }).map((k) => k.id)).toEqual(['3', '1'])
  })
  it('searches name or prefix, case-insensitive', () => {
    expect(filterKeys(keys, { search: 'bill' }).map((k) => k.id)).toEqual(['3'])
    expect(filterKeys(keys, { search: 'AA' }).map((k) => k.id)).toEqual(['1'])
  })
  it('does not mutate the input', () => {
    const copy = [...keys]
    filterKeys(keys)
    expect(keys).toEqual(copy)
  })
})
