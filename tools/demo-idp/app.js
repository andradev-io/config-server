// OIDC provider for the public sandbox. See README.md. Never use it for real accounts: there is no password.
import Provider from 'oidc-provider'
import http from 'node:http'
import { randomBytes } from 'node:crypto'
import {
  AUDIENCE,
  RESOURCE,
  CLIENT_ID,
  PAGE_CSP,
  createRateLimiter,
  isPersona,
  issuerPrefix,
  loginPage,
  logoutPage,
  messagePage,
  newVisitorId,
  normalizeIssuer,
  parseVisitorId,
  tokenClaimsFor,
  visitorName,
} from './lib.js'

const SCOPES = 'openid profile offline_access'

/**
 * @param {object} o
 * @param {string} o.issuer public issuer URL, e.g. https://host/idp (its path is the mount prefix)
 * @param {string[]} o.redirectUris redirect and post-logout URIs registered for the SPA client
 * @param {boolean} [o.trustProxy] honour X-Forwarded-* (default true: it sits behind nginx/Traefik/Cloudflare)
 * @param {string[]} [o.cookieKeys]
 * @param {number} [o.accessTokenTtl] seconds
 * @param {number} [o.sessionTtl] seconds (session, grant and refresh token)
 * @param {number} [o.loginsPerWindow] max sign-ins per client address and window
 * @param {number} [o.loginWindowMs]
 */
