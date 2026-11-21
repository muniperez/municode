#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
git check-ignore -q .local/artifacts/runtime/check
mkdir -p .local/artifacts/runtime
mkdir -p .local/artifacts/tmp
export TMPDIR="$PWD/.local/artifacts/tmp"
PYTHONDONTWRITEBYTECODE=1 python3 -B -m unittest discover \
  -s templates/development-workspaces/tests -v
