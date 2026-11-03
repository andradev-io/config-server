// End-to-end: authorization code + PKCE against the real provider, mounted under /idp, no browser needed.
import assert from 'node:assert/strict'
import { createHash, randomBytes } from 'node:crypto'
import { createServer } from 'node:net'
import { after, before, test } from 'node:test'
import { createDemoIdp } from '../app.js'

const REDIRECT = 'http://localhost:5173/'
let idp
let issuer

const freePort = () =>
  new Promise((resolve) => {
    const s = createServer().listen(0, () => {
      const { port } = s.address()
      s.close(() => resolve(port))
    })
  })

before(async () => {
  const port = await freePort()
  issuer = `http://localhost:${port}/idp`
  idp = createDemoIdp({ issuer, redirectUris: [REDIRECT], trustProxy: false, loginsPerWindow: 4 })
  await new Promise((r) => idp.server.listen(port, '127.0.0.1', r))
})

after(() => new Promise((r) => idp.server.close(r)))

const b64url = (buf) => buf.toString('base64url')
const decode = (jwt) => JSON.parse(Buffer.from(jwt.split('.')[1], 'base64url').toString())

/** Minimal cookie jar: name=value only (all cookies live on one host in these tests). */
function jar() {
  const cookies = new Map()
  return {
    header: () => [...cookies].map(([k, v]) => `${k}=${v}`).join('; '),
    store(res) {
      for (const c of res.headers.getSetCookie()) {
        const [pair] = c.split(';')
        const i = pair.indexOf('=')
        const name = pair.slice(0, i)
        const value = pair.slice(i + 1)
        if (value === '' || /expires=Thu, 01 Jan 1970/i.test(c)) cookies.delete(name)
        else cookies.set(name, value)
      }
    },
  }
}

async function get(url, cookies) {
  const res = await fetch(new URL(url, `http://localhost:${new URL(issuer).port}`), {
    redirect: 'manual',
    headers: { cookie: cookies.header() },
  })
  cookies.store(res)
  return res
}

/** Runs the whole sign-in and returns the token response plus the state echoed to the redirect URI. */
async function signIn(persona) {
  const cookies = jar()
  const verifier = b64url(randomBytes(32))
  const state = b64url(randomBytes(8))
  const authUrl =
    `${issuer}/auth?` +
    new URLSearchParams({
      client_id: 'config-demo-web',
      redirect_uri: REDIRECT,
      response_type: 'code',
      scope: 'openid profile',
      code_challenge: b64url(createHash('sha256').update(verifier).digest()),
      code_challenge_method: 'S256',
      state,
    })
  let res = await get(authUrl, cookies)
  assert.equal(res.status, 303)
  const interaction = res.headers.get('location')
  assert.match(interaction, /^\/idp\/interaction\/[\w-]+$/)

  const page = await get(interaction, cookies)
  assert.equal(page.status, 200)
  const html = await page.text()
  for (const p of ['Viewer', 'Developer', 'Admin']) assert.ok(html.includes(p))

  const post = await fetch(new URL(`${interaction}/login`, issuer), {
    method: 'POST',
    redirect: 'manual',
    headers: { cookie: cookies.header(), 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ persona }),
  })
  cookies.store(post)
  if (post.status !== 303) return { post }

  let location = post.headers.get('location')
  for (let i = 0; i < 6 && !location.startsWith(REDIRECT); i++) {
    res = await get(location, cookies)
    location = res.headers.get('location')
  }
  assert.ok(location.startsWith(REDIRECT), `unexpected redirect: ${location}`)
  const back = new URL(location)
  assert.equal(back.searchParams.get('state'), state)
  const code = back.searchParams.get('code')
  assert.ok(code)

  const tokenRes = await fetch(`${issuer}/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'authorization_code',
      code,
      redirect_uri: REDIRECT,
      client_id: 'config-demo-web',
      code_verifier: verifier,
    }),
  })
  assert.equal(tokenRes.status, 200)
  return { tokens: await tokenRes.json(), cookies }
}

test('discovery advertises endpoints under the /idp prefix', async () => {
  const res = await fetch(`${issuer}/.well-known/openid-configuration`)
  assert.equal(res.status, 200)
  const doc = await res.json()
  assert.equal(doc.issuer, issuer)
  assert.equal(doc.authorization_endpoint, `${issuer}/auth`)
  assert.equal(doc.token_endpoint, `${issuer}/token`)
  assert.equal(doc.jwks_uri, `${issuer}/jwks`)
  assert.equal((await fetch(`${issuer}/jwks`)).status, 200)
})

test('health endpoint and unknown paths', async () => {
  const base = new URL(issuer).origin
  assert.equal((await fetch(`${base}/healthz`)).status, 200)
  assert.equal((await fetch(`${issuer}/healthz`)).status, 200)
  assert.equal((await fetch(`${base}/elsewhere`)).status, 404)
})

test('developer sign-in: a fresh visitor with Keycloak-shaped roles and the config-demo-web audience', async () => {
  const { tokens } = await signIn('developer')
  const access = decode(tokens.access_token)
  assert.equal(access.iss, issuer)
  assert.equal(access.aud, 'config-demo-web')
  assert.match(access.sub, /^visitor-developer-[0-9a-f]{8}$/)
  assert.deepEqual(access.roles, ['config:read', 'config:write:*:dev'])
  assert.deepEqual(access.resource_access['config-demo-web'].roles, ['config:read', 'config:write:*:dev'])
  assert.match(access.name, /^Visitor Developer [0-9a-f]{4}$/)
  assert.equal(decode(tokens.id_token).sub, access.sub)
  assert.ok(tokens.refresh_token, 'a refresh token is issued (silent renew)')
  assert.ok(tokens.expires_in <= 900)
})

test('every sign-in is a new identity; refresh keeps it', async () => {
  const a = await signIn('viewer')
  const b = await signIn('admin')
  const subA = decode(a.tokens.access_token).sub
  const subB = decode(b.tokens.access_token).sub
  assert.notEqual(subA, subB)
  assert.deepEqual(decode(b.tokens.access_token).roles, ['config:admin', 'config:write', 'config:read'])

  const refreshed = await fetch(`${issuer}/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'refresh_token', refresh_token: a.tokens.refresh_token, client_id: 'config-demo-web' }),
  })
  assert.equal(refreshed.status, 200)
  const next = await refreshed.json()
  assert.equal(decode(next.access_token).sub, subA)
  assert.deepEqual(decode(next.access_token).roles, ['config:read'])
})

test('sign out shows the styled confirmation and returns to the app', async () => {
  const { tokens, cookies } = await signIn('viewer')
  const end = await get(
    `${issuer}/session/end?` +
      new URLSearchParams({ id_token_hint: tokens.id_token, post_logout_redirect_uri: REDIRECT, state: 'bye' }),
    cookies,
  )
  assert.equal(end.status, 200)
  const html = await end.text()
  assert.ok(html.includes('Yes, sign me out') && html.includes('op.logoutForm'))
  const xsrf = /name="xsrf" value="([^"]+)"/.exec(html)?.[1]
  assert.ok(xsrf)
  const done = await fetch(`${issuer}/session/end/confirm`, {
    method: 'POST',
    redirect: 'manual',
    headers: { cookie: cookies.header(), 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ xsrf, logout: 'yes' }),
  })
  assert.equal(done.status, 303)
  assert.ok(done.headers.get('location').startsWith(REDIRECT))
})

test('sign-ins are rate limited per client address', async () => {
  // 4 are allowed per window in this test server; developer, viewer, admin and the sign-out test used them.
  const limited = await signIn('viewer')
  assert.equal(limited.post.status, 429)
})
