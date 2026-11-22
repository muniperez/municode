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
install_stage="$install_root.seed-pending"
secrets_stage="$secrets_dir.seed-pending"

validate_install() {
    root=$1
    [ -f "$root/.env" ] &&
    [ -f "$root/release-manifest.json" ] &&
    [ -f "$root/runtime/api/Workspaces.Api.dll" ] &&
    [ -f "$root/runtime/worker/Workspaces.Worker.dll" ] &&
    [ -f "$root/runtime/gateway/Workspaces.Gateway.dll" ] &&
    [ -f "$root/dashboard/index.html" ] &&
    python3 -B "$root/verify_bundle.py"
}

validate_secrets() {
    root=$1
    [ -s "$root/postgres-admin-password" ] &&
    [ -s "$root/app-db-password" ] &&
    [ -s "$root/kratos-db-password" ] &&
    [ -s "$root/workspace-gateway-credential" ] &&
    [ -s "$root/kratos.env" ] &&
    [ -s "$root/ssh/worker-key" ] &&
    [ -s "$root/ssh/worker-key.pub" ] &&
    [ -s "$root/openbao-server.crt" ] &&
    [ -s "$root/openbao-server.key" ] &&
    [ -s "$root/openbao-ca.crt" ] &&
    [ -f "$root/openbao-approle/role-id" ] &&
    [ -f "$root/openbao-approle/secret-id" ]
}

write_owner() {
    phase=$1
    install -d -m 700 "$(dirname "$owner_record")"
    printf '%s %s %s\n' "$installation_id" "$release_tag" "$phase" > "$owner_record.pending"
    chmod 600 "$owner_record.pending"
    mv "$owner_record.pending" "$owner_record"
}

ownership=
[ ! -s "$owner_record" ] || ownership="$(cat "$owner_record")"
expected="$installation_id $release_tag"
if [ -f "$marker" ]; then
    { [ "$ownership" = "$expected complete" ] || [ "$ownership" = "$expected promotion" ]; } &&
    validate_install "$install_root" &&
    validate_secrets "$secrets_dir" || {
        echo "Completed seed installation ownership or contents no longer match." >&2
        exit 1
    }
    [ "$ownership" = "$expected complete" ] || write_owner complete
elif [ -n "$ownership" ]; then
    case "$ownership" in
        "$expected staging")
            # These private staging trees were created by this exact operation only.
            rm -rf -- "$install_stage" "$secrets_stage"
            [ ! -e "$install_root" ] && [ ! -e "$secrets_dir" ] || {
                echo "Owned staging state conflicts with unexpected final paths." >&2
                exit 1
            }
            ;;
        "$expected promotion")
            if [ -e "$secrets_stage" ] && [ ! -e "$secrets_dir" ]; then
                validate_secrets "$secrets_stage" || {
                    echo "Owned secrets staging validation failed; no partial final was promoted." >&2
                    exit 1
                }
                mv "$secrets_stage" "$secrets_dir"
            fi
            if [ -e "$install_stage" ] && [ ! -e "$install_root" ]; then
                validate_install "$install_stage" || {
                    echo "Owned install staging validation failed; no partial final was promoted." >&2
                    exit 1
                }
                mv "$install_stage" "$install_root"
            fi
            validate_install "$install_root" && validate_secrets "$secrets_dir" || {
                echo "Owned promotion is incomplete; preserve state for reconciliation." >&2
                exit 1
            }
            : > "$marker.pending"
            chmod 600 "$marker.pending"
            mv "$marker.pending" "$marker"
            write_owner complete
            ;;
        "$expected complete")
            validate_install "$install_root" && validate_secrets "$secrets_dir" &&
            [ ! -e "$install_stage" ] && [ ! -e "$secrets_stage" ] || {
                echo "Completed ownership has not reached a valid final installation." >&2
                exit 1
            }
            : > "$marker.pending"
            chmod 600 "$marker.pending"
            mv "$marker.pending" "$marker"
            ;;
        *) echo "Seed owner record conflicts with the exact installation and release." >&2; exit 1 ;;
    esac
fi

