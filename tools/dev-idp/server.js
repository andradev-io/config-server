// Dev-only OIDC provider. Users: alice (admin), carol (writer), bob (reader), dave (none), erin/frank (scoped). Never expose publicly.
// Roles are emitted twice in the access token: flat `roles` and Keycloak-style `resource_access.<client>.roles`.
import Provider from 'oidc-provider'
import http from 'node:http'

const PORT = Number(process.env.PORT ?? 9000)
const ISSUER = process.env.ISSUER ?? `http://localhost:${PORT}`
const AUDIENCE = 'http://localhost:5000'
const SPA = process.env.SPA_ORIGIN ?? 'http://localhost:5173'
const CLIENT_ID = 'config-web'

// Role format: config:<read|write|admin>[:<app>[:<env>]] (app/env omitted = all; admin is global only).
const USERS = {
  alice: { name: 'Alice (admin)', roles: ['config:admin'] },
  carol: { name: 'Carol (writer)', roles: ['config:write'] },
  bob: { name: 'Bob (reader)', roles: ['config:read'] },
  dave: { name: 'Dave (no roles)', roles: [] },
  erin: { name: 'Erin (billing dev writer)', roles: ['config:write:billing-service:dev', 'config:read:billing-service:stg'] },
  frank: { name: 'Frank (demo-app reader)', roles: ['config:read:demo-app'] },
}

const provider = new Provider(ISSUER, {
  clients: [
    {
      client_id: CLIENT_ID,
      token_endpoint_auth_method: 'none',
      grant_types: ['authorization_code', 'refresh_token'],
      response_types: ['code'],
      redirect_uris: [`${SPA}/`, `${SPA}/silent-renew.html`, `${SPA}`],
      post_logout_redirect_uris: [`${SPA}/`, SPA],
    },
  ],
  pkce: { required: () => true },
  scopes: ['openid', 'profile', 'offline_access'],
  claims: { openid: ['sub'], profile: ['name'] },
  cookies: { keys: ['dev-only-not-secret'] },
  features: {
    devInteractions: { enabled: false },
    resourceIndicators: {
      enabled: true,
      defaultResource: () => AUDIENCE,
      useGrantedResource: () => true,
      getResourceServerInfo: () => ({
        scope: 'openid profile offline_access',
        audience: AUDIENCE,
        accessTokenFormat: 'jwt',
        accessTokenTTL: 3600,
        jwt: { sign: { alg: 'RS256' } },
      }),
    },
  },
  issueRefreshToken: () => true,
  findAccount: async (_ctx, id) => ({
    accountId: id,
    claims: async () => ({ sub: id, name: USERS[id]?.name ?? id }),
  }),
  extraTokenClaims: async (_ctx, token) => {
    const u = USERS[token.accountId]
    return u ? { roles: u.roles, resource_access: { [CLIENT_ID]: { roles: u.roles } }, name: u.name } : {}
  },
  interactions: { url: (_ctx, interaction) => `/interaction/${interaction.uid}` },
  ttl: { AccessToken: 3600, IdToken: 3600, Session: 86400, Interaction: 3600, Grant: 86400, RefreshToken: 86400 },
})

const page = (uid) => `<!doctype html><meta charset=utf-8><title>Dev IdP</title>
<body style="font-family:monospace;background:#121212;color:#f5f5f5;padding:2rem;max-width:30rem;margin:auto">
<h2>Dev IdP sign-in</h2><p>Pick a user (dev only, no password).</p>
${Object.entries(USERS).map(([id, u]) => `<form method=post action=/interaction/${uid}/login style="margin:.5rem 0">
<input type=hidden name=user value=${id}><button style="width:100%;padding:.8rem;background:#222;color:#f5f5f5;border:1px solid #b4b96b;cursor:pointer">${u.name} — ${u.roles.join(', ') || 'no roles'}</button></form>`).join('')}
</body>`

const app = provider.app
const oidc = provider.callback()
const server = http.createServer(async (req, res) => {
  const m = req.url.match(/^\/interaction\/([^/?]+)(\/login)?/)
  if (!m) return oidc(req, res)
  try {
    const details = await provider.interactionDetails(req, res)
    if (req.method === 'GET' && !m[2]) {
      res.setHeader('content-type', 'text/html')
      return res.end(page(details.uid))
    }
    if (req.method === 'POST' && m[2]) {
      let body = ''
      for await (const c of req) body += c
      const user = new URLSearchParams(body).get('user')
      if (!USERS[user]) { res.statusCode = 400; return res.end('unknown user') }
      const grant = new provider.Grant({ accountId: user, clientId: details.params.client_id })
      grant.addOIDCScope('openid profile offline_access')
      grant.addResourceScope(AUDIENCE, 'openid profile offline_access')
      const grantId = await grant.save()
      return await provider.interactionFinished(req, res, { login: { accountId: user }, consent: { grantId } }, { mergeWithLastSubmission: false })
    }
    res.statusCode = 404; res.end()
  } catch (e) { res.statusCode = 500; res.end(String(e?.message ?? e)) }
})
server.listen(PORT, () => console.log(`Dev IdP on ${ISSUER}  (client ${CLIENT_ID}, audience ${AUDIENCE})`))
