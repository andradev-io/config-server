// Entry point: configuration from the environment (see README.md).
import { createDemoIdp } from './app.js'
import { parseList } from './lib.js'

const PORT = Number(process.env.PORT ?? 9000)
const ISSUER = process.env.DEMO_IDP_ISSUER ?? `http://localhost:${PORT}/idp`
const REDIRECT_URIS = parseList(process.env.DEMO_IDP_REDIRECT_URIS, ['http://localhost:5173/'])

const { server, issuer } = createDemoIdp({
  issuer: ISSUER,
  redirectUris: REDIRECT_URIS,
  trustProxy: process.env.DEMO_IDP_TRUST_PROXY !== 'false',
  cookieKeys: parseList(process.env.DEMO_IDP_COOKIE_KEYS),
  accessTokenTtl: Number(process.env.DEMO_IDP_ACCESS_TOKEN_TTL ?? 900),
  sessionTtl: Number(process.env.DEMO_IDP_SESSION_TTL ?? 8 * 3600),
  loginsPerWindow: Number(process.env.DEMO_IDP_LOGINS_PER_10_MIN ?? 30),
})

server.listen(PORT, '0.0.0.0', () => console.log(`Demo IdP listening on :${PORT}, issuer ${issuer}, redirects ${REDIRECT_URIS.join(' ')}`))

for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => server.close(() => process.exit(0)))
