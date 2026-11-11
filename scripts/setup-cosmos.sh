#!/usr/bin/env bash
# ==============================================================================
# One-Time Master Azure Cosmos DB Setup (Free Forever Tier: 1,000 RU/s + 25 GB)
# Creates the multi-tenant database (`SmallBusinessPlatform`) and 3 containers:
#   1. `clients` (/businessId) — Client tracking, setup details & site config
#   2. `leads`   (/businessId) — Customer inquiries from POST /api/leads
#   3. `billing` (/businessId) — Service charges, custom work & invoicing
# ==============================================================================
set -euo pipefail

RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-rg-smallbiz-platform}"
LOCATION="${AZURE_LOCATION:-eastus}"
COSMOS_ACCOUNT="${COSMOS_ACCOUNT_NAME:-cosmos-smallbiz-agency}"
DATABASE_NAME="${COSMOS_DATABASE_NAME:-SmallBusinessPlatform}"

echo "==> 1. Ensuring Resource Group '$RESOURCE_GROUP' exists in '$LOCATION'..."
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none

echo "==> 1b. Ensuring 'Microsoft.DocumentDB' resource provider is registered on subscription..."
az provider register --namespace Microsoft.DocumentDB --wait

echo "==> 2. Creating Azure Cosmos DB Account '$COSMOS_ACCOUNT' (Free Tier Enabled)..."
az cosmosdb create \
  --name "$COSMOS_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --locations regionName="$LOCATION" failoverPriority=0 isZoneRedundant=false \
  --default-consistency-level "Session" \
  --enable-free-tier true \
  --output none

echo "==> 3. Creating Shared-Throughput Database '$DATABASE_NAME' (1,000 RU/s Free Tier)..."
az cosmosdb sql database create \
  --account-name "$COSMOS_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DATABASE_NAME" \
  --throughput 1000 \
  --output none

for CONTAINER in "clients" "leads" "billing"; do
  echo "==> 4. Creating Container '$CONTAINER' (Partition Key: /businessId)..."
  az cosmosdb sql container create \
    --account-name "$COSMOS_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" \
    --database-name "$DATABASE_NAME" \
    --name "$CONTAINER" \
    --partition-key-path "/businessId" \
    --output none
done

CONN_STR=$(az cosmosdb keys list \
  --name "$COSMOS_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --type connection-strings \
  --query "connectionStrings[0].connectionString" -o tsv)

echo ""
echo "=============================================================================="
echo "Azure Cosmos DB Free Tier Provisioned Successfully!"
echo "Database:   $DATABASE_NAME"
echo "Containers: clients, leads, billing (partitioned by /businessId)"
echo ""
echo "Save this in your .env.agency and Azure Static Web App Environment Variables:"
echo "COSMOS_CONNECTION_STRING=\"$CONN_STR\""
echo "=============================================================================="
