import { describe, expect, it } from 'vitest'
import type { UsedBy, VariableEntry } from '../api/variables'
import {
  buildMatrix,
  csvCell,
  fromRows,
  newRow,
  statusLabel,
  toRows,
  usageKind,
  validateEnvName,
  validateRows,
  variablesToCsv,
} from './variables'

const u = (app: string, extra: Partial<UsedBy> = {}): UsedBy => ({
  app,
  required: false,
  secret: false,
  declared: true,
  definedIn: [],
  environments: [],
  ...extra,
})

describe('validateEnvName', () => {
  it.each(['dev', 'Prod-1', '1a', 'a_b'])('accepts %s', (n) => expect(validateEnvName(n)).toBeNull())
  it.each(['', '-a', '_a', 'a b', 'a.b', 'a/b'])('rejects "%s"', (n) => expect(validateEnvName(n)).not.toBeNull())
})

describe('validateRows', () => {
  it('accepts valid rows', () => {
    expect(validateRows([newRow('A'), newRow('B.C')]).size).toBe(0)
  })
  it('flags empty, blank and duplicate keys', () => {
    const rows = [newRow('A'), newRow(''), newRow('  '), newRow('A')]
    const errs = validateRows(rows)
    expect(errs.get(rows[0].id)).toBeUndefined()
    expect(errs.get(rows[1].id)?.key).toBe('Required')
    expect(errs.get(rows[2].id)?.key).toBe('Required')
    expect(errs.get(rows[3].id)?.key).toBe('Duplicate key')
  })
  it('flags invalid types', () => {
    const r = newRow('A')
    ;(r as { type: string }).type = 'date'
    expect(validateRows([r]).get(r.id)?.type).toBe('Invalid type')
  })
})

describe('rows conversion', () => {
  it('maps null environments to empty and back', () => {
    const rows = toRows([
      { key: 'A', type: 'number', required: true, secret: false, description: 'd', environments: null },
      { key: 'B', type: 'string', required: false, secret: true, description: '', environments: ['dev'] },
    ])
    expect(rows[0].environments).toEqual([])
    const back = fromRows(rows)
    expect(back[0].environments).toBeNull()
    expect(back[1].environments).toEqual(['dev'])
    expect(back[0]).toMatchObject({ key: 'A', type: 'number', required: true })
  })
  it('trims keys', () => {
    expect(fromRows([newRow('  A ')])[0].key).toBe('A')
  })
})

describe('buildMatrix', () => {
  it('builds environments union ordered and rows sorted with null for unknown', () => {
    const m = buildMatrix(
      [
        u('b', { byEnvironment: { prod: 'missing', dev: 'defined' } }),
        u('a', { byEnvironment: { dev: 'inherited', qa: 'defined' } }),
      ],
      ['dev', 'qa', 'prod'],
    )
    expect(m.envs).toEqual(['dev', 'qa', 'prod'])
    expect(m.rows.map((r) => r.app)).toEqual(['a', 'b'])
    expect(m.rows[0].cells.map((c) => c.state)).toEqual(['inherited', 'defined', null])
    expect(m.rows[1].cells.map((c) => c.state)).toEqual(['defined', null, 'missing'])
  })
  it('falls back to alphabetical order and handles no data', () => {
    expect(buildMatrix([u('a', { byEnvironment: { z: 'defined', b: 'defined' } })]).envs).toEqual(['b', 'z'])
    expect(buildMatrix([u('a')]).envs).toEqual([])
  })
})

describe('usageKind and statusLabel', () => {
  it('prioritises undeclared > secret > required > optional', () => {
    expect(usageKind(u('a', { declared: false, secret: true, required: true }))).toBe('undeclared')
    expect(usageKind(u('a', { secret: true, required: true }))).toBe('secret')
    expect(usageKind(u('a', { required: true }))).toBe('required')
    expect(usageKind(u('a'))).toBe('optional')
  })
  it('labels statuses', () => {
    const base = { ok: false, missing: [] as string[], typeMismatches: [] as unknown[], secretsInPlainText: [] as string[] }
    expect(statusLabel({ ...base, ok: true })).toBe('ok')
    expect(statusLabel({ ...base, missing: ['a', 'b'] })).toBe('2 missing')
    expect(statusLabel({ ...base, typeMismatches: [1], secretsInPlainText: ['x'] })).toBe('2 issues')
    expect(statusLabel({ ...base, typeMismatches: [1] })).toBe('1 issue')
    expect(statusLabel({ ...base, error: 'x' })).toBe('error')
  })
})

describe('CSV', () => {
  it('escapes quotes, commas and newlines', () => {
    expect(csvCell('a,b')).toBe('"a,b"')
    expect(csvCell('say "hi"')).toBe('"say ""hi"""')
    expect(csvCell('plain')).toBe('plain')
  })
  it('neutralises formula injection', () => {
    expect(csvCell('=SUM(A1)')).toBe("'=SUM(A1)")
    expect(csvCell('-1,2')).toBe('"\'-1,2"')
  })
  it('exports one row per key and app, without values', () => {
    const entries: VariableEntry[] = [
      {
        key: 'Db.Host',
        type: 'string',
        appCount: 2,
        usedBy: [
          u('a', { required: true, definedIn: ['default.json', 'dev.json'], environments: ['dev', 'qa'] }),
          u('b', { secret: true }),
        ],
      },
    ]
    expect(variablesToCsv(entries)).toBe(
      'key,app,required,secret,files,environments\r\n' +
        'Db.Host,a,true,false,default.json;dev.json,dev;qa\r\n' +
        'Db.Host,b,false,true,,\r\n',
    )
  })
  it('exports only the header when empty', () => {
    expect(variablesToCsv([])).toBe('key,app,required,secret,files,environments\r\n')
  })
})