if [ ! -f "$marker" ] && [ -z "$ownership" ]; then
    [ ! -e "$install_root" ] && [ ! -e "$secrets_dir" ] &&
    [ ! -e "$install_stage" ] && [ ! -e "$secrets_stage" ] || {
        echo "Unowned installation paths exist; seed will not adopt or delete them." >&2
        exit 1
    }
    write_owner staging
    ownership="$expected staging"
fi

if [ ! -f "$marker" ] && [ "$ownership" = "$expected staging" ]; then
    "$release_root/install.sh" --install-root "$install_stage" --secrets-dir "$secrets_stage"
    validate_install "$install_stage" && validate_secrets "$secrets_stage" || {
        echo "The exact staged release or secrets are incomplete; owned staging is retained for a safe retry." >&2
        exit 1
    }
    write_owner promotion
    mv "$secrets_stage" "$secrets_dir"
    mv "$install_stage" "$install_root"
    validate_install "$install_root" && validate_secrets "$secrets_dir" || {
        echo "Promoted seed contents changed; no completion marker was written." >&2
        exit 1
    }
    : > "$marker.pending"
    chmod 600 "$marker.pending"
    mv "$marker.pending" "$marker"
    write_owner complete
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
preflight_openbao_ownership() {
    existing_volumes="$(docker volume ls -q \
        --filter label=com.docker.compose.project=municode-workspaces \
        --filter label=com.docker.compose.volume=openbao)"
    if [ -n "$existing_volumes" ]; then
        [ "$(printf '%s\n' "$existing_volumes" | wc -l)" -eq 1 ] || {
            echo "Pre-existing OpenBao volumes are ambiguous; refusing any Compose mutation." >&2
            exit 1
        }
        [ "$(docker volume inspect \
            --format '{{ index .Labels "com.municode.workspaces.installation-id" }}' \
            "$existing_volumes")" = "$installation_id" ] || {
            echo "Pre-existing OpenBao volume ownership changed before startup." >&2
            exit 1
        }
    fi
    existing_containers="$(docker compose ps -aq openbao openbao-agent)"
    for container in $existing_containers; do
        [ "$(docker inspect \
            --format '{{ index .Config.Labels "com.municode.workspaces.installation-id" }}' \
            "$container")" = "$installation_id" ] || {
            echo "Pre-existing OpenBao container ownership changed before startup." >&2
            exit 1
        }
    done
}
preflight_openbao_ownership
docker compose up -d postgres openbao

openbao_init="$secrets_dir/openbao-seed-init.json"
openbao_pending="$openbao_init.pending"
openbao_configured="$secrets_dir/openbao-seed-configured"

valid_openbao_record() {
    python3 -B - "$1" 2>/dev/null <<'PY'
import json
import sys
value = json.load(open(sys.argv[1]))
assert len(value["unseal_keys_b64"]) == 1
assert isinstance(value["root_token"], str) and value["root_token"]
PY
}

reset_unconfigured_openbao() {
    [ ! -e "$openbao_configured" ] || {
        echo "Configured OpenBao recovery material is missing; refusing to reset application data." >&2
        exit 1
    }
    volume="$(docker volume ls -q \
        --filter label=com.docker.compose.project=municode-workspaces \
        --filter label=com.docker.compose.volume=openbao \
        --filter "label=com.municode.workspaces.installation-id=$installation_id")"
    [ -n "$volume" ] && [ "$(printf '%s\n' "$volume" | wc -l)" -eq 1 ] || {
        echo "No unique OpenBao volume is owned by this exact installation; preserve the host." >&2
        exit 1
    }
    [ "$(docker volume inspect \
        --format '{{ index .Labels "com.municode.workspaces.installation-id" }}' "$volume")" \
        = "$installation_id" ] || {
        echo "OpenBao volume ownership changed before reset." >&2
        exit 1
    }
    containers="$(docker compose ps -aq openbao openbao-agent)"
    owned_containers=
    for container in $containers; do
        [ "$(docker inspect \
            --format '{{ index .Config.Labels "com.municode.workspaces.installation-id" }}' \
            "$container")" = "$installation_id" ] || {
            echo "OpenBao container ownership changed before reset." >&2
            exit 1
        }
        owned_containers="$owned_containers $container"
    done
    for container in $owned_containers; do
        docker rm -f "$container"
    done
    docker volume rm "$volume"
    rm -f "$openbao_init" "$openbao_pending" "$secrets_dir/openbao-unseal-key" \
        "$secrets_dir/openbao-root-token"
    docker compose up -d openbao
}

