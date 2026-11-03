// Pure helpers of the demo IdP (no oidc-provider import): personas, visitor identities, pages, rate limiting.
import { randomBytes } from 'node:crypto'

export const CLIENT_ID = 'config-demo-web'
/** Audience of the access tokens: the API falls back to Auth:Oidc:ClientId, which is this same value. */
export const AUDIENCE = CLIENT_ID
/** oidc-provider resource indicators must be absolute URIs; the token's `aud` is rewritten to AUDIENCE (see app.js). */
export const RESOURCE = 'urn:config-demo:api'

/** Role format: config:<read|write|admin>[:<app>[:<env>]] (omitted app/env = all; admin is global only). */
export const PERSONAS = {
  viewer: {
    label: 'Viewer',
    summary: 'Reads every application and environment.',
    roles: ['config:read'],
  },
  developer: {
    label: 'Developer',
    summary: 'Reads everything and edits the dev environment of every application.',
    roles: ['config:read', 'config:write:*:dev'],
  },
  admin: {
    label: 'Admin',
    summary: 'Full access: edits everything, manages API keys and who can access what.',
    roles: ['config:admin', 'config:write', 'config:read'],
  },
}

const VISITOR_ID = /^visitor-(viewer|developer|admin)-([0-9a-f]{8})$/

export const isPersona = (p) => typeof p === 'string' && Object.hasOwn(PERSONAS, p)

/** A fresh identity per sign-in: the API derives each visitor's private workspace from `sub`. */
export function newVisitorId(persona, random = () => randomBytes(4).toString('hex')) {
  if (!isPersona(persona)) throw new Error(`unknown persona: ${persona}`)
  return `visitor-${persona}-${random()}`
}

export function parseVisitorId(id) {
  const m = VISITOR_ID.exec(typeof id === 'string' ? id : '')
  return m ? { persona: m[1], suffix: m[2] } : null
}

export function visitorName(id) {
  const v = parseVisitorId(id)
  return v ? `Visitor ${PERSONAS[v.persona].label} ${v.suffix.slice(0, 4)}` : null
}

export const rolesOf = (id) => {
  const v = parseVisitorId(id)
  return v ? [...PERSONAS[v.persona].roles] : []
}

/** Extra access-token claims: Keycloak-shaped client roles plus a flat `roles` (the API's default claim). */
export function tokenClaimsFor(id) {
  const v = parseVisitorId(id)
  if (!v) return {}
  const roles = rolesOf(id)
  return { name: visitorName(id), roles, resource_access: { [CLIENT_ID]: { roles } } }
}

// ---- configuration parsing

export const parseList = (value, fallback = []) => {
  const items = String(value ?? '')
    .split(/[,\s]+/)
    .map((s) => s.trim())
    .filter(Boolean)
  return items.length ? items : fallback
}

/** `https://host/idp/` => `/idp` (no trailing slash; `''` when the issuer is at the root). */
export function issuerPrefix(issuer) {
  const path = new URL(issuer).pathname.replace(/\/+$/, '')
  return path === '/' ? '' : path
}

export const normalizeIssuer = (issuer) => new URL(issuer).toString().replace(/\/+$/, '')

// ---- rate limit (in memory, per key): sliding window

export function createRateLimiter({ limit, windowMs, now = () => Date.now() }) {
  const hits = new Map()
  return {
    /** True when the call is allowed (and counted). */
    take(key) {
      const t = now()
      const recent = (hits.get(key) ?? []).filter((at) => t - at < windowMs)
      if (recent.length >= limit) {
        hits.set(key, recent)
        return false
      }
      recent.push(t)
      hits.set(key, recent)
      if (hits.size > 5000) for (const [k, v] of hits) if (v.every((at) => t - at >= windowMs)) hits.delete(k)
      return true
    },
  }
}

// ---- pages

export const escapeHtml = (s) =>
  String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c])

/** Same dark look as the app: surface #121212, olive accent #b4b96b, monospace. */
const STYLE = `
:root{color-scheme:dark;--bg:#121212;--card:#1c1c1c;--line:#333;--text:#f1f1ec;--muted:#a3a39a;--accent:#b4b96b}
*{box-sizing:border-box}
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:var(--bg);color:var(--text);font-family:'JetBrains Mono',ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:14px;line-height:1.5}
main{width:100%;max-width:30rem;padding:1.5rem}
.brand{display:flex;align-items:center;gap:.6rem;margin-bottom:1.25rem;font-weight:600;font-size:1.05rem}
.brand svg{height:26px;width:auto;color:var(--text);overflow:visible}
h1{font-size:1.15rem;margin:0 0 .35rem}
p{margin:.25rem 0 1rem;color:var(--muted)}
form{margin:0 0 .75rem}
button{width:100%;text-align:left;cursor:pointer;padding:.85rem 1rem;background:var(--card);color:var(--text);border:1px solid var(--line);border-radius:10px;font:inherit}
button:hover,button:focus-visible{border-color:var(--accent);outline:none}
button strong{display:block;color:var(--accent)}
button span{display:block;color:var(--muted);font-size:.85rem;margin-top:.15rem}
button code{color:var(--text);font-size:.78rem}
.primary{background:var(--accent);color:#16160f;border-color:var(--accent);text-align:center}
.note{margin-top:1.25rem;font-size:.8rem;color:var(--muted)}
`

// Same "split" mark as the app (components/AppLogo.vue): a V in the text colour interlocked with its mirror in the accent.
const LOGO = `<svg viewBox="0 19.74 135.47 96" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M45.4 103.5 V84.5 L67.74 32 L90.1 84.5 V103.5" fill="none" stroke="#b4b96b" stroke-opacity="0.9" stroke-width="10"/><path d="M45.4 32 V51 L67.74 103.5 L90.1 51 V32" fill="none" stroke="currentColor" stroke-opacity="0.85" stroke-width="10"/></svg>`

const layout = (title, body) => `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>${escapeHtml(title)}</title><style>${STYLE}</style></head>
<body><main><div class="brand">${LOGO}<span>Config Server demo</span></div>${body}</main></body></html>`

export function loginPage({ uid, prefix = '', clientName = 'Config Server' }) {
  const cards = Object.entries(PERSONAS)
    .map(
      ([id, p]) => `<form method="post" action="${escapeHtml(prefix)}/interaction/${escapeHtml(uid)}/login">
<input type="hidden" name="persona" value="${id}">
<button type="submit"><strong>${escapeHtml(p.label)}</strong><span>${escapeHtml(p.summary)}</span><span><code>${escapeHtml(p.roles.join('  '))}</code></span></button></form>`,
    )
    .join('')
  return layout(
    'Sign in to the demo',
    `<h1>Sign in to ${escapeHtml(clientName)}</h1>
<p>No password. Pick a role: each sign-in creates a throwaway visitor with its own private workspace of sample data.</p>
${cards}
<p class="note">This is a public sandbox. Do not enter real secrets or personal data.</p>`,
  )
}

export function logoutPage(formHtml) {
  return layout(
    'Sign out',
    `<h1>Sign out of the demo?</h1><p>Your workspace is discarded after a while of inactivity.</p>
${formHtml}
<button class="primary" type="submit" form="op.logoutForm" name="logout" value="yes">Yes, sign me out</button>
<p></p><button type="submit" form="op.logoutForm">No, stay signed in</button>`,
  )
}

export function messagePage(title, message) {
  return layout(title, `<h1>${escapeHtml(title)}</h1><p>${escapeHtml(message)}</p>`)
}

/** CSP for the pages this IdP renders itself (inline style only, no script). */
export const PAGE_CSP = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'"
