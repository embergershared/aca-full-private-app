# Azure Container App - Sample app Private

## Links

Application is a Basic C# .NET core WebAPI.
It's source code is here: [Build and deploy from local source code to Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/quickstart-code-to-cloud?tabs=bash%2Ccsharp)

## Overview

The aim is to deploy it fully private, with no public access, and to use a VNet to connect to the Azure Container App.

The deployment is done in steps:

1. Quick deployment, Public access, using `az containerapp up`
2. Controlled full deployment, Public access, using `az cli` for all components
3. Controlled full deployment, Private access, using `az cli` for all components

## Common pre-requisite steps

```pwsh
az login
az upgrade
az extension add --name containerapp --upgrade --allow-preview true
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights

$LOCATION="southcentralus"
```

## 1. Quick deployment Option - Public

```pwsh
$RESOURCE_GROUP="rg-aca-quickstart-album-api-01"

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

$RANDOM_SUFFIX = $(Get-Random -Minimum 100 -Maximum 999)

$ACR_NAME = "acracaqsalbapi$($RANDOM_SUFFIX)"
$LOG_ANALYTICS_WORKSPACE="law-aca-albumapi-$($RANDOM_SUFFIX)"
$STORAGE_ACCOUNT="stacaalbumapi$($RANDOM_SUFFIX)"
$BUILD_IMAGE_NAME = "eb-apps/album-api"
$BUILD_IMAGE_TAG = "original"

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

## 3. Controlled Full deployment Option - Private

Now we deploy, adding VNet integration and private access on all the resources.
It will lead to deploy and use a jumpbox in the VNet to complete the deployment.

```pwsh
$RESOURCE_GROUP="rg-aca-quickstart-album-api-03"

$RANDOM_SUFFIX = $(Get-Random -Minimum 100 -Maximum 999)

$VNET_NAME = "vnet-aca-albumapi-$($RANDOM_SUFFIX)"
$WKLD_SUBNET_NAME = "wkld-snet"
$ACA_ENV_SUBNET_NAME = "aca-env-snet"
$PE_SUBNET_NAME = "pe-snet"

$ACR_NAME = "acracaalbumapi$($RANDOM_SUFFIX)"
$LOG_ANALYTICS_WORKSPACE="law-aca-albumapi-$($RANDOM_SUFFIX)"
$STORAGE_ACCOUNT="stacaalbumapi$($RANDOM_SUFFIX)"

$BUILD_IMAGE_NAME = "eb-apps/album-api"
$BUILD_IMAGE_TAG = "original"

$ENVIRONMENT="aca-env-private-album-api"
$API_NAME="aca-app-private-album-api"

# Create Resource Group
az group create --name $RESOURCE_GROUP --location $LOCATION


# Create a Virtual Network and Subnets
# Ref: To use a VNet with Container Apps, the VNet must have a dedicated subnet with a CIDR range of /23 or larger when using the Consumption only environemnt, or a CIDR range of /27 or larger when using the workload profiles environment (https://learn.microsoft.com/en-us/azure/container-apps/vnet-custom?tabs=bash&pivots=azure-portal#create-a-virtual-network)

az network vnet create `
  --address-prefixes 13.0.0.0/23 `
  --resource-group $RESOURCE_GROUP `
  --location $LOCATION `
  --name $VNET_NAME

az network vnet subnet create `
  --address-prefixes 13.0.0.0/27 `
  --name $WKLD_SUBNET_NAME `
  --resource-group $RESOURCE_GROUP `
  --vnet-name $VNET_NAME

