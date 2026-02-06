#!/bin/bash
set -e

IMAGE_NAME="kanbeast"
CONTAINER_NAME="kanbeast-server"
NETWORK_NAME="kanbeast-network"

echo "Building KanBeast..."
docker build -t "$IMAGE_NAME" -f Dockerfile .

echo "Cleaning up old containers..."
docker rm -f "$CONTAINER_NAME" 2>/dev/null || true
docker network rm "$NETWORK_NAME" 2>/dev/null || true

echo "Creating network..."
docker network create "$NETWORK_NAME"

echo "Starting KanBeast at http://localhost:8080"

docker run --rm -it \
  --name "$CONTAINER_NAME" \
  --network "$NETWORK_NAME" \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:8080 \
  -v "$(pwd)/env:/workspace" \
  -v /var/run/docker.sock:/var/run/docker.sock \
  "$IMAGE_NAME"
