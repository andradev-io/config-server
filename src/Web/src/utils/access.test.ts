import { describe, expect, it } from 'vitest'
import type { Grant } from '../auth/grants'
import {
  addOverrideDenial,
  environmentsWriteDenial,
  fileWriteDenial,
  hasNoAccess,
  NO_ACCESS_MESSAGE,
  newFileDenial,
  openEnvFileDenial,
  scopeWriteDenial,
  variablesWriteDenial,
  writableScopes,
} from './access'

const g = (permission: Grant['permission'], app = '*', environment = '*'): Grant => ({ permission, app, environment })

const devWriter = [g('write', 'billing', 'dev'), g('read', 'billing')]
const appWriter = [g('write', 'billing')]
const reader = [g('read')]
const admin = [g('admin')]

describe('editor files', () => {
  it('default.* needs write on every environment of the app', () => {
    expect(fileWriteDenial(appWriter, 'billing', 'default.json')).toBeNull()
    expect(fileWriteDenial(admin, 'billing', 'default.yaml')).toBeNull()
    expect(fileWriteDenial(devWriter, 'billing', 'default.json')).toMatch(/all environments of billing/)
  })

  it('{env}.* needs write on that environment', () => {
    expect(fileWriteDenial(devWriter, 'billing', 'dev.json')).toBeNull()
    expect(fileWriteDenial(devWriter, 'billing', 'dev.env')).toBeNull()
    expect(fileWriteDenial(devWriter, 'billing', 'DEV.env')).not.toBeNull()
    expect(fileWriteDenial(devWriter, 'billing', 'prd.json')).toBe('Read-only: you have no write access to billing · prd.')
    expect(fileWriteDenial(reader, 'billing', 'dev.json')).not.toBeNull()
    // A new-file base name works too (Editor "New file" dialog).
    expect(fileWriteDenial(devWriter, 'billing', 'dev')).toBeNull()
    expect(fileWriteDenial(devWriter, 'billing', 'default')).not.toBeNull()
  })

  it('new file needs write on some environment of the app', () => {
    expect(newFileDenial(devWriter, 'billing')).toBeNull()
    expect(newFileDenial(devWriter, 'orders')).not.toBeNull()
    expect(newFileDenial(reader, 'billing')).not.toBeNull()
  })

  it('variables need write on every environment of the app', () => {
    expect(variablesWriteDenial(appWriter, 'billing')).toBeNull()
    expect(variablesWriteDenial(devWriter, 'billing')).toMatch(/all environments of billing/)
  })
})

describe('environments', () => {
  it('need a global writer', () => {
    expect(environmentsWriteDenial([g('write')])).toBeNull()
    expect(environmentsWriteDenial(admin)).toBeNull()
    expect(environmentsWriteDenial(appWriter)).toMatch(/all applications and environments/)
    expect(environmentsWriteDenial(reader)).not.toBeNull()
  })
})

describe('overrides scopes', () => {
  const scopes = ['default', 'dev', 'stg', 'prd']

  it('default needs app-wide write, an environment needs write on it', () => {
    expect(scopeWriteDenial(devWriter, 'billing', 'default')).toMatch(/default scope/)
    expect(scopeWriteDenial(devWriter, 'billing', 'dev')).toBeNull()
    expect(scopeWriteDenial(devWriter, 'billing', 'prd')).toMatch(/billing · prd/)
    expect(writableScopes(devWriter, 'billing', scopes)).toEqual(['dev'])
    expect(writableScopes(appWriter, 'billing', scopes)).toEqual(scopes)
    expect(writableScopes(reader, 'billing', scopes)).toEqual([])
  })

  it('add variable needs one writable scope', () => {
    expect(addOverrideDenial(devWriter, 'billing', scopes)).toBeNull()
    expect(addOverrideDenial(reader, 'billing', scopes)).toMatch(/no write access to billing/)
  })
})

describe('overview quick actions', () => {
  it('opening an existing file is fine read-only; creating one needs write', () => {
    expect(openEnvFileDenial(reader, 'billing', 'prd', true)).toBeNull()
    expect(openEnvFileDenial(reader, 'billing', 'prd', false)).toMatch(/creating one needs write access to billing · prd/)
    expect(openEnvFileDenial(devWriter, 'billing', 'dev', false)).toBeNull()
  })
})

describe('no access', () => {
  it('is signed in without any grant', () => {
    expect(hasNoAccess([])).toBe(true)
    expect(hasNoAccess(null)).toBe(true)
    expect(hasNoAccess(reader)).toBe(false)
    expect(NO_ACCESS_MESSAGE).toMatch(/ask an administrator/)
  })
})
