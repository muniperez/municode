path "workspaces/data/env/+/orgs/+/providers/+/credentials/+" {
  capabilities = ["create", "read", "update"]
}

path "workspaces/metadata/env/+/orgs/+/providers/+/credentials/+" {
  capabilities = ["delete"]
}

path "workspaces/data/env/+/orgs/+/integrations/+/credentials/+" {
  capabilities = ["create", "read", "update"]
}

path "workspaces/metadata/env/+/orgs/+/integrations/+/credentials/+" {
  capabilities = ["delete"]
}

path "auth/token/renew-self" {
  capabilities = ["update"]
}

path "auth/token/lookup-self" {
  capabilities = ["read"]
}
