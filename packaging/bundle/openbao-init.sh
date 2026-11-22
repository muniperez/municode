#!/bin/sh
set -eu
umask 077

BAO_ADDR=${BAO_ADDR:-https://openbao:8200}
BAO_CACERT=${BAO_CACERT:-/run/secrets/openbao-ca.crt}
ROOT_TOKEN_FILE=${OPENBAO_ROOT_TOKEN_FILE:?set OPENBAO_ROOT_TOKEN_FILE}
POLICY_FILE=${OPENBAO_POLICY_FILE:?set OPENBAO_POLICY_FILE}
ROLE_ID_FILE=${OPENBAO_ROLE_ID_FILE:?set OPENBAO_ROLE_ID_FILE}
SECRET_ID_FILE=${OPENBAO_SECRET_ID_FILE:?set OPENBAO_SECRET_ID_FILE}

[ -r "$ROOT_TOKEN_FILE" ] || {
    echo "The protected OpenBao root token is unavailable." >&2
    exit 1
}
BAO_TOKEN="$(cat "$ROOT_TOKEN_FILE")"
[ -n "$BAO_TOKEN" ] || {
    echo "The protected OpenBao root token is empty." >&2
    exit 1
}
export BAO_ADDR BAO_CACERT BAO_TOKEN
bao status >/dev/null

mounts="$(bao secrets list -format=json)"
if printf '%s' "$mounts" | grep -Fq '"workspaces/"'; then
    info="$(bao read -format=json sys/mounts/workspaces)"
    printf '%s' "$info" | grep -Eq '"type"[[:space:]]*:[[:space:]]*"kv"' || exit 1
    printf '%s' "$info" | grep -Eq '"version"[[:space:]]*:[[:space:]]*"2"' || exit 1
else
    bao secrets enable -path=workspaces -version=2 kv >/dev/null
fi

auth_mounts="$(bao auth list -format=json)"
if ! printf '%s' "$auth_mounts" | grep -Fq '"approle/"'; then
    bao auth enable approle >/dev/null
fi

bao policy write workspaces "$POLICY_FILE" >/dev/null
bao write auth/approle/role/workspaces \
    token_policies=workspaces token_type=service token_period=24h \
    secret_id_ttl=0 secret_id_num_uses=0 >/dev/null

role_id="$(bao read -field=role_id auth/approle/role/workspaces/role-id)"
if [ -s "$ROLE_ID_FILE" ]; then
    [ "$(cat "$ROLE_ID_FILE")" = "$role_id" ] || {
        echo "The existing OpenBao role ID does not match." >&2
        exit 1
    }
else
    printf '%s\n' "$role_id" > "$ROLE_ID_FILE"
fi
if [ ! -s "$SECRET_ID_FILE" ]; then
    bao write -field=secret_id -f auth/approle/role/workspaces/secret-id > "$SECRET_ID_FILE"
fi
chmod 600 "$ROLE_ID_FILE" "$SECRET_ID_FILE"
unset BAO_TOKEN role_id
echo "OpenBao Workspaces policy, KV v2 mount, and AppRole are ready."
