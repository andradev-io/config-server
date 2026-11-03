import { describe, expect, it } from 'vitest'
import { lintEnv, lintJson, lintText, lintYaml, offsetToLineColumn } from './lint'

describe('offsetToLineColumn', () => {
  it('computes 1-based positions', () => {
    expect(offsetToLineColumn('ab\ncd', 0)).toEqual({ line: 1, column: 1 })
    expect(offsetToLineColumn('ab\ncd', 4)).toEqual({ line: 2, column: 2 })
    expect(offsetToLineColumn('ab', 99)).toEqual({ line: 1, column: 3 })
  })
})

describe('lintJson', () => {
  it('accepts valid and empty', () => {
    expect(lintJson('{"a":1}')).toEqual([])
    expect(lintJson('  ')).toEqual([])
  })
  it('reports a position inside the document', () => {
    const [i] = lintJson('{\n  "a": 1,\n  oops\n}')
    expect(i.line).toBeGreaterThanOrEqual(2)
    expect(i.line).toBeLessThanOrEqual(4)
    expect(i.message).toBeTruthy()
  })
  it('handles unexpected end', () => {
    expect(lintJson('{"a":')).toHaveLength(1)
  })
})

describe('lintYaml', () => {
  it('accepts valid', () => expect(lintYaml('a: 1\nb:\n  - x\n')).toEqual([]))
  it('reports line and column', () => {
    const issues = lintYaml('a: 1\nb: [1, 2\n')
    expect(issues.length).toBeGreaterThan(0)
    expect(issues[0].line).toBeGreaterThanOrEqual(2)
  })
  it('reports duplicate keys', () => {
    expect(lintYaml('a: 1\na: 2\n').length).toBeGreaterThan(0)
  })
})

describe('lintEnv', () => {
  it('accepts comments, blanks and assignments', () => {
    expect(lintEnv('# c\n\nA=1\nB=\nexport C="x y"\n')).toEqual([])
  })
  it('flags lines without =', () => {
    expect(lintEnv('A=1\nBROKEN\n')).toEqual([{ line: 2, column: 1, message: expect.any(String) }])
  })
  it('flags missing key and handles CRLF', () => {
    expect(lintEnv('=x\r\nA=1\r\n')).toHaveLength(1)
  })
})

describe('lintText', () => {
  it('dispatches by language', () => {
    expect(lintText('env', 'x')).toHaveLength(1)
    expect(lintText('json', '{}')).toEqual([])
    expect(lintText('yaml', 'a: 1')).toEqual([])
  })
})
