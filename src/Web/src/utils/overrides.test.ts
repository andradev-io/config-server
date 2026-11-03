import { describe, expect, it } from 'vitest'
import type { OverrideVariable } from '../api/overrides'
import {
  buildChanges,
  cellMismatch,
  cellView,
  clearPending,
  deepEqual,
  emptyPending,
  filterPending,
  filterRows,
  formatValue,
  getPending,
  hasTypeMismatch,
  inferType,
  newLocalRow,
  parseValue,
  reconcilePending,
  removePending,
  setPending,
  storedCell,
  validateNewKey,
  withLocalRows,
} from './overrides'

const row = (over: Partial<OverrideVariable> = {}): OverrideVariable => ({
  key: 'Db.Port',
  type: 'number',
  secret: false,
  declared: true,
  values: { default: { value: 5432, format: 'json', file: 'default.json' } },
  effective: { dev: { value: 5432, from: 'default' }, prod: { value: 1, from: 'prod' } },
  ...over,
})

describe('parseValue', () => {
  it('keeps strings as is, including empty', () => {
    expect(parseValue('string', ' a ')).toEqual({ ok: true, value: ' a ' })
    expect(parseValue('string', '')).toEqual({ ok: true, value: '' })
  })
  it('parses numbers', () => {
    expect(parseValue('number', ' 12.5 ')).toEqual({ ok: true, value: 12.5 })
    expect(parseValue('number', 'abc').ok).toBe(false)
    expect(parseValue('number', '').ok).toBe(false)
    expect(parseValue('number', 'Infinity').ok).toBe(false)
  })
  it('parses booleans', () => {
    expect(parseValue('boolean', 'True')).toEqual({ ok: true, value: true })
    expect(parseValue('boolean', 'false')).toEqual({ ok: true, value: false })
    expect(parseValue('boolean', 'yes').ok).toBe(false)
  })
  it('validates arrays and objects as JSON', () => {
    expect(parseValue('array', '[1,2]')).toEqual({ ok: true, value: [1, 2] })
    expect(parseValue('array', '{}').ok).toBe(false)
    expect(parseValue('object', '{"a":1}')).toEqual({ ok: true, value: { a: 1 } })
    expect(parseValue('object', '[]').ok).toBe(false)
    expect(parseValue('object', 'null').ok).toBe(false)
    expect(parseValue('object', '{bad').ok).toBe(false)
  })
})

describe('formatValue / deepEqual / inferType', () => {
  it('formats', () => {
    expect(formatValue('a')).toBe('a')
    expect(formatValue(3)).toBe('3')
    expect(formatValue([1])).toBe('[1]')
    expect(formatValue({ a: 1 }, true)).toBe('{\n  "a": 1\n}')
  })
  it('compares deeply', () => {
    expect(deepEqual({ a: [1, { b: 2 }] }, { a: [1, { b: 2 }] })).toBe(true)
    expect(deepEqual([1], { 0: 1 })).toBe(false)
    expect(deepEqual(1, '1')).toBe(false)
  })
  it('infers types', () => {
    expect(inferType('true')).toBe('boolean')
    expect(inferType('4')).toBe('number')
    expect(inferType('[1]')).toBe('array')
    expect(inferType('{"a":1}')).toBe('object')
    expect(inferType('hello')).toBe('string')
    expect(inferType('')).toBe('string')
  })
})

