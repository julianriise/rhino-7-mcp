#!/usr/bin/env bash
# Start the local Python MCP server from this checkout (matched to the plugin).
# Grok Build (and other MCP clients) should launch this script, not `uvx rhinomcp`.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
exec uv run --directory "$ROOT/server" rhinomcp "$@"
