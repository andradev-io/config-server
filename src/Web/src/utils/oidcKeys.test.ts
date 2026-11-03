import { describe, expect, it } from 'vitest'
import type { OidcKey } from '../api/oidcKeys'
import {
  buildOidcRequest,
  emptyOidcKeyForm,
  filterOidcKeys,
  formGrants,
  grantLabel,
  grantLimitError,
  OIDC_MAX_GRANTS,
  oidcStatus,
  oidcUsageSnippet,
  summarizeForm,
  validateOidcName,
} from './oidcKeys'

const key = (over: Partial<OidcKey> = {}): OidcKey => ({
  id: 'k',
  clientId: 'cfs-ci-abc123',
  name: 'ci',
  grants: [],
  enabled: true,
  createdAt: '2026-10-01T10:00:00Z',
  ...over,
})

describe('validateOidcName', () => {
  it('requires a trimmed, bounded name without control characters', () => {
    expect(validateOidcName('ci')).toBeNull()
    expect(validateOidcName('   ')).toBe('Required')
    expect(validateOidcName('a'.repeat(101))).toMatch(/At most 100/)
    expect(validateOidcName('a'.repeat(100))).toBeNull()
    expect(validateOidcName('a\nb')).toMatch(/control/)
  })
})

describe('formGrants', () => {
  it('turns "all" into one wildcard grant without app / environment', () => {
    expect(formGrants(emptyOidcKeyForm())).toEqual([{ permission: 'read' }])
  })

  it('builds the apps x environments pairs', () => {
    const grants = formGrants({ permission: 'write', apps: ['web', 'api'], environments: ['dev', 'prod'] })
    expect(grants).toEqual([
      { permission: 'write', app: 'web', environment: 'dev' },
      { permission: 'write', app: 'web', environment: 'prod' },
      { permission: 'write', app: 'api', environment: 'dev' },
      { permission: 'write', app: 'api', environment: 'prod' },
    ])
  })

  it('leaves out only the wildcard side', () => {
    expect(formGrants({ permission: 'read', apps: ['web'], environments: ['*'] })).toEqual([{ permission: 'read', app: 'web' }])
    expect(formGrants({ permission: 'read', apps: ['*'], environments: ['prod'] })).toEqual([{ permission: 'read', environment: 'prod' }])
  })

  it('ignores duplicates', () => {
    expect(formGrants({ permission: 'read', apps: ['web', 'web'], environments: ['*'] })).toHaveLength(1)
  })
})

describe('grantLimitError', () => {
  it('flags selections above the server limit', () => {
    const apps = Array.from({ length: 8 }, (_, i) => `a${i}`)
    const envs = Array.from({ length: 7 }, (_, i) => `e${i}`)
    expect(grantLimitError({ permission: 'read', apps, environments: envs })).toMatch(/56 grants \(at most 50\)/)
    expect(grantLimitError({ permission: 'read', apps: apps.slice(0, 5), environments: envs.slice(0, 5) })).toBeNull()
    expect(OIDC_MAX_GRANTS).toBe(50)
  })
})

describe('buildOidcRequest', () => {
  it('trims the name and carries the grants', () => {
    expect(buildOidcRequest({ name: '  ci  ', permission: 'read', apps: ['web'], environments: ['prod'] })).toEqual({
      name: 'ci',
      grants: [{ permission: 'read', app: 'web', environment: 'prod' }],
    })
  })
})

describe('labels', () => {
  it('shows the wildcard as all', () => {
    expect(grantLabel({ permission: 'read', app: '*', environment: '*' })).toBe('read · all apps / all environments')
    expect(grantLabel({ permission: 'write', app: 'web', environment: 'prod' })).toBe('write · web / prod')
  })

  it('summarizes the form', () => {
    expect(summarizeForm(emptyOidcKeyForm())).toBe('read access to all applications in all environments')
    expect(summarizeForm({ permission: 'write', apps: ['web', 'api'], environments: ['prod'] })).toBe('write access to web, api in prod')
  })

  it('reports the status', () => {
    expect(oidcStatus(key())).toBe('active')
    expect(oidcStatus(key({ enabled: false }))).toBe('disabled')
  })
})

describe('filterOidcKeys', () => {
  const list = [
    key({ id: '1', name: 'billing', clientId: 'cfs-billing-aaa111', createdAt: '2026-01-01T00:00:00Z' }),
    key({ id: '2', name: 'ci', clientId: 'cfs-ci-bbb222', createdAt: '2026-02-01T00:00:00Z' }),
    key({ id: '3', name: 'adhoc', clientId: 'cfs-adhoc-ccc333', createdAt: null }),
  ]

  it('sorts newest first, undated last', () => {
    expect(filterOidcKeys(list).map((k) => k.id)).toEqual(['2', '1', '3'])
  })

  it('matches name or client id, ignoring case', () => {
    expect(filterOidcKeys(list, ' BILL ').map((k) => k.id)).toEqual(['1'])
    expect(filterOidcKeys(list, 'bbb222').map((k) => k.id)).toEqual(['2'])
    expect(filterOidcKeys(list, 'zzz')).toEqual([])
  })
})

describe('oidcUsageSnippet', () => {
  it('shows the token request and an authorized call for the first grant', () => {
    const s = oidcUsageSnippet('sec', 'cfs-ci-1', 'https://idp.test/token', 'http://api.test/', [{ app: 'web', environment: 'prod' }])
    expect(s).toContain("curl -s 'https://idp.test/token'")
    expect(s).toContain('-d grant_type=client_credentials')
    expect(s).toContain("-d client_id='cfs-ci-1'")
    expect(s).toContain("-d client_secret='sec'")
    expect(s).toContain('Authorization: Bearer $TOKEN" http://api.test/config/web/prod')
  })

  it('uses placeholders for wildcard grants and a missing endpoint', () => {
    const s = oidcUsageSnippet('sec', 'c', null, 'http://api.test', [{ app: '*', environment: '*' }])
    expect(s).toContain('<token endpoint of your identity provider>')
    expect(s).toContain('/config/<app>/<env>')
    expect(oidcUsageSnippet('sec', 'c', 'e', 'http://api.test', [])).toContain('/config/<app>/<env>')
  })

  it('quotes single quotes in the secret for the shell', () => {
    expect(oidcUsageSnippet("a'b", 'c', 'e', 'http://x', [])).toContain(`-d client_secret='a'\\''b'`)
  })
})