describe('pending set', () => {
  it('sets, replaces and dedupes against the stored value', () => {
    const r = row()
    let p = setPending(emptyPending(), r, 'dev', 1)
    p = setPending(p, r, 'dev', 2)
    expect(p.size).toBe(1)
    expect(getPending(p, r.key, 'dev')).toMatchObject({ remove: false, value: 2 })
    // setting the stored value again is not a change
    p = setPending(p, r, 'default', 5432)
    expect(getPending(p, r.key, 'default')).toBeUndefined()
    p = setPending(emptyPending(), row({ type: 'array', values: { dev: { value: [1], format: 'json', file: 'dev.json' } } }), 'dev', [1])
    expect(p.size).toBe(0)
  })

  it('does not mutate the input set', () => {
    const p0 = emptyPending()
    setPending(p0, row(), 'dev', 1)
    expect(p0.size).toBe(0)
  })

  it('removes only when an override exists', () => {
    const r = row()
    expect(removePending(emptyPending(), r, 'dev').size).toBe(0)
    const p = removePending(emptyPending(), r, 'default')
    expect(getPending(p, r.key, 'default')).toMatchObject({ remove: true })
  })

  it('remove replaces a pending set and set replaces a pending remove', () => {
    const r = row()
    let p = removePending(emptyPending(), r, 'default')
    p = setPending(p, r, 'default', 1)
    expect(getPending(p, r.key, 'default')).toMatchObject({ remove: false, value: 1 })
    p = removePending(p, r, 'default')
    expect(p.size).toBe(1)
    expect(getPending(p, r.key, 'default')?.remove).toBe(true)
  })

  it('clears one cell', () => {
    const r = row()
    const p = clearPending(setPending(emptyPending(), r, 'dev', 1), r.key, 'dev')
    expect(p.size).toBe(0)
  })

  it('masked secrets always count as a change', () => {
    const r = row({ secret: true, type: 'string', values: { default: { masked: true, file: 'default.json' } } })
    expect(setPending(emptyPending(), r, 'default', 'x').size).toBe(1)
  })

  it('reconciles after reload', () => {
    const r = row()
    let p = setPending(emptyPending(), r, 'dev', 1)
    p = setPending(p, r, 'prod', 7)
    p = removePending(p, r, 'default')
    const reloaded = row({
      values: { dev: { value: 1, format: 'json', file: 'dev.json' } },
    })
    const out = reconcilePending(p, [reloaded])
    // dev already matches; default removal already applied; prod still pending
    expect([...out.values()].map((c) => c.scope)).toEqual(['prod'])
    // pending for a vanished variable is dropped unless it is a new set
    expect(reconcilePending(removePending(p, r, 'default'), []).size).toBe(2)
  })

  it('keeps only the changes on allowed scopes (what the pending bar counts and saves)', () => {
    const r = row()
    let p = setPending(emptyPending(), r, 'dev', 1)
    p = setPending(p, r, 'prod', 7)
    p = removePending(p, r, 'default')
    const writable = new Set(['dev'])
    const allowed = filterPending(p, (s) => writable.has(s))
    expect([...allowed.values()].map((c) => c.scope)).toEqual(['dev'])
    expect(buildChanges(allowed)).toEqual([{ key: 'Db.Port', scope: 'dev', value: 1 }])
    expect(p.size).toBe(3)
    expect(filterPending(p, () => false).size).toBe(0)
  })
})

describe('buildChanges', () => {
  it('orders by key then scope and uses remove/value shapes', () => {
    const a = row({ key: 'B' })
    const b = row({ key: 'A' })
    let p = setPending(emptyPending(), a, 'prod', 2)
    p = setPending(p, b, 'dev', 1)
    p = removePending(p, b, 'default')
    expect(buildChanges(p)).toEqual([
      { key: 'A', scope: 'default', remove: true },
      { key: 'A', scope: 'dev', value: 1 },
      { key: 'B', scope: 'prod', value: 2 },
    ])
  })
  it('is empty for an empty set', () => {
    expect(buildChanges(emptyPending())).toEqual([])
  })
})

