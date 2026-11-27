#!/bin/sh
set -eu
umask 077

usage() {
    echo "Usage: $0 --install-root PATH --secrets-dir PATH" >&2
    exit 2
}

install_root=
secrets_dir=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --install-root) [ "$#" -ge 2 ] || usage; install_root=$2; shift 2 ;;
        --secrets-dir) [ "$#" -ge 2 ] || usage; secrets_dir=$2; shift 2 ;;
        *) usage ;;
    esac
done
[ -n "$install_root" ] && [ -n "$secrets_dir" ] || usage

case "$install_root" in /*) ;; *) echo "Installation root must be absolute." >&2; exit 1 ;; esac
case "$secrets_dir" in /*) ;; *) echo "Secrets directory must be absolute." >&2; exit 1 ;; esac
[ ! -e "$install_root" ] || {
    echo "Refusing to overwrite an existing installation root." >&2
    exit 1
}
[ ! -e "$secrets_dir" ] || {
    echo "Refusing to overwrite an existing secrets directory." >&2
    exit 1
}
command -v python3 >/dev/null || {
    echo "Python 3 is required to verify the release." >&2
    exit 1
}
command -v openssl >/dev/null || {
    echo "OpenSSL is required to create the private OpenBao service certificate." >&2
    exit 1
}
command -v ssh-keygen >/dev/null || {
    echo "OpenSSH is required to create the worker key." >&2
    exit 1
}

source_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
python3 -B "$source_dir/verify_bundle.py"
mkdir -p -- "$(dirname -- "$install_root")" "$(dirname -- "$secrets_dir")"
python3 -B - "$source_dir" "$install_root" <<'PY'
from pathlib import Path
import shutil
import sys

source, destination = map(Path, sys.argv[1:])
if source.is_symlink() or destination.exists() or destination.is_symlink():
    raise SystemExit("Unsafe installer source or destination.")
for path in source.rglob("*"):
    if path.is_symlink():
        raise SystemExit(f"Symlink release entry is forbidden: {path}")
shutil.copytree(source, destination, symlinks=False)
PY

mkdir -m 700 "$secrets_dir"
mkdir -m 700 "$secrets_dir/ssh" "$secrets_dir/bootstrap" "$secrets_dir/openbao-approle"
: > "$secrets_dir/openbao-approle/role-id"
: > "$secrets_dir/openbao-approle/secret-id"
chmod 600 "$secrets_dir/openbao-approle/role-id" "$secrets_dir/openbao-approle/secret-id"
python3 -B - "$secrets_dir" <<'PY'
from pathlib import Path
import secrets
import sys

root = Path(sys.argv[1])
for name in (
    "postgres-admin-password",
    "app-db-password",
    "kratos-db-password",
    "workspace-gateway-credential",
):
    path = root / name
    path.write_text(secrets.token_hex(32) + "\n")
    path.chmod(0o600)
kratos_password = (root / "kratos-db-password").read_text().strip()
(root / "kratos.env").write_text(
    f"DSN=postgres://kratos:{kratos_password}@postgres:5432/kratos?sslmode=disable\n"
    f"SECRETS_COOKIE={secrets.token_hex(32)}\n"
    f"SECRETS_CIPHER={secrets.token_urlsafe(24)}\n"
)
(root / "kratos.env").chmod(0o600)
PY

ssh-keygen -q -t ed25519 -N "" -f "$secrets_dir/ssh/worker-key"
chmod 600 "$secrets_dir/ssh/worker-key"
chmod 644 "$secrets_dir/ssh/worker-key.pub"
openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 3650 \
    -subj "/CN=openbao" -addext "subjectAltName=DNS:openbao" \
    -keyout "$secrets_dir/openbao-server.key" \
    -out "$secrets_dir/openbao-server.crt" >/dev/null 2>&1
cp "$secrets_dir/openbao-server.crt" "$secrets_dir/openbao-ca.crt"
chmod 600 "$secrets_dir/openbao-server.key"
chmod 644 "$secrets_dir/openbao-server.crt" "$secrets_dir/openbao-ca.crt"

python3 -B - "$install_root/.env.example" "$install_root/.env" \
    "$secrets_dir" "$secrets_dir/ssh/worker-key.pub" <<'PY'
from pathlib import Path
import sys

source, destination, secrets, public_key = map(Path, sys.argv[1:])
values = {
    "MUNICODE_SECRETS_DIR": str(secrets),
    "MUNICODE_WORKER_SSH_PUBLIC_KEY": public_key.read_text().strip(),
}
lines = []
for line in source.read_text().splitlines():
    key = line.partition("=")[0]
    lines.append(f"{key}={values[key]}" if key in values else line)
destination.write_text("\n".join(lines) + "\n")
PY
chmod 600 "$install_root/.env"

printf '%s\n' "Installed release at $install_root."
printf '%s\n' "Edit $install_root/.env and continue with its operator guide before startup."
