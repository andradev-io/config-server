import { describe, expect, it } from 'vitest'
import {
  buildTemplate,
  emptyTemplate,
  environmentForFile,
  formatForFileName,
  isDefaultFile,
  isReservedFile,
  splitFileName,
  validateBaseName,
  validateFileName,
} from './fileNames'

describe('file names', () => {
  it('splits', () => {
    expect(splitFileName('prod.json')).toEqual({ base: 'prod', ext: '.json' })
    expect(splitFileName('noext')).toEqual({ base: 'noext', ext: '' })
  })
  it('validates names', () => {
    for (const ok of ['default.json', 'prod-1.yaml', 'qa_2.yml', 'dev.env']) expect(validateFileName(ok)).toBeNull()
    for (const bad of ['a.txt', 'a b.json', '../x.json', 'a/b.json', '.json', 'x', 'a.b.json'])
      expect(validateFileName(bad)).not.toBeNull()
    expect(validateBaseName('')).toBe('Required')
    expect(validateBaseName('a.b')).not.toBeNull()
  })
  it('detects format, default and environment', () => {
    expect(formatForFileName('x.YML')).toBe('yaml')
    expect(formatForFileName('x.env')).toBe('env')
    expect(formatForFileName('x.txt')).toBeNull()
    expect(isDefaultFile('default.yaml')).toBe(true)
    expect(environmentForFile('default.json')).toBeNull()
    expect(environmentForFile('prod.json')).toBe('prod')
  })
  it('builds templates', () => {
    expect(buildTemplate('empty', 'json')).toBe(emptyTemplate('json'))
    expect(JSON.parse(emptyTemplate('json'))).toEqual({})
    expect(buildTemplate('copy-default', 'json', '{"a":1}')).toBe('{"a":1}')
    expect(buildTemplate('copy-default', 'env')).toBe(emptyTemplate('env'))
  })
})

describe('reserved files', () => {
  it('detects reserved files and rejects their names', () => {
    expect(isReservedFile('_environments.json')).toBe(true)
    expect(isReservedFile('_Variables.json')).toBe(true)
    expect(isReservedFile('dev.json')).toBe(false)
    expect(validateFileName('_variables.json')).not.toBeNull()
  })
})
