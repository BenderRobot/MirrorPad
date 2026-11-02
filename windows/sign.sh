#!/bin/sh
# Signe des exe Windows avec Azure Trusted Signing (depuis Linux, avec jsign).
# Ne fait rien tant que la signature n'est pas configurée dans GitHub (variables et secrets ci-dessous).
#   Variables : AZURE_SIGNING_ENDPOINT (ex. https://weu.codesigning.azure.net), AZURE_SIGNING_ACCOUNT, AZURE_SIGNING_PROFILE
#   Secrets   : AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET (application Entra avec le rôle
#               « Trusted Signing Certificate Profile Signer » sur le compte de signature)
set -e
if [ -z "$AZURE_SIGNING_ACCOUNT" ] || [ -z "$AZURE_CLIENT_SECRET" ]; then
  echo "signature non configurée : exe non signés"; exit 0
fi
[ -f /tmp/jsign.jar ] || curl -fsSL -o /tmp/jsign.jar https://github.com/ebourg/jsign/releases/download/7.1/jsign-7.1.jar
TOKEN=$(curl -fsS -X POST "https://login.microsoftonline.com/$AZURE_TENANT_ID/oauth2/v2.0/token" \
  -d "client_id=$AZURE_CLIENT_ID" --data-urlencode "client_secret=$AZURE_CLIENT_SECRET" \
  -d "grant_type=client_credentials" -d "scope=https://codesigning.azure.net/.default" | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')
for f in "$@"; do
  java -jar /tmp/jsign.jar --storetype TRUSTEDSIGNING --keystore "$AZURE_SIGNING_ENDPOINT" --storepass "$TOKEN" \
    --alias "$AZURE_SIGNING_ACCOUNT/$AZURE_SIGNING_PROFILE" --tsaurl http://timestamp.acs.microsoft.com --tsmode RFC3161 \
    --name "MirrorPad" "$f"
  echo "signé : $f"
done
