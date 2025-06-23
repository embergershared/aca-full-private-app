## 1. Quick deployment Option - Public

```pwsh
$RESOURCE_GROUP="rg-aca-quickstart-album-api-01"
$LOCATION="southcentralus"

$ENVIRONMENT="aca-env-quick-album-api"
$API_NAME="aca-app-quick-album-api"

az containerapp up `
  --name $API_NAME `
  --resource-group $RESOURCE_GROUP `
  --location $LOCATION `
  --environment $ENVIRONMENT `
  --source containerapps-albumapi-csharp/src

# Access: https://aca-app-album-api.calmsmoke-9a6a4343.southcentralus.azurecontainerapps.io/albums
# See logs: az containerapp logs show -n aca-app-album-api -g rg-aca-quickstart-album-api
```

## 2. Controlled Full deployment Option - Public

### `az containerapp up` does

- Create the resource group
- Create an Azure Container Registry
- Build the container image and push it to the registry
- Create the Container Apps environment with a Log Analytics workspace
- Create and deploy the container app using the built container image

### So we do these manually, in another Resource Group

```pwsh
$RESOURCE_GROUP="rg-aca-quickstart-album-api-02"
$LOCATION="southcentralus"

$RANDOM_SUFFIX = $(Get-Random -Minimum 100 -Maximum 999)

$ACR_NAME = "acracaqsalbapi$($RANDOM_SUFFIX)"
$LOG_ANALYTICS_WORKSPACE="law-aca-albumapi-$($RANDOM_SUFFIX)"
$STORAGE_ACCOUNT="stacaalbumapi$($RANDOM_SUFFIX)"
$BUILD_IMAGE_NAME = "eb-apps/album-api"
$BUILD_IMAGE_TAG = "original"

$ENVIRONMENT="aca-env-manual-album-api"
$API_NAME="aca-app-manual-album-api"

# Create Resource Group
if (-not $LOCATION) {
    $LOCATION = Read-Host "Enter your Azure region (e.g., eastus, westeurope)"
}
az group create --name $RESOURCE_GROUP --location $LOCATION
```

```pwsh
# Create ACR, build container image and push it to the ACR
az acr create -n $ACR_NAME -g $RESOURCE_GROUP --sku Premium --location $LOCATION --public-network-enabled true
az acr build -t "${BUILD_IMAGE_NAME}:${BUILD_IMAGE_TAG}" -r $ACR_NAME git/containerapps-albumapi-csharp/src

# Create a Storage Account, LAW and an Container App Environment
az storage account create `
    --name $STORAGE_ACCOUNT `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --sku Standard_LRS `
    --kind StorageV2

az monitor log-analytics workspace create `
    --resource-group $RESOURCE_GROUP `
    --workspace-name $LOG_ANALYTICS_WORKSPACE `
    --location $LOCATION

# Get the Log Analytics Client ID and Client Secret
$LOG_ANALYTICS_WORKSPACE_CLIENT_ID=az monitor log-analytics workspace show --resource-group $RESOURCE_GROUP --workspace-name $LOG_ANALYTICS_WORKSPACE --query customerId -o tsv
$LOG_ANALYTICS_WORKSPACE_CLIENT_SECRET=az monitor log-analytics workspace get-shared-keys --resource-group $RESOURCE_GROUP --workspace-name $LOG_ANALYTICS_WORKSPACE --query primarySharedKey -o tsv

# Create the Container App Environment
az containerapp env create `
    --name $ENVIRONMENT `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --logs-workspace-id $LOG_ANALYTICS_WORKSPACE_CLIENT_ID `
    --logs-workspace-key $LOG_ANALYTICS_WORKSPACE_CLIENT_SECRET `
    --enable-workload-profiles true `
    --internal-only false `
    --storage-account $STORAGE_ACCOUNT

# Add the Dedicated D4 workload profile to the environment (required for VNet integration)
az containerapp env workload-profile add `
    --name $ENVIRONMENT `
    --resource-group $RESOURCE_GROUP `
    --workload-profile-name Dedicated-D4 `
    --workload-profile-type D4 `
    --min-nodes 1 `
    --max-nodes 3

# Create a Private Endpoint for Container App Environment
$ACA_ENV_ID=$(az containerapp env show --name $ENVIRONMENT --resource-group $RESOURCE_GROUP --query id -o tsv)

az network private-endpoint create `
    --name "$($ENVIRONMENT)-pe" `
    --resource-group $RESOURCE_GROUP `
    --vnet-name $VNET_NAME `
    --subnet $PE_SUBNET_NAME `
    --private-connection-resource-id $ACA_ENV_ID `
    --group-id managedEnvironments `
    --nic-name "$($ENVIRONMENT)-pe-nic" `
    --connection-name "conn-aca-env"

# Create DNS records for the Container App Environment private endpoint
$ACA_PE_NIC_ID=$(az network private-endpoint show --name "$($ENVIRONMENT)-pe" --resource-group $RESOURCE_GROUP --query 'networkInterfaces[0].id' -o tsv)
$ACA_ENV_PRIVATE_IP=$(az network nic show --ids $ACA_PE_NIC_ID --query "ipConfigurations[0].privateIPAddress" -o tsv)

# The Container App Environment DNS record needs to be a wildcard to support all apps
az network private-dns record-set a create --name "*" --zone-name "privatelink.azurecontainerapps.io" --resource-group $RESOURCE_GROUP
az network private-dns record-set a add-record --record-set-name "*" --zone-name "privatelink.azurecontainerapps.io" --resource-group $RESOURCE_GROUP --ipv4-address $ACA_ENV_PRIVATE_IP


# Get the ACR login server
$ACR_LOGIN_SERVER=$(az acr show --name $ACR_NAME --resource-group $RESOURCE_GROUP --query loginServer -o tsv)

# Create and deploy the Container App WebAPI
az containerapp create `
    --name $API_NAME `
    --resource-group $RESOURCE_GROUP `
    --environment $ENVIRONMENT `
    --image "${ACR_LOGIN_SERVER}/${BUILD_IMAGE_NAME}:${BUILD_IMAGE_TAG}" `
    --registry-identity system `
    --target-port 8080 `
    --ingress internal `
    --workload-profile-name Dedicated-D4 `
    --min-replicas 1 `
    --max-replicas 3 `
    --registry-server $ACR_LOGIN_SERVER `
    --query properties.configuration.ingress.fqdn
```
