import type { JsonValue } from '../api/config'

export type DiffType = 'added' | 'removed' | 'changed'

export interface DiffEntry {
  /** Path segments from the root. */
  path: string[]
  /** Display path, e.g. `db.hosts`, or `$` for the root. */
  display: string
  type: DiffType
  before?: JsonValue
  after?: JsonValue
}

const isObject = (v: JsonValue | undefined): v is { [key: string]: JsonValue } =>
  typeof v === 'object' && v !== null && !Array.isArray(v)

export function deepEqual(a: JsonValue | undefined, b: JsonValue | undefined): boolean {
  if (a === b) return true
  if (Array.isArray(a) && Array.isArray(b)) {
    return a.length === b.length && a.every((x, i) => deepEqual(x, b[i]))
  }
  if (isObject(a) && isObject(b)) {
    const ka = Object.keys(a)
    const kb = Object.keys(b)
    return ka.length === kb.length && ka.every((k) => Object.hasOwn(b, k) && deepEqual(a[k], b[k]))
  }
  return false
}

/** Stable string id for a path, used as a lookup key by the tree view. */
export function pathKey(path: readonly string[]): string {
  return path.map((s) => s.replace(/~/g, '~0').replace(/\//g, '~1')).join('/')
}

export function displayPath(path: readonly string[]): string {
  return path.length ? path.join('.') : '$'
}

/**
 * Leaf-level diff of two parsed JSON documents. Objects are compared key by key;
 * arrays and scalars are compared as whole values (matching the server merge
 * semantics, where an array override replaces the base array).
 */
export function diffJson(
  baseline: JsonValue | undefined,
  target: JsonValue | undefined,
  path: string[] = [],
): DiffEntry[] {
  if (isObject(baseline) && isObject(target)) {
    const keys = [...new Set([...Object.keys(baseline), ...Object.keys(target)])].sort()
    return keys.flatMap((k): DiffEntry[] => {
      const inB = Object.hasOwn(baseline, k)
      const inT = Object.hasOwn(target, k)
      const p = [...path, k]
      if (inB && !inT) return [{ path: p, display: displayPath(p), type: 'removed', before: baseline[k] }]
      if (!inB && inT) return [{ path: p, display: displayPath(p), type: 'added', after: target[k] }]
      return diffJson(baseline[k], target[k], p)
    })
  }
  if (deepEqual(baseline, target)) return []
  return [{ path, display: displayPath(path), type: 'changed', before: baseline, after: target }]
}

/** Map of path key to diff type for one side (baseline hides additions, target hides removals). */
export function diffMapForSide(entries: DiffEntry[], side: 'baseline' | 'target'): Map<string, DiffType> {
  const m = new Map<string, DiffType>()
  for (const e of entries) {
    if (side === 'baseline' && e.type === 'added') continue
    if (side === 'target' && e.type === 'removed') continue
    m.set(pathKey(e.path), e.type)
  }
  return m
}
