import { describe, expect, it } from 'vitest'
import { isAuthCallback, oidcSettings, stripAuthParams } from './oidcConfig'

const info = { authority: 'https://idp.test/realms/main', clientId: 'config-web', scopes: ['profile', 'email'] }

describe('oidcSettings', () => {
  it('uses authorization code (PKCE) with the SPA base URL as redirect targets', () => {
    const s = oidcSettings(info, 'https://config.example.com', '/')
    expect(s).toMatchObject({
      authority: info.authority,
      client_id: 'config-web',
      response_type: 'code',
      redirect_uri: 'https://config.example.com/',
      post_logout_redirect_uri: 'https://config.example.com/',
      silent_redirect_uri: 'https://config.example.com/',
      automaticSilentRenew: true,
      monitorSession: false,
    })
  })

  it('always requests openid, without duplicates or blanks', () => {
    expect(oidcSettings(info, 'http://localhost:5173').scope).toBe('openid profile email')
    expect(oidcSettings({ ...info, scopes: ['openid', ' ', 'offline_access', 'openid'] }, 'http://x').scope).toBe(
      'openid offline_access',
    )
    expect(oidcSettings({ ...info, scopes: [] }, 'http://x').scope).toBe('openid')
  })

  it('honors a sub-path base', () => {
    expect(oidcSettings(info, 'https://host', '/config/').redirect_uri).toBe('https://host/config/')
  })
})

describe('callback detection', () => {
  it.each([
    ['?code=abc&state=xyz', true],
    ['?error=access_denied&state=xyz', true],
    ['?code=abc', false],
    ['?state=xyz', false],
    ['', false],
    ['?app=a', false],
  ])('%s -> %s', (search, expected) => {
    expect(isAuthCallback(search)).toBe(expected)
  })

  it('strips only the authorization response parameters', () => {
    expect(stripAuthParams('http://localhost:5173/?code=a&state=b&session_state=c&iss=d')).toBe('http://localhost:5173/')
    expect(stripAuthParams('http://localhost:5173/?keep=1&error=x&error_description=y&state=b#h')).toBe(
      'http://localhost:5173/?keep=1#h',
    )
  })
})
