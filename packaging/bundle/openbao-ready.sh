#!/bin/sh
set -eu

TOKEN_FILE=${OPENBAO_TOKEN_FILE:-/run/municode-openbao/token}
[ -r "$TOKEN_FILE" ] && [ -s "$TOKEN_FILE" ] || exit 1
BAO_ADDR=${BAO_ADDR:-https://openbao:8200}
BAO_CACERT=${BAO_CACERT:-/run/secrets/openbao-ca.crt}
BAO_TOKEN="$(cat "$TOKEN_FILE")"
[ -n "$BAO_TOKEN" ] || exit 1
export BAO_ADDR BAO_CACERT BAO_TOKEN
bao token lookup -format=json >/dev/null 2>&1
