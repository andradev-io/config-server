import { describe, expect, it } from 'vitest'
import type { EnvironmentStatus } from '../api/variables'
import { orderEnvironments, overrideFiles, summarizeApp, summarizeEnv } from './overview'

const st = (over: Partial<EnvironmentStatus>): EnvironmentStatus => ({
  name: 'dev',
  ok: true,
  missing: [],
  typeMismatches: [],
  secretsInPlainText: [],
  ...over,
})

describe('overview helpers', () => {
  it('maps environments to their override file with the server lookup order', () => {
    const m = overrideFiles(['default.json', 'dev.yaml', 'dev.json', 'prd.env', '_variables.json', 'notes.txt', 'stg.yml'])
    expect([...m.entries()].sort()).toEqual([
      ['dev', 'dev.json'],
      ['prd', 'prd.env'],
      ['stg', 'stg.yml'],
    ])
  })

  it('orders environments by the declared order, unknown ones last', () => {
    expect(orderEnvironments(['qa', 'prd', 'dev', 'dev', 'alpha'], ['dev', 'stg', 'prd'])).toEqual([
      'dev',
      'prd',
      'alpha',
      'qa',
    ])
  })

  it('summarizes one environment', () => {
    expect(summarizeEnv(st({}))).toMatchObject({ level: 'ok', label: 'ok', issues: 0 })
    expect(summarizeEnv(st({ ok: false, missing: ['a', 'b'] }))).toMatchObject({
      level: 'warning',
      label: '2 missing',
      missing: 2,
      issues: 2,
    })
    expect(
      summarizeEnv(st({ ok: false, typeMismatches: [{ key: 'x', expected: 'number', actual: 'string' }], secretsInPlainText: ['s'] })),
    ).toMatchObject({ level: 'warning', typeMismatches: 1, secretsInPlainText: 1, issues: 2 })
    expect(summarizeEnv(st({ ok: false, error: 'bad yaml' }))).toMatchObject({ level: 'error', label: 'error' })
  })

  it('summarizes an app', () => {
    const s = summarizeApp({
      app: 'a',
      environments: [st({}), st({ name: 'stg', ok: false, missing: ['k'] }), st({ name: 'prd', ok: false, error: 'x' })],
      undeclared: ['u1', 'u2'],
    })
    expect(s).toEqual({ environments: 3, ok: 1, warning: 1, error: 1, undeclared: 2 })
  })
})
