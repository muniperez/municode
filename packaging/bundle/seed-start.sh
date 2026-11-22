#!/bin/sh
# SPDX-License-Identifier: MPL-2.0
set -eu
umask 077

usage() {
    echo "Usage: seed-start.sh --release-tag TAG --release-root PATH --installation-id ID" >&2
    echo "  --dashboard-hostname HOST --api-hostname HOST --gateway-domain DOMAIN" >&2
    echo "  --install-root PATH --secrets-dir PATH --setup-token-output PATH" >&2
    exit 2
}

release_tag=
release_root=
installation_id=
dashboard_hostname=
api_hostname=
gateway_domain=
install_root=
secrets_dir=
setup_token_output=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --release-tag) [ "$#" -ge 2 ] || usage; release_tag=$2; shift 2 ;;
        --release-root) [ "$#" -ge 2 ] || usage; release_root=$2; shift 2 ;;
        --installation-id) [ "$#" -ge 2 ] || usage; installation_id=$2; shift 2 ;;
        --dashboard-hostname) [ "$#" -ge 2 ] || usage; dashboard_hostname=$2; shift 2 ;;
        --api-hostname) [ "$#" -ge 2 ] || usage; api_hostname=$2; shift 2 ;;
        --gateway-domain) [ "$#" -ge 2 ] || usage; gateway_domain=$2; shift 2 ;;
        --install-root) [ "$#" -ge 2 ] || usage; install_root=$2; shift 2 ;;
        --secrets-dir) [ "$#" -ge 2 ] || usage; secrets_dir=$2; shift 2 ;;
        --setup-token-output) [ "$#" -ge 2 ] || usage; setup_token_output=$2; shift 2 ;;
        *) usage ;;
    esac
done
[ -n "$release_tag" ] && [ -n "$release_root" ] && [ -n "$installation_id" ] &&
[ -n "$dashboard_hostname" ] && [ -n "$api_hostname" ] && [ -n "$gateway_domain" ] &&
[ -n "$install_root" ] && [ -n "$secrets_dir" ] && [ -n "$setup_token_output" ] || usage

