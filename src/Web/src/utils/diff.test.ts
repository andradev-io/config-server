import { describe, expect, it } from 'vitest'
import { deepEqual, diffJson, diffMapForSide, pathKey } from './diff'

describe('diffJson', () => {
  it('returns nothing for equal documents', () => {
    expect(diffJson({ a: 1, b: { c: [1, 2] } }, { b: { c: [1, 2] }, a: 1 })).toEqual([])
  })

  it('detects changed scalars at nested paths', () => {
    const d = diffJson({ db: { port: 1 } }, { db: { port: 2 } })
    expect(d).toEqual([{ path: ['db', 'port'], display: 'db.port', type: 'changed', before: 1, after: 2 }])
  })

  it('detects added and removed keys, sorted', () => {
    const d = diffJson({ a: 1, x: 1 }, { a: 1, b: { n: 1 } })
    expect(d.map((e) => [e.display, e.type])).toEqual([
      ['b', 'added'],
      ['x', 'removed'],
    ])
  })

  it('treats arrays as whole values', () => {
    const d = diffJson({ l: [1, 2, 3] }, { l: [1, 2] })
    expect(d).toHaveLength(1)
    expect(d[0]).toMatchObject({ display: 'l', type: 'changed' })
  })

  it('treats null differently from missing', () => {
    expect(diffJson({ a: null }, {})).toMatchObject([{ type: 'removed', display: 'a' }])
    expect(diffJson({ a: null }, { a: 0 })).toMatchObject([{ type: 'changed' }])
  })

  it('handles type changes and root scalars', () => {
    expect(diffJson({ a: { b: 1 } }, { a: 'x' })).toMatchObject([{ display: 'a', type: 'changed' }])
    expect(diffJson(1, 2)).toMatchObject([{ display: '$', type: 'changed' }])
  })
})

describe('helpers', () => {
  it('deepEqual compares structurally', () => {
    expect(deepEqual({ a: [1, { b: 2 }] }, { a: [1, { b: 2 }] })).toBe(true)
    expect(deepEqual([1], [1, 2])).toBe(false)
  })

  it('pathKey escapes slashes', () => {
    expect(pathKey(['a/b', 'c'])).toBe('a~1b/c')
  })

  it('diffMapForSide filters per side', () => {
    const d = diffJson({ a: 1, r: 1 }, { a: 2, n: 1 })
    expect([...diffMapForSide(d, 'baseline')].sort()).toEqual([
      ['a', 'changed'],
      ['r', 'removed'],
    ])
    expect([...diffMapForSide(d, 'target')].sort()).toEqual([
      ['a', 'changed'],
      ['n', 'added'],
    ])
  })
})
