#!/bin/sh
set -eu
umask 077

source_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec python3 -B "$source_dir/install_host.py" "$@"
