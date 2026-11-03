import type { Permissions } from '../auth/roles'

export type ViewId =
  | 'overview'
  | 'view'
  | 'compare'
  | 'editor'
  | 'environments'
  | 'variables'
  | 'overrides'
  | 'keys'
  | 'access'
export type NavGroupId = 'configuration' | 'management' | 'access'

export interface NavItem {
  id: ViewId
  title: string
  icon: string
  /** null = top-level entry (no group heading). */
  group: NavGroupId | null
  /** Permission needed to see the entry; absent = everyone who can sign in. */
  requires?: keyof Permissions
}

export interface NavGroup {
  id: NavGroupId
  title: string
}

export interface NavSection {
  group: NavGroup | null
  items: NavItem[]
}

export const DEFAULT_VIEW: ViewId = 'overview'

export const NAV_GROUPS: readonly NavGroup[] = [
  { id: 'configuration', title: 'Configuration' },
  { id: 'management', title: 'Management' },
  { id: 'access', title: 'Access' },
]

export const NAV_ITEMS: readonly NavItem[] = [
  { id: 'overview', title: 'Overview', icon: 'mdi-view-dashboard-outline', group: null },
  { id: 'view', title: 'View', icon: 'mdi-file-eye-outline', group: 'configuration' },
  { id: 'compare', title: 'Compare', icon: 'mdi-compare-horizontal', group: 'configuration' },
  { id: 'editor', title: 'Editor', icon: 'mdi-file-code-outline', group: 'configuration' },
  { id: 'environments', title: 'Environments', icon: 'mdi-layers-outline', group: 'management' },
  { id: 'variables', title: 'Variables', icon: 'mdi-variable', group: 'management' },
  { id: 'overrides', title: 'Overrides', icon: 'mdi-table-edit', group: 'management' },
  { id: 'keys', title: 'API Keys', icon: 'mdi-key-chain-variant', group: 'access', requires: 'admin' },
  { id: 'access', title: 'Access', icon: 'mdi-account-key-outline', group: 'access', requires: 'admin' },
]

export const isViewAllowed = (id: ViewId, p: Permissions): boolean => {
  const item = NAV_ITEMS.find((i) => i.id === id)
  return !!item && (!item.requires || p[item.requires])
}

export const visibleNavItems = (p: Permissions): NavItem[] => NAV_ITEMS.filter((i) => isViewAllowed(i.id, p))

/** Entries grouped for the drawer: top-level entries first, then each group in order; empty groups are dropped. */
export function navSections(p: Permissions): NavSection[] {
  const items = visibleNavItems(p)
  const sections: NavSection[] = []
  const top = items.filter((i) => i.group === null)
  if (top.length) sections.push({ group: null, items: top })
  for (const g of NAV_GROUPS) {
    const inGroup = items.filter((i) => i.group === g.id)
    if (inGroup.length) sections.push({ group: g, items: inGroup })
  }
  return sections
}

/** The view to show: the requested one when allowed, otherwise the default. */
export const allowedView = (id: ViewId, p: Permissions): ViewId => (isViewAllowed(id, p) ? id : DEFAULT_VIEW)

export const viewTitle = (id: ViewId): string => NAV_ITEMS.find((i) => i.id === id)?.title ?? ''
