#!/usr/bin/env bash
# Publishes the native AOT ailog executable to ./dist
set -euo pipefail
cd "$(dirname "$0")"
dotnet publish src/AiLog.Host -c Release -r "${1:-osx-arm64}" -o dist
