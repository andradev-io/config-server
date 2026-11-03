#!/usr/bin/env bash
# Builds the three sandbox images (no push). Usage:
#   deploy/build.sh <registry-prefix> <tag>          e.g. deploy/build.sh ghcr.io/andradev-io 0.1.0
#   ENGINE=podman deploy/build.sh ...                 (default: docker)
# Images: <registry>/config-server-api-demo, <registry>/config-server-web, <registry>/config-server-demo-idp
set -euo pipefail

REGISTRY="${1:?usage: build.sh <registry-prefix> <tag>}"
TAG="${2:?usage: build.sh <registry-prefix> <tag>}"
ENGINE="${ENGINE:-docker}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

[ -d demo-data ] || { echo "demo-data/ is missing: the API image seeds the visitors' workspaces from it" >&2; exit 1; }

"$ENGINE" build -f deploy/Dockerfile.api-demo -t "$REGISTRY/config-server-api-demo:$TAG" .
"$ENGINE" build --build-arg VITE_API_BASE_URL=/ -t "$REGISTRY/config-server-web:$TAG" src/Web
"$ENGINE" build -t "$REGISTRY/config-server-demo-idp:$TAG" tools/demo-idp

echo
echo "Built:"
echo "  $REGISTRY/config-server-api-demo:$TAG"
echo "  $REGISTRY/config-server-web:$TAG"
echo "  $REGISTRY/config-server-demo-idp:$TAG"
echo "Push them with: for i in api-demo web demo-idp; do $ENGINE push $REGISTRY/config-server-\$i:$TAG; done"
