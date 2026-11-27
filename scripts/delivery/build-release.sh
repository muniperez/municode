#!/bin/sh
# SPDX-License-Identifier: MPL-2.0
set -eu
exec python3 -B "$(dirname "$0")/build_release.py" "$@"
