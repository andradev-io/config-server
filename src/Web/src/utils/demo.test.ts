import { describe, expect, it } from 'vitest'
import { demoBannerText, dismissDemoBanner, isDemoBannerDismissed } from './demo'

const memory = () => {
  const m = new Map<string, string>()
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => void m.set(k, v) }
}

describe('demoBannerText', () => {
  it('prefers the server notice', () => {
    expect(demoBannerText({ enabled: true, idleTtlMinutes: 30, notice: 'Custom text' })).toBe('Custom text')
  })
  it('states the idle lifetime when known', () => {
    expect(demoBannerText({ enabled: true, idleTtlMinutes: 45, notice: null })).toContain('45 minutes')
  })
  it('omits the lifetime when unknown', () => {
    expect(demoBannerText({ enabled: true, idleTtlMinutes: 0, notice: null })).not.toContain('minutes')
  })
})

describe('banner dismissal', () => {
  it('is remembered in the given store', () => {
    const s = memory()
    expect(isDemoBannerDismissed(s)).toBe(false)
    dismissDemoBanner(s)
    expect(isDemoBannerDismissed(s)).toBe(true)
  })
  it('never throws when storage is missing or broken', () => {
    const broken = {
      getItem: () => {
        throw new Error('x')
      },
      setItem: () => {
        throw new Error('x')
      },
    }
    expect(isDemoBannerDismissed(null)).toBe(false)
    expect(() => dismissDemoBanner(null)).not.toThrow()
    expect(isDemoBannerDismissed(broken)).toBe(false)
    expect(() => dismissDemoBanner(broken)).not.toThrow()
  })
})
