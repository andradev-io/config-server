import { describe, expect, it } from 'vitest'
import { environmentColors } from '../theme/tokens'
import { ENV_COLOR_SLOTS, envColor, envColorCss, envSlot, hashName } from './envColor'

describe('envColor', () => {
  it('uses fixed slots for dev, stg and prd (and their aliases)', () => {
    expect(envSlot('dev')).toBe(0)
    expect(envSlot('development')).toBe(0)
    expect(envSlot('stg')).toBe(1)
    expect(envSlot('staging')).toBe(1)
    expect(envSlot('prd')).toBe(2)
    expect(envSlot('prod')).toBe(2)
    expect(envSlot('production')).toBe(2)
  })

  it('is case-insensitive and ignores surrounding spaces', () => {
    expect(envSlot('PRD')).toBe(2)
    expect(envSlot(' Dev ')).toBe(0)
    expect(envSlot('QA')).toBe(envSlot('qa'))
  })

  it('hashes other names deterministically into the non-fixed slots', () => {
    for (const name of ['qa', 'uat', 'test', 'perf', 'sandbox', 'eu-west', 'local', 'hml']) {
      const slot = envSlot(name)
      expect(slot).toBe(envSlot(name))
      expect(slot).toBeGreaterThanOrEqual(3)
      expect(slot).toBeLessThan(ENV_COLOR_SLOTS)
    }
  })

  it('spreads different names over several slots', () => {
    const slots = new Set(['qa', 'uat', 'test', 'perf', 'sandbox', 'demo', 'preview', 'hotfix'].map(envSlot))
    expect(slots.size).toBeGreaterThan(2)
  })

  it('returns the Vuetify theme color name and a CSS color', () => {
    expect(envColor('dev')).toBe('env-0')
    expect(envColorCss('prd')).toBe('rgb(var(--v-theme-env-2))')
    expect(envColorCss('stg', 0.2)).toBe('rgba(var(--v-theme-env-1), 0.2)')
  })

  it('has a color for every slot in both themes', () => {
    expect(environmentColors.dark).toHaveLength(ENV_COLOR_SLOTS)
    expect(environmentColors.light).toHaveLength(ENV_COLOR_SLOTS)
  })

  it('hashName is the FNV-1a 32-bit hash', () => {
    expect(hashName('')).toBe(0x811c9dc5)
    expect(hashName('a')).toBe(0xe40c292c)
  })
})