case "$release_root:$install_root:$secrets_dir:$setup_token_output" in
    /*:*:*:/*) ;;
    *) echo "Seed installation paths must be absolute." >&2; exit 1 ;;
esac
[ -d "$release_root" ] && [ -f "$release_root/install.sh" ] || {
    echo "The exact extracted release root is unavailable." >&2
    exit 1
}
for command in docker python3; do
    command -v "$command" >/dev/null || {
        echo "Required host command is unavailable: $command" >&2
        exit 1
    }
done
docker compose version >/dev/null

marker="$secrets_dir/seed-install-complete"
owner_record="$secrets_dir.seed-owner"
[ "$install_root" = /opt/municode/workspaces ] &&
[ "$secrets_dir" = /etc/municode/workspaces/secrets ] || {
    echo "Seed owns only /opt/municode/workspaces and /etc/municode/workspaces/secrets." >&2
    exit 1
}
if [ -e "$install_root" ] || [ -e "$secrets_dir" ]; then
    if [ ! -f "$marker" ]; then
        [ -s "$owner_record" ] &&
        grep -Fqx "$installation_id $release_tag" "$owner_record" || {
            echo "A partial or unrelated installation root/secrets directory exists without seed ownership." >&2
            exit 1
        }
        rm -rf -- /opt/municode/workspaces /etc/municode/workspaces/secrets
    fi
fi
if [ ! -e "$install_root" ] && [ ! -e "$secrets_dir" ]; then
    install -d -m 700 "$(dirname "$owner_record")"
    printf '%s %s\n' "$installation_id" "$release_tag" > "$owner_record.pending"
    chmod 600 "$owner_record.pending"
    mv "$owner_record.pending" "$owner_record"
    "$release_root/install.sh" --install-root "$install_root" --secrets-dir "$secrets_dir"
    : > "$marker"
    chmod 600 "$marker"
fi
if [ ! -f "$marker" ]; then
    echo "Seed installation ownership or completion marker is incomplete." >&2
    exit 1
fi

python3 -B - "$install_root/.env" "$secrets_dir" \
    "$installation_id" "$dashboard_hostname" "$api_hostname" "$gateway_domain" <<'PY'
from pathlib import Path
import os
import re
import sys

path, secrets, installation, dashboard, api, gateway = sys.argv[1:]
if any(not isinstance(v, str) or not v for v in sys.argv[1:]):
    raise SystemExit("Invalid seed configuration.")
for host in (dashboard, api, gateway):
    if not re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+", host):
        raise SystemExit("Unsafe public hostname.")
values = {
    "WORKSPACES_INSTALLATION_ID": installation,
    "CONSOLE_HOSTNAME": dashboard,
    "CONSOLE_URL": "https://" + dashboard,
    "API_HOSTNAME": api,
    "API_URL": "https://" + api,
    "WORKSPACES_GATEWAY_DOMAIN": gateway,
    "WORKSPACES_SECRETS_DIR": secrets,
}
lines = []
seen = set()
for line in Path(path).read_text().splitlines():
    key = line.partition("=")[0]
    value = values.get(key)
    if value is None:
        lines.append(line)
    else:
        lines.append(f"{key}={value}")
        seen.add(key)
if seen != set(values):
    raise SystemExit("The exact release environment contract changed.")
temporary = Path(path + ".pending")
temporary.write_text("\n".join(lines) + "\n")
os.chmod(temporary, 0o600)
os.replace(temporary, path)
PY
chmod 600 "$install_root/.env"

cd "$install_root"
docker compose up -d postgres openbao

openbao_init="$secrets_dir/openbao-seed-init.json"
openbao_pending="$openbao_init.pending"
if [ ! -s "$openbao_init" ]; then
    initialized="$(docker compose exec -T openbao \
        sh -c 'bao status -format=json 2>/dev/null || true' |
        python3 -c 'import json,sys
try: print(str(json.load(sys.stdin).get("initialized", False)).lower())
except Exception: print("false")')"
    if [ -e "$openbao_pending" ]; then
        if python3 -B - "$openbao_pending" <<'PY'
import json
import sys
value = json.load(open(sys.argv[1]))
assert len(value["unseal_keys_b64"]) == 1
assert isinstance(value["root_token"], str) and value["root_token"]
PY
        then
            mv "$openbao_pending" "$openbao_init"
        elif [ "$initialized" = "false" ]; then
            rm -f "$openbao_pending"
        else
            echo "OpenBao initialized but seed lost complete unseal material. Preserve the host and reconcile recovery." >&2
            exit 1
        fi
    fi
    if [ ! -s "$openbao_init" ]; then
        [ "$initialized" = "false" ] || {
            echo "OpenBao is initialized but its seed recovery record is unavailable." >&2
            exit 1
        }
        docker compose exec -T openbao \
            bao operator init -key-shares=1 -key-threshold=1 -format=json > "$openbao_pending"
        chmod 600 "$openbao_pending"
        python3 -B - "$openbao_pending" <<'PY'
import json
import sys
value = json.load(open(sys.argv[1]))
assert len(value["unseal_keys_b64"]) == 1
assert isinstance(value["root_token"], str) and value["root_token"]
PY
        mv "$openbao_pending" "$openbao_init"
    fi
fi

python3 -B - "$openbao_init" "$secrets_dir/openbao-unseal-key" \
    "$secrets_dir/openbao-root-token" <<'PY'
from pathlib import Path
import json
import os
import sys

source, unseal, root = map(Path, sys.argv[1:])
value = json.loads(source.read_text())
keys = value["unseal_keys_b64"]
assert len(keys) == 1 and isinstance(value["root_token"], str) and value["root_token"]
for path, content in ((unseal, keys[0]), (root, value["root_token"])):
    path.write_text(content + "\n")
    os.chmod(path, 0o600)
PY
unseal_key="$(cat "$secrets_dir/openbao-unseal-key")"
docker compose exec -T openbao bao operator unseal "$unseal_key" >/dev/null 2>&1 || true
unset unseal_key

OPENBAO_ROOT_TOKEN_FILE="$secrets_dir/openbao-root-token" \
    docker compose --profile maintenance run --rm openbao-configure
docker compose up database-initialize kratos-migrate api-migrate
docker compose up -d

setup_token="$secrets_dir/bootstrap/setup-token"
if [ ! -s "$setup_token" ]; then
    printf '%s\n' \
        "{\"installationId\":\"$installation_id\",\"operationId\":\"first-owner\"}" |
        docker compose --profile maintenance run --rm --no-TTY api-bootstrap \
            prepare --stdin --output-file /run/workspaces-bootstrap/setup-token
    if [ ! -s "$setup_token" ]; then
        printf '%s\n' \
            "{\"installationId\":\"$installation_id\",\"operationId\":\"first-owner\"}" |
            docker compose --profile maintenance run --rm --no-TTY api-bootstrap \
                rotate-token --stdin --output-file /run/workspaces-bootstrap/setup-token
    fi
fi
[ -s "$setup_token" ] && [ "$(stat -c %a "$setup_token")" = "600" ] || {
    echo "The one-time first-owner token could not be recovered safely." >&2
    exit 1
}
install -d -m 700 "$(dirname "$setup_token_output")"
if [ -e "$setup_token_output" ]; then
    cmp "$setup_token" "$setup_token_output" || {
        echo "The protected first-owner token output conflicts with the existing value." >&2
        exit 1
    }
else
    cp "$setup_token" "$setup_token_output.pending"
    chmod 600 "$setup_token_output.pending"
    mv "$setup_token_output.pending" "$setup_token_output"
fi

sleep 5
for service in postgres openbao openbao-agent kratos api worker workspace-gateway caddy; do
    container="$(docker compose ps -q "$service")"
    [ -n "$container" ] &&
    [ "$(docker inspect -f '{{.State.Running}}' "$container")" = "true" ] || {
        echo "Required service is not running: $service" >&2
        exit 1
    }
done

printf '%s\n' "Workspaces release $release_tag is installed and running."