read_openbao_initialized() {
    attempt=0
    while [ "$attempt" -lt 12 ]; do
        attempt=$((attempt + 1))
        result="$(docker compose exec -T openbao \
            sh -c 'bao status -format=json 2>/dev/null || true' |
            python3 -c 'import json,sys
try:
    value=json.load(sys.stdin).get("initialized")
    print("true" if value is True else "false" if value is False else "unknown")
except Exception:
    print("unknown")')"
        case "$result" in
            true|false) printf '%s\n' "$result"; return 0 ;;
        esac
        sleep "${WORKSPACES_SEED_OPENBAO_READY_INTERVAL:-2}"
    done
    return 1
}
initialized="$(read_openbao_initialized)" || {
    echo "OpenBao did not return valid readiness status; no volume or recovery record was changed." >&2
    exit 1
}
if [ ! -s "$openbao_init" ]; then
    if [ -e "$openbao_pending" ]; then
        if valid_openbao_record "$openbao_pending"; then
            [ "$initialized" = "true" ] || {
                rm -f "$openbao_pending"
                initialized=false
            }
            [ ! -e "$openbao_pending" ] || {
                mv "$openbao_pending" "$openbao_init"
            }
        elif [ "$initialized" = "false" ]; then
            rm -f "$openbao_pending"
        else
            echo "OpenBao positively reports initialized but seed recovery output is invalid; preserving data for reconciliation." >&2
            exit 1
        fi
    fi
    if [ ! -s "$openbao_init" ]; then
        [ "$initialized" = "false" ] || {
            echo "OpenBao positively reports initialized but no owned seed recovery record exists; preserving data." >&2
            exit 1
        }
        docker compose exec -T openbao \
            bao operator init -key-shares=1 -key-threshold=1 -format=json > "$openbao_pending"
        chmod 600 "$openbao_pending"
        valid_openbao_record "$openbao_pending" || {
            echo "OpenBao initialization output is incomplete; owned unconfigured state is recoverable on resume." >&2
            exit 1
        }
        mv "$openbao_pending" "$openbao_init"
    fi
elif [ "$initialized" = "false" ]; then
    reset_unconfigured_openbao
    rm -f "$openbao_init"
    docker compose exec -T openbao \
        bao operator init -key-shares=1 -key-threshold=1 -format=json > "$openbao_pending"
    valid_openbao_record "$openbao_pending" || {
        echo "OpenBao initialization output is incomplete; owned unconfigured state is recoverable on resume." >&2
        exit 1
    }
    mv "$openbao_pending" "$openbao_init"
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
[ -s "$secrets_dir/openbao-approle/role-id" ] &&
[ -s "$secrets_dir/openbao-approle/secret-id" ] || {
    echo "OpenBao Workspaces AppRole configuration is incomplete." >&2
    exit 1
}
: > "$openbao_configured.pending"
chmod 600 "$openbao_configured.pending"
mv "$openbao_configured.pending" "$openbao_configured"
for migration in database-initialize kratos-migrate api-migrate; do
    docker compose up --abort-on-container-exit --exit-code-from "$migration" "$migration"
done
docker compose up -d

setup_token="$secrets_dir/bootstrap/setup-token"
token_rotated_this_run=false
valid_setup_token() {
    [ -f "$1" ] &&
    [ "$(stat -c %a "$1")" = "600" ] &&
    [ "$(wc -c < "$1")" -eq 65 ] &&
    grep -Eq '^[0-9a-f]{64}$' "$1"
}
if [ -e "$setup_token" ] && ! valid_setup_token "$setup_token"; then
    # Only this exact seed-owned output may be discarded; a valid one is immutable.
    rm -f "$setup_token"