describe('cells', () => {
  const r = row()
  it('shows overrides, inherited and empty cells', () => {
    expect(storedCell(r, 'default')).toMatchObject({ kind: 'override', text: '5432', masked: false })
    expect(storedCell(r, 'dev')).toMatchObject({ kind: 'inherited', from: 'default', text: '5432' })
    expect(storedCell(r, 'qa')).toEqual({ kind: 'empty' })
    expect(storedCell(row({ values: {}, effective: {} }), 'default')).toEqual({ kind: 'empty' })
  })
  it('masks secrets and never reveals', () => {
    const s = row({
      secret: true,
      type: 'string',
      values: { default: { masked: true, file: 'default.json' } },
      effective: { dev: { masked: true, from: 'default' } },
    })
    expect(storedCell(s, 'default')).toMatchObject({ kind: 'override', masked: true, text: '••••••' })
    expect(storedCell(s, 'dev')).toMatchObject({ kind: 'inherited', masked: true, text: '••••••' })
    const p = setPending(emptyPending(), s, 'dev', 'hunter2')
    expect(cellView(s, 'dev', getPending(p, s.key, 'dev'))).toMatchObject({ text: '••••••', masked: true })
  })
  it('reflects pending set and remove', () => {
    const p = setPending(emptyPending(), r, 'dev', 9)
    expect(cellView(r, 'dev', getPending(p, r.key, 'dev'))).toMatchObject({ kind: 'override', text: '9' })
    const q = removePending(emptyPending(), row({ values: { default: r.values.default, dev: { value: 3, format: 'json', file: 'dev.json' } } }), 'dev')
    const rr = row({ values: { default: r.values.default, dev: { value: 3, format: 'json', file: 'dev.json' } } })
    expect(cellView(rr, 'dev', getPending(q, rr.key, 'dev'))).toMatchObject({ kind: 'inherited', from: 'default' })
    const d = removePending(emptyPending(), r, 'default')
    expect(cellView(r, 'default', getPending(d, r.key, 'default'))).toEqual({ kind: 'empty' })
  })
  it('detects type mismatches', () => {
    const bad = row({ values: { default: { value: 'abc', format: 'json', file: 'default.json' } } })
    expect(cellMismatch(bad, storedCell(bad, 'default'))).toBe(true)
    expect(cellMismatch(r, storedCell(r, 'default'))).toBe(false)
    expect(cellMismatch(r, storedCell(r, 'dev'))).toBe(false)
  })
})

describe('filterRows', () => {
  const rows = [
    row({ key: 'Db.Port' }),
    row({ key: 'Db.Host', values: {} }),
    row({ key: 'Api.Url', values: { dev: { value: 'x', format: 'json', file: 'dev.json' } } }),
  ]
  it('filters by search, case-insensitive', () => {
    expect(filterRows(rows, { search: ' db', onlyOverridden: false }).map((r) => r.key)).toEqual(['Db.Port', 'Db.Host'])
  })
  it('filters only overridden', () => {
    expect(filterRows(rows, { search: '', onlyOverridden: true }).map((r) => r.key)).toEqual(['Db.Port', 'Api.Url'])
  })
  it('counts pending sets as overridden', () => {
    const p = setPending(emptyPending(), rows[1], 'dev', 1)
    expect(filterRows(rows, { search: '', onlyOverridden: true }, p)).toHaveLength(3)
  })
})

describe('add variable', () => {
  it('validates keys', () => {
    expect(validateNewKey('', [])).toBe('Required')
    expect(validateNewKey('a..b', [])).not.toBe(true)
    expect(validateNewKey('a b', [])).not.toBe(true)
    expect(validateNewKey('Db.Host', ['db.host'])).toBe('Variable already exists')
    expect(validateNewKey('Db.Host', ['x'])).toBe(true)
  })
  it('merges local rows without duplicates, sorted', () => {
    const merged = withLocalRows([row({ key: 'B' })], [newLocalRow(' A ', 'string'), newLocalRow('B', 'string')])
    expect(merged.map((r) => r.key)).toEqual(['A', 'B'])
    expect(merged[1].type).toBe('number')
  })
})

describe('hasTypeMismatch leniency (values from .env are strings)', () => {
  it('accepts numeric and boolean strings like the server', () => {
    expect(hasTypeMismatch('number', '20')).toBe(false)
    expect(hasTypeMismatch('boolean', 'TRUE')).toBe(false)
  })
  it('still rejects non-numeric strings and wrong kinds', () => {
    expect(hasTypeMismatch('number', 'abc')).toBe(true)
    expect(hasTypeMismatch('number', '')).toBe(true)
    expect(hasTypeMismatch('boolean', 'yes')).toBe(true)
    expect(hasTypeMismatch('string', 5)).toBe(true)
  })
})
