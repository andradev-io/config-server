# Demo OIDC provider (sandbox only). Build context: tools/demo-idp
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev

FROM node:22-alpine
ENV NODE_ENV=production PORT=9000
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY package.json app.js lib.js server.js ./
# Stateless and read-only friendly: nothing is written to disk.
USER node
EXPOSE 9000
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -qO- http://127.0.0.1:9000/healthz || exit 1
CMD ["node", "server.js"]
