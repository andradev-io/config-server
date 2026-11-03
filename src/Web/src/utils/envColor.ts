import { environmentColors } from '../theme/tokens'

/** Number of environment color slots (`env-0` .. `env-N`) registered in the Vuetify themes. */
export const ENV_COLOR_SLOTS = environmentColors.dark.length

/** Fixed slots for the usual names; any other environment is hashed into the remaining slots. */
const FIXED: Record<string, number> = {
  dev: 0,
  develop: 0,
  development: 0,
  stg: 1,
  stage: 1,
  staging: 1,
  prd: 2,
  prod: 2,
  production: 2,
}
const FIXED_SLOTS = 3

/** FNV-1a 32-bit hash: small, deterministic and well spread for short names. */
export function hashName(name: string): number {
  let h = 0x811c9dc5
  for (let i = 0; i < name.length; i++) {
    h ^= name.charCodeAt(i)
    h = Math.imul(h, 0x01000193) >>> 0
  }
  return h >>> 0
}

/** Color slot of an environment: stable for a given name (case-insensitive, trimmed). */
export function envSlot(name: string): number {
  const key = name.trim().toLowerCase()
  const fixed = FIXED[key]
  if (fixed !== undefined) return fixed
  return FIXED_SLOTS + (hashName(key) % (ENV_COLOR_SLOTS - FIXED_SLOTS))
}

/** Vuetify theme color name for an environment (usable as a `color` prop), e.g. `env-0`. */
export const envColor = (name: string): string => `env-${envSlot(name)}`

/** CSS color for an environment, following the active theme. */
export const envColorCss = (name: string, alpha?: number): string =>
  alpha === undefined
    ? `rgb(var(--v-theme-${envColor(name)}))`
    : `rgba(var(--v-theme-${envColor(name)}), ${alpha})`
