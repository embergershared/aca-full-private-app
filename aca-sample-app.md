# ACA Sample app

## Links


Application is a Basic C# .NET core WebAPI, which code is here: [Build and deploy from local source code to Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/quickstart-code-to-cloud?tabs=bash%2Ccsharp)


## Starter steps

```pwsh
az login
az upgrade
az extension add --name containerapp --upgrade --allow-preview true
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
```

## Quick deployment Option - Public

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

## Controlled Full deployment Option - Public

### az containerapp up does:
- Create the resource group
- Create an Azure Container Registry
- Build the container image and push it to the registry
- Create the Container Apps environment with a Log Analytics workspace
- Create and deploy the container app using the built container image

## So we do these manually, Public access first:

```pwsh
$RESOURCE_GROUP="rg-aca-quickstart-album-api-02"
$RANDOM_SUFFIX = $(Get-Random -Minimum 100 -Maximum 999)

$ACR_NAME = "acracaqsalbapi$($RANDOM_SUFFIX)"
$LOG_ANALYTICS_WORKSPACE="law-aca-albumapi-$($RANDOM_SUFFIX)"
$STORAGE_ACCOUNT="stacaalbumapi$($RANDOM_SUFFIX)"
$BUILD_IMAGE_NAME = "eb-apps/album-api"
$BUILD_IMAGE_TAG = "emm"

$ENVIRONMENT="aca-env-manual-album-api"
$API_NAME="aca-app-manual-album-api"

# Create Resource Group
az group create --name $RESOURCE_GROUP --location $LOCATION

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

# Get the ACR login server
$ACR_LOGIN_SERVER=$(az acr show --name $ACR_NAME --resource-group $RESOURCE_GROUP --query loginServer -o tsv)

# Create and deploy the Container App WebAPI
az containerapp create `
    --name $API_NAME `
    --resource-group $RESOURCE_GROUP `
    --environment $ENVIRONMENT `
    --image "$ACR_LOGIN_SERVER/${BUILD_IMAGE_NAME}:${BUILD_IMAGE_TAG}" `
    --target-port 8080 `
    --ingress external `
    --workload-profile-name Dedicated-D4 `
    --min-replicas 1 `
    --max-replicas 3 `
    --registry-server $ACR_LOGIN_SERVER `
    --query properties.configuration.ingress.fqdn
```



