import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  PERSONAS,
  createRateLimiter,
  issuerPrefix,
  loginPage,
  newVisitorId,
  normalizeIssuer,
  parseList,
  parseVisitorId,
  rolesOf,
  tokenClaimsFor,
  visitorName,
} from '../lib.js'

test('visitor ids are fresh, parseable and carry the persona', () => {
  const a = newVisitorId('admin')
  const b = newVisitorId('admin')
  assert.match(a, /^visitor-admin-[0-9a-f]{8}$/)
  assert.notEqual(a, b)
  assert.deepEqual(parseVisitorId(a)?.persona, 'admin')
  assert.throws(() => newVisitorId('root'))
})

test('parseVisitorId rejects anything else', () => {
  for (const id of ['alice', 'visitor-root-aaaaaaaa', 'visitor-admin-xyz', 'visitor-admin-aaaaaaaaa', '', null, undefined]) {
    assert.equal(parseVisitorId(id), null, String(id))
  }
  assert.equal(visitorName('alice'), null)
  assert.deepEqual(rolesOf('alice'), [])
  assert.deepEqual(tokenClaimsFor('alice'), {})
})

test('persona roles', () => {
  assert.deepEqual(PERSONAS.viewer.roles, ['config:read'])
  assert.deepEqual(PERSONAS.developer.roles, ['config:read', 'config:write:*:dev'])
  assert.deepEqual(PERSONAS.admin.roles, ['config:admin', 'config:write', 'config:read'])
})

test('token claims have the Keycloak shape plus a flat roles claim', () => {
  const id = 'visitor-developer-0a1b2c3d'
  const claims = tokenClaimsFor(id)
  assert.equal(claims.name, 'Visitor Developer 0a1b')
  assert.deepEqual(claims.roles, PERSONAS.developer.roles)
  assert.deepEqual(claims.resource_access['config-demo-web'].roles, PERSONAS.developer.roles)
})

test('rolesOf returns a copy', () => {
  rolesOf('visitor-viewer-0a1b2c3d').push('config:admin')
  assert.deepEqual(PERSONAS.viewer.roles, ['config:read'])
})

test('issuer helpers', () => {
  assert.equal(issuerPrefix('https://demo.example.com/idp'), '/idp')
  assert.equal(issuerPrefix('https://demo.example.com/idp/'), '/idp')
  assert.equal(issuerPrefix('http://localhost:9000'), '')
  assert.equal(normalizeIssuer('https://demo.example.com/idp/'), 'https://demo.example.com/idp')
})

test('parseList splits on commas and whitespace', () => {
  assert.deepEqual(parseList('a, b\nc,,'), ['a', 'b', 'c'])
  assert.deepEqual(parseList('', ['x']), ['x'])
  assert.deepEqual(parseList(undefined), [])
})

test('rate limiter is a sliding window per key', () => {
  let t = 0
  const limiter = createRateLimiter({ limit: 2, windowMs: 1000, now: () => t })
  assert.ok(limiter.take('a'))
  assert.ok(limiter.take('a'))
  assert.ok(!limiter.take('a'))
  assert.ok(limiter.take('b'))
  t = 1001
  assert.ok(limiter.take('a'))
})

test('login page lists every persona and escapes the uid', () => {
  const html = loginPage({ uid: 'x"><script>', prefix: '/idp' })
  for (const id of Object.keys(PERSONAS)) assert.ok(html.includes(`value="${id}"`))
  assert.ok(!html.includes('<script>'))
  assert.ok(html.includes('action="/idp/interaction/'))
})