fi
if ! valid_setup_token "$setup_token"; then
    printf '%s\n' \
        "{\"installationId\":\"$installation_id\",\"operationId\":\"first-owner\"}" |
        docker compose --profile maintenance run --rm --no-TTY api-bootstrap \
            prepare --stdin --output-file /run/workspaces-bootstrap/setup-token
    if [ -e "$setup_token" ] && ! valid_setup_token "$setup_token"; then
        rm -f "$setup_token"
    fi
    if ! valid_setup_token "$setup_token"; then
        printf '%s\n' \
            "{\"installationId\":\"$installation_id\",\"operationId\":\"first-owner\"}" |
            docker compose --profile maintenance run --rm --no-TTY api-bootstrap \
                rotate-token --stdin --output-file /run/workspaces-bootstrap/setup-token
        token_rotated_this_run=true
    fi
fi
valid_setup_token "$setup_token" || {
    echo "The one-time first-owner token could not be recovered safely." >&2
    exit 1
}
bootstrap_token_decision() {
    status="$(printf '%s\n' \
        "{\"installationId\":\"$installation_id\",\"operationId\":\"first-owner\"}" |
        docker compose --profile maintenance run --rm --no-TTY api-bootstrap status --stdin)"
    printf '%s' "$status" | python3 -c '
import datetime
import json
import sys
value = json.load(sys.stdin)
if value.get("state") != "required" or not value.get("expiresAt"):
    print("invalid")
    raise SystemExit
expires = datetime.datetime.fromisoformat(value["expiresAt"].replace("Z", "+00:00"))
minimum = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=5)
print("fresh" if expires > minimum else "rotate")
'
}
token_decision="$(bootstrap_token_decision)" || {
    echo "First-owner setup status is unavailable; preserving the existing token." >&2
    exit 1
}
case "$token_decision" in
    fresh) ;;
    rotate)
        rm -f "$setup_token"
        printf '%s\n' \
            "{\"installationId\":\"$installation_id\",\"operationId\":\"first-owner\"}" |
            docker compose --profile maintenance run --rm --no-TTY api-bootstrap \
                rotate-token --stdin --output-file /run/workspaces-bootstrap/setup-token
        token_rotated_this_run=true
        valid_setup_token "$setup_token" || {
            echo "The expired first-owner token could not be rotated safely." >&2
            exit 1
        }
        ;;
    *) echo "First-owner setup is not in a rotatable required state; preserve and reconcile it." >&2
       exit 1 ;;
esac
install -d -m 700 "$(dirname "$setup_token_output")"
if [ -e "$setup_token_output" ]; then
    if ! valid_setup_token "$setup_token_output"; then
        rm -f "$setup_token_output"
    elif ! cmp -s "$setup_token" "$setup_token_output"; then
        [ "$token_rotated_this_run" = true ] || {
            echo "The protected setup-token output conflicts with the current admission token." >&2
            exit 1
        }
    fi
fi
cp "$setup_token" "$setup_token_output.pending"
chmod 600 "$setup_token_output.pending"
mv "$setup_token_output.pending" "$setup_token_output"

settle_interval=${WORKSPACES_SEED_HEALTH_SETTLE_INTERVAL:-5}
consecutive=0
attempt=0
while [ "$attempt" -lt 6 ] && [ "$consecutive" -lt 3 ]; do
    attempt=$((attempt + 1))
    all_running=true
    for service in postgres openbao openbao-agent kratos api worker workspace-gateway caddy; do
        container="$(docker compose ps -q "$service")"
        if [ -z "$container" ] ||
            [ "$(docker inspect -f '{{.State.Running}}' "$container")" != "true" ]; then
            all_running=false
            break
        fi
    done
    if [ "$all_running" = true ]; then
        consecutive=$((consecutive + 1))
    else
        consecutive=0
    fi
    [ "$consecutive" -eq 3 ] || sleep "$settle_interval"
done
[ "$consecutive" -eq 3 ] || {
    echo "Required API, worker, gateway and dashboard services did not remain running through the settling window." >&2
    exit 1
}

printf '%s\n' "Workspaces release $release_tag is installed and running."
