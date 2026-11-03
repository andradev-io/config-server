import type { DemoInfo } from '../auth/api'

const DISMISSED_KEY = 'config-server.demo-banner-dismissed'

type Store = Pick<Storage, 'getItem' | 'setItem'>

function sessionStore(): Store | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage
  } catch {
    return null
  }
}

/** Banner text: the server's notice, else a default that states the workspace lifetime when known. */
export function demoBannerText(demo: DemoInfo): string {
  if (demo.notice) return demo.notice
  const ttl = demo.idleTtlMinutes > 0 ? ` and discarded after ${demo.idleTtlMinutes} minutes of inactivity` : ''
  return `Demo sandbox: you work in your own private workspace with sample data. Changes are not kept${ttl}. Do not enter real secrets.`
}

export function isDemoBannerDismissed(store: Store | null = sessionStore()): boolean {
  try {
    return store?.getItem(DISMISSED_KEY) === '1'
  } catch {
    return false
  }
}

export function dismissDemoBanner(store: Store | null = sessionStore()): void {
  try {
    store?.setItem(DISMISSED_KEY, '1')
  } catch {
    /* storage unavailable: the banner just comes back on reload */
  }
}