az network vnet subnet create `
  --address-prefixes 13.0.0.32/27 `
  --delegations Microsoft.App/environments `
  --name $ACA_ENV_SUBNET_NAME `
  --resource-group $RESOURCE_GROUP `
  --vnet-name $VNET_NAME

az network vnet subnet create `
  --address-prefixes 13.0.0.64/27 `
  --name $PE_SUBNET_NAME `
  --resource-group $RESOURCE_GROUP `
  --vnet-name $VNET_NAME


# Create and link the required Private DNS Zones
$privateDnsZones = @(
    "privatelink.azurecr.io",                # For Azure Container Registry
    "privatelink.blob.core.windows.net",     # For Azure Storage Account
    "privatelink.monitor.azure.com",         # For Log Analytics Workspace
    "privatelink.azurecontainerapps.io"      # For Azure Container Apps
)

foreach ($zone in $privateDnsZones) {
    az network private-dns zone create `
        --resource-group $RESOURCE_GROUP `
        --name $zone
    
    az network private-dns link vnet create `
        --resource-group $RESOURCE_GROUP `
        --zone-name $zone `
        --name "vnet-link" `
        --virtual-network $VNET_NAME `
        --registration-enabled false
}


# Create a Windows Jumpbox VM in the VNet with a Public IP
$JUMPBOX_NAME="vm-win-albumapi-$($RANDOM_SUFFIX)"
$JUMPBOX_ADMIN_USERNAME="acaadmin"
$JUMPBOX_ADMIN_PASSWORD="P@ssw0rd123!" # Use a strong password
$JUMPBOX_PIP_NAME="$($JUMPBOX_NAME)-pip"

az network public-ip create `
    --name $JUMPBOX_PIP_NAME `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION

az vm create `
    --resource-group $RESOURCE_GROUP `
    --name $JUMPBOX_NAME `
    --computer-name "win-vm" `
    --image "microsoftwindowsdesktop:windows-11:win11-24h2-pro:latest" `
    --public-ip-address $JUMPBOX_PIP_NAME `
    --size "Standard_D8s_v6" `
    --admin-username $JUMPBOX_ADMIN_USERNAME `
    --admin-password $JUMPBOX_ADMIN_PASSWORD `
    --vnet-name $VNET_NAME `
    --subnet $WKLD_SUBNET_NAME `
    --nsg-rule NONE


# Create ACR, build container image and push it to the ACR
az acr create -n $ACR_NAME -g $RESOURCE_GROUP --sku Premium --location $LOCATION --public-network-enabled true

# Create a Private Endpoint for the ACR
$ACR_ID=$(az acr show --name $ACR_NAME --resource-group $RESOURCE_GROUP --query id -o tsv)
$PE_NAME="pe-acr-albumapi-$($RANDOM_SUFFIX)"

az network private-endpoint create `
    --name $PE_NAME `
    --resource-group $RESOURCE_GROUP `
    --vnet-name $VNET_NAME `
    --subnet $SUBNET_NAME `
    --private-connection-resource-id $ACR_ID `
    --group-id registry `
    --connection-name "conn-acr-$($RANDOM_SUFFIX)"

# Create Private DNS Zone for ACR
$DNS_ZONE_NAME="privatelink.azurecr.io"
$DNS_LINK_NAME="link-dns-acr-$($RANDOM_SUFFIX)"

az network private-dns zone create `
    --resource-group $RESOURCE_GROUP `
    --name $DNS_ZONE_NAME

az network private-dns link vnet create `
    --resource-group $RESOURCE_GROUP `
    --zone-name $DNS_ZONE_NAME `
    --name $DNS_LINK_NAME `
    --virtual-network $VNET_NAME `
    --registration-enabled false

# Create DNS record for the ACR's private endpoint
$PE_NIC_ID=$(az network private-endpoint show --name $PE_NAME --resource-group $RESOURCE_GROUP --query 'networkInterfaces[0].id' -o tsv)
$PE_IP=$(az network nic show --ids $PE_NIC_ID --query 'ipConfigurations[0].privateIpAddress' -o tsv)
$ACR_HOST=$(az acr show --name $ACR_NAME --resource-group $RESOURCE_GROUP --query loginServer -o tsv | cut -d'.' -f1)

az network private-dns record-set a add-record `
    --resource-group $RESOURCE_GROUP `
    --zone-name $DNS_ZONE_NAME `
    --record-set-name $ACR_HOST `
    --ipv4-address $PE_IP


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
    --internal-only true `
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