export function createDemoIdp(o) {
  const issuer = normalizeIssuer(o.issuer)
  const prefix = issuerPrefix(issuer)
  const accessTokenTtl = o.accessTokenTtl ?? 900
  const sessionTtl = o.sessionTtl ?? 8 * 3600
  const limiter = createRateLimiter({ limit: o.loginsPerWindow ?? 30, windowMs: o.loginWindowMs ?? 10 * 60 * 1000 })

  const provider = new Provider(issuer, {
    clients: [
      {
        client_id: CLIENT_ID,
        client_name: 'Config Server demo',
        token_endpoint_auth_method: 'none',
        grant_types: ['authorization_code', 'refresh_token'],
        response_types: ['code'],
        redirect_uris: o.redirectUris,
        post_logout_redirect_uris: o.redirectUris,
      },
    ],
    pkce: { required: () => true },
    scopes: SCOPES.split(' '),
    claims: { openid: ['sub'], profile: ['name'] },
    cookies: { keys: o.cookieKeys?.length ? o.cookieKeys : [randomBytes(32).toString('hex')] },
    clientBasedCORS: (_ctx, origin, client) => client.redirectUris.some((u) => new URL(u).origin === origin),
    features: {
      devInteractions: { enabled: false },
      rpInitiatedLogout: {
        logoutSource: (ctx, form) => {
          ctx.type = 'html'
          ctx.set('Content-Security-Policy', PAGE_CSP)
          ctx.body = logoutPage(form)
        },
        postLogoutSuccessSource: (ctx) => {
          ctx.type = 'html'
          ctx.set('Content-Security-Policy', PAGE_CSP)
          ctx.body = messagePage('Signed out', 'You can close this tab or sign in again.')
        },
      },
      resourceIndicators: {
        enabled: true,
        defaultResource: () => RESOURCE,
        useGrantedResource: () => true,
        getResourceServerInfo: () => ({
          scope: SCOPES,
          audience: RESOURCE,
          accessTokenFormat: 'jwt',
          accessTokenTTL: accessTokenTtl,
          jwt: { sign: { alg: 'RS256' } },
        }),
      },
    },
    formats: {
      customizers: {
        // The API validates `aud` against Auth:Oidc:Audience, which defaults to the client id.
        jwt: async (_ctx, _token, jwt) => {
          jwt.payload.aud = AUDIENCE
        },
      },
    },
    issueRefreshToken: () => true,
    // Stateless accounts: everything is encoded in the id (`visitor-<persona>-<hex>`), nothing is stored.
    findAccount: async (_ctx, id) => {
      const name = visitorName(id)
      return name ? { accountId: id, claims: async () => ({ sub: id, name }) } : undefined
    },
    extraTokenClaims: async (_ctx, token) => tokenClaimsFor(token.accountId),
    interactions: { url: (_ctx, interaction) => `${prefix}/interaction/${interaction.uid}` },
    renderError: async (ctx, out) => {
      ctx.type = 'html'
      ctx.set('Content-Security-Policy', PAGE_CSP)
      ctx.body = messagePage('Something went wrong', out.error_description || out.error || 'Unexpected error.')
    },
    ttl: {
      AccessToken: accessTokenTtl,
      IdToken: accessTokenTtl,
      Session: sessionTtl,
      Interaction: 600,
      Grant: sessionTtl,
      RefreshToken: sessionTtl,
    },
  })
  if (o.trustProxy ?? true) provider.proxy = true

  const oidc = provider.callback()

  const send = (res, status, body, type = 'text/plain; charset=utf-8', headers = {}) => {
    res.writeHead(status, { 'content-type': type, 'cache-control': 'no-store', ...headers })
    res.end(body)
  }
  const sendPage = (res, status, html) =>
    send(res, status, html, 'text/html; charset=utf-8', { 'content-security-policy': PAGE_CSP, 'x-content-type-options': 'nosniff' })

  /** Best effort client address for the rate limit (Cloudflare header, then the proxy chain). */
  const clientKey = (req) =>
    String(req.headers['cf-connecting-ip'] ?? req.headers['x-forwarded-for'] ?? '').split(',')[0].trim() ||
    req.socket.remoteAddress ||
    'unknown'

  const handle = async (req, res) => {
    const path = new URL(req.url, 'http://idp').pathname
    if (path === '/healthz' || path === `${prefix}/healthz`) return send(res, 200, 'ok')
    if (prefix && path !== prefix && !path.startsWith(`${prefix}/`)) return send(res, 404, 'Not found')

    // Mounted under the issuer path: hand the provider the path below the prefix (as Express would) and
    // keep the full one in originalUrl so it builds absolute URLs with the prefix.
    req.originalUrl = req.url
    req.url = req.url.slice(prefix.length) || '/'

    const m = /^\/interaction\/([^/?#]+)(\/login)?(?:[?#]|$)/.exec(req.url)
    if (!m) return oidc(req, res)

    const details = await provider.interactionDetails(req, res)
    if (req.method === 'GET' && !m[2]) {
      return sendPage(res, 200, loginPage({ uid: details.uid, prefix, clientName: 'Config Server' }))
    }
    if (req.method === 'POST' && m[2]) {
      if (!limiter.take(clientKey(req))) {
        return sendPage(res, 429, messagePage('Too many sign-ins', 'Wait a few minutes and try again.'))
      }
      let body = ''
      for await (const chunk of req) {
        body += chunk
        if (body.length > 2048) return send(res, 413, 'Too large')
      }
      const persona = new URLSearchParams(body).get('persona')
      if (!isPersona(persona)) return sendPage(res, 400, messagePage('Unknown role', 'Pick one of the listed roles.'))

      const accountId = newVisitorId(persona)
      const grant = new provider.Grant({ accountId, clientId: details.params.client_id })
      grant.addOIDCScope(SCOPES)
      grant.addResourceScope(RESOURCE, SCOPES)
      const grantId = await grant.save()
      return provider.interactionFinished(
        req,
        res,
        { login: { accountId }, consent: { grantId } },
        { mergeWithLastSubmission: false },
      )
    }
    return send(res, 404, 'Not found')
  }

  const server = http.createServer((req, res) => {
    handle(req, res).catch((e) => {
      console.error('demo-idp error:', e?.stack ?? e)
      if (!res.headersSent) sendPage(res, 500, messagePage('Something went wrong', 'Try again from the application.'))
      else res.end()
    })
  })

  return { server, provider, issuer, prefix, parseVisitorId }
}
