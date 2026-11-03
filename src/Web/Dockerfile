# Web UI + reverse proxy for the public sandbox. Build context: src/Web
#   docker build -t config-server-web src/Web [--build-arg VITE_API_BASE_URL=/]
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# "/" = same origin (the nginx below proxies /api, /config and /idp). Real env vars win over any .env file.
ARG VITE_API_BASE_URL=/
ENV VITE_API_BASE_URL=${VITE_API_BASE_URL}
RUN npm run build

# Unprivileged nginx: non-root (uid 101), listens on 8080, renders /etc/nginx/templates/*.template at start.
FROM nginxinc/nginx-unprivileged:1.28-alpine
ENV DEMO_API_UPSTREAM=config-demo-api:8080 \
    DEMO_IDP_UPSTREAM=config-demo-idp:9000 \
    NGINX_ENVSUBST_FILTER=^DEMO_
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY nginx/security-headers.conf nginx/proxy.conf /etc/nginx/snippets/
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/nginx-health || exit 1
