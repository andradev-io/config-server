import { describe, expect, it } from 'vitest'
import { canAdmin, canRead, canWrite, permissionsOf } from './roles'

describe('role gating', () => {
  it.each([
    [[], { read: false, write: false, admin: false }],
    [['config:read'], { read: true, write: false, admin: false }],
    [['config:write'], { read: true, write: true, admin: false }],
    [['config:admin'], { read: true, write: true, admin: true }],
    [['config:read', 'config:admin'], { read: true, write: true, admin: true }],
    [['other', 'Config:Write'], { read: false, write: false, admin: false }],
  ])('%j -> %j', (roles, expected) => {
    expect(permissionsOf(roles)).toEqual(expected)
    expect(canRead(roles)).toBe(expected.read)
    expect(canWrite(roles)).toBe(expected.write)
    expect(canAdmin(roles)).toBe(expected.admin)
  })

  it('understands scoped roles (read / write somewhere; admin only global)', () => {
    expect(permissionsOf(['config:write:billing:dev'])).toEqual({ read: true, write: true, admin: false })
    expect(permissionsOf(['config:read:orders'])).toEqual({ read: true, write: false, admin: false })
    expect(permissionsOf(['config:admin:orders'])).toEqual({ read: false, write: false, admin: false })
  })

  it('treats null / undefined as no roles', () => {
    expect(permissionsOf(null)).toEqual({ read: false, write: false, admin: false })
    expect(canWrite(undefined)).toBe(false)
  })
})
