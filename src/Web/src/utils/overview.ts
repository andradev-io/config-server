import type { AppStatus, EnvironmentStatus } from '../api/variables'
import { EXTENSIONS, environmentForFile, isReservedFile, splitFileName } from './fileNames'
import { statusLabel } from './variables'

/**
 * Environment -> override file actually used by the server for an app's file list. Same lookup order
 * as the server (.json, .yaml, .yml, .env; first wins); `default.*` and reserved files are skipped.
 */
export function overrideFiles(fileNames: readonly string[]): Map<string, string> {
  const rank = (n: string) => {
    const i = (EXTENSIONS as readonly string[]).indexOf(splitFileName(n).ext.toLowerCase())
    return i < 0 ? EXTENSIONS.length : i
  }
  const out = new Map<string, string>()
  for (const n of [...fileNames].sort((a, b) => rank(a) - rank(b))) {
    if (isReservedFile(n) || rank(n) === EXTENSIONS.length) continue
    const env = environmentForFile(n)
    if (env && !out.has(env)) out.set(env, n)
  }
  return out
}

/** Names in the declared environment order first, then the unknown ones alphabetically. */
export function orderEnvironments(names: readonly string[], order: readonly string[]): string[] {
  const pos = (n: string) => {
    const i = order.indexOf(n)
    return i < 0 ? Number.MAX_SAFE_INTEGER : i
  }
  return [...new Set(names)].sort((a, b) => pos(a) - pos(b) || a.localeCompare(b))
}

export type HealthLevel = 'ok' | 'warning' | 'error'

export interface EnvSummary {
  name: string
  level: HealthLevel
  label: string
  missing: number
  typeMismatches: number
  secretsInPlainText: number
  issues: number
}

export function summarizeEnv(s: EnvironmentStatus): EnvSummary {
  const missing = s.missing.length
  const typeMismatches = s.typeMismatches.length
  const secretsInPlainText = s.secretsInPlainText.length
  const issues = missing + typeMismatches + secretsInPlainText
  const level: HealthLevel = s.error ? 'error' : s.ok && issues === 0 ? 'ok' : 'warning'
  return { name: s.name, level, label: statusLabel(s), missing, typeMismatches, secretsInPlainText, issues }
}

export interface AppSummary {
  environments: number
  ok: number
  warning: number
  error: number
  undeclared: number
}

export function summarizeApp(status: AppStatus): AppSummary {
  const levels = status.environments.map((e) => summarizeEnv(e).level)
  return {
    environments: levels.length,
    ok: levels.filter((l) => l === 'ok').length,
    warning: levels.filter((l) => l === 'warning').length,
    error: levels.filter((l) => l === 'error').length,
    undeclared: status.undeclared.length,
  }
}

export const HEALTH_VISUAL: Record<HealthLevel, { color: string; icon: string }> = {
  ok: { color: 'success', icon: 'mdi-check-circle-outline' },
  warning: { color: 'warning', icon: 'mdi-alert-outline' },
  error: { color: 'error', icon: 'mdi-alert-circle-outline' },
}
