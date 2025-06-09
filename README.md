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
```

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
$RESOURCE_GROUP="rg-aca-quickstart-album-api-04"
$LOCATION="southcentralus"

$RANDOM_SUFFIX = $(Get-Random -Minimum 100 -Maximum 999)

$VNET_NAME = "vnet-aca-albumapi-$($RANDOM_SUFFIX)"
$WKLD_SUBNET_NAME = "wkld-snet"
$ACA_ENV_SUBNET_NAME = "aca-env-snet"
$PE_SUBNET_NAME = "pe-snet"
$BASTION_NAME = "bastion-aca-albumapi-$($RANDOM_SUFFIX)"
$BASTION_PIP_NAME = "$($BASTION_NAME)-pip"

$ACR_NAME = "acracaalbumapi$($RANDOM_SUFFIX)"
$LOG_ANALYTICS_WORKSPACE="law-aca-albumapi-$($RANDOM_SUFFIX)"
$STORAGE_ACCOUNT="stacaalbumapi$($RANDOM_SUFFIX)"
$KV_NAME = "kv-aca-albumapi-$($RANDOM_SUFFIX)"

$BUILD_IMAGE_NAME = "eb-apps/album-api"
$BUILD_IMAGE_TAG = "original"

$ENVIRONMENT="aca-env-private-album-api"
$API_NAME="aca-app-private-album-api"

# Create Resource Group
az group create --name $RESOURCE_GROUP --location $LOCATION


# Create a Virtual Network and Subnets
# Ref: To use a VNet with Container Apps, the VNet must have a dedicated subnet with a CIDR range of /23 or larger when using the Consumption only environemnt, or a CIDR range of /27 or larger when using the workload profiles environment (https://learn.microsoft.com/en-us/azure/container-apps/vnet-custom?tabs=bash&pivots=azure-portal#create-a-virtual-network)
az network vnet create `
  --address-prefixes 192.168.10.0/23 `
  --resource-group $RESOURCE_GROUP `
  --location $LOCATION `
  --name $VNET_NAME

az network vnet subnet create `
  --address-prefixes 192.168.10.0/27 `
  --name $WKLD_SUBNET_NAME `
  --resource-group $RESOURCE_GROUP `
  --vnet-name $VNET_NAME

az network vnet subnet create `
  --address-prefixes 192.168.10.32/27 `
  --delegations Microsoft.App/environments `
  --name $ACA_ENV_SUBNET_NAME `
  --resource-group $RESOURCE_GROUP `
  --vnet-name $VNET_NAME

az network vnet subnet create `
  --address-prefixes 192.168.10.64/27 `
  --name $PE_SUBNET_NAME `
  --resource-group $RESOURCE_GROUP `
  --vnet-name $VNET_NAME `
  --private-endpoint-network-policies Disabled

az network vnet subnet create `
  --address-prefixes 192.168.10.128/27 `
  --name AzureBastionSubnet `
  --resource-group $RESOURCE_GROUP `
  --vnet-name $VNET_NAME


# Create Azure Bastion
az network public-ip create `
  --name $BASTION_PIP_NAME `
  --resource-group $RESOURCE_GROUP `
  --location $LOCATION `
  --sku Standard

az network bastion create `
  --name $BASTION_NAME `
  --resource-group $RESOURCE_GROUP `
  --location $LOCATION `
  --public-ip-address $BASTION_PIP_NAME `
  --vnet-name $VNET_NAME `
  --sku Basic


# Create and link the required Private DNS Zones on the VNet
$privateDnsZones = @(
    "privatelink.vaultcore.azure.net",       # For Key Vault
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


# Create an Azure Key Vault to store the VM Password
$MY_PUBLIC_IP = $((Invoke-WebRequest ifconfig.me/ip).Content.Trim())
az keyvault create `
  --name $KV_NAME `
  --resource-group $RESOURCE_GROUP `
  --location $LOCATION `
  --enabled-for-deployment false `
  --enabled-for-template-deployment false `
  --enabled-for-disk-encryption false `
  --bypass 'AzureServices' `
  --network-acls-ips $MY_PUBLIC_IP/32 `
  --default-action 'Deny' `
  --public-network-access Enabled

$KV_ID = $(az keyvault show --name $KV_NAME --resource-group $RESOURCE_GROUP --query id -o tsv)


# Create a Windows Jumpbox VM in the VNet with a Public IP
$JUMPBOX_NAME="vm-win-albumapi-$($RANDOM_SUFFIX)"
$JUMPBOX_ADMIN_USERNAME="acaadmin"
$JUMPBOX_ADMIN_PASSWORD_KV_SECRET_NAME="$($JUMPBOX_NAME)-admin-password"

## Generate admin password and store it in Key vault
function GeneratePassword {
  param(
    [ValidateRange(12, 256)]
    [int] 
    $length = 14
  )

  $symbols = '@#%&*'.ToCharArray()
  $characterList = 'a'..'z' + 'A'..'Z' + '0'..'9' + $symbols
  do {
    $password = -join (0..$length | ForEach-Object { $characterList | Get-Random })
    [int]$hasLowerChar = $password -cmatch '[a-z]'
    [int]$hasUpperChar = $password -cmatch '[A-Z]'
    [int]$hasDigit = $password -match '[0-9]'
    [int]$hasSymbol = $password.IndexOfAny($symbols) -ne -1
  }
  until (($hasLowerChar + $hasUpperChar + $hasDigit + $hasSymbol) -ge 4)

  return $password #| ConvertTo-SecureString -AsPlainText
}

$JUMPBOX_ADMIN_PASSWORD = GeneratePassword 18
az keyvault secret set `
  --vault-name $KV_NAME `
  --name $JUMPBOX_ADMIN_PASSWORD_KV_SECRET_NAME `
  --value $JUMPBOX_ADMIN_PASSWORD


## (Optional) Create a Public IP to access the VM
# $JUMPBOX_PIP_NAME="$($JUMPBOX_NAME)-pip"

# az network public-ip create `
#     --name $JUMPBOX_PIP_NAME `
#     --resource-group $RESOURCE_GROUP `
#     --location $LOCATION

## Create a Windows 11 VM
az vm create `
    --resource-group $RESOURCE_GROUP `
    --name $JUMPBOX_NAME `
    --computer-name "win-vm" `
    --image "microsoftwindowsdesktop:windows-11:win11-24h2-pro:latest" `
    --public-ip-address '""' `
    --size "Standard_D8s_v6" `
    --admin-username $JUMPBOX_ADMIN_USERNAME `
    --admin-password $JUMPBOX_ADMIN_PASSWORD `
    --vnet-name $VNET_NAME `
    --subnet $WKLD_SUBNET_NAME `
    --nsg-rule NONE
# --public-ip-address $JUMPBOX_PIP_NAME `


####################   LOG IN TO THE JUMPBOX VM   ####################

# Update Key Vault to be completely private
# 1. Disable public network access
az keyvault update `
    --name $KV_NAME `
    --resource-group $RESOURCE_GROUP `
    --public-network-access Disabled

# 2. Get the Key Vault resource ID
$KV_ID=$(az keyvault show --name $KV_NAME --resource-group $RESOURCE_GROUP --query id -o tsv)

# 3. Create a private endpoint for Key Vault
az network private-endpoint create `
    --name "$($KV_NAME)-pe" `
    --resource-group $RESOURCE_GROUP `
    --vnet-name $VNET_NAME `
    --subnet $PE_SUBNET_NAME `
    --private-connection-resource-id $KV_ID `
    --group-id vault `
    --nic-name "$($KV_NAME)-pe-nic" `
    --connection-name "conn-kv"

# 4. Create DNS records for the Key Vault private endpoint
# Gather the data
$KV_PE_NIC_ID=$(az network private-endpoint show --name "$($KV_NAME)-pe" --resource-group $RESOURCE_GROUP --query 'networkInterfaces[0].id' -o tsv)
$KV_PRIVATE_IP=$(az network nic show --ids $KV_PE_NIC_ID --query "ipConfigurations[0].privateIPAddress" --output tsv)
$KV_FQDN=$(az keyvault show --name $KV_NAME --resource-group $RESOURCE_GROUP --query properties.vaultUri -o tsv | sed 's/https:\/\///' | sed 's/\///')

# Create the private DNS record
az network private-dns record-set a create --name $KV_NAME --zone-name "privatelink.vaultcore.azure.net" --resource-group $RESOURCE_GROUP
az network private-dns record-set a add-record --record-set-name $KV_NAME --zone-name "privatelink.vaultcore.azure.net" --resource-group $RESOURCE_GROUP --ipv4-address $KV_PRIVATE_IP


# Create a Private ACR
az acr create `
    --name $ACR_NAME `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --sku Premium `
    --public-network-enabled false

$ACR_ID=$(az acr show --name $ACR_NAME --resource-group $RESOURCE_GROUP --query id -o tsv)

# Create a Private Endpoint for the ACR
az network private-endpoint create `
    --name "$($ACR_NAME)-pe" `
    --resource-group $RESOURCE_GROUP `
    --vnet-name $VNET_NAME `
    --subnet $PE_SUBNET_NAME `
    --private-connection-resource-id $ACR_ID `
    --group-id registry `
    --nic-name "$($ACR_NAME)-pe-nic" `
    --connection-name "conn-acr"

# Create DNS record for the ACR's private endpoint
## Gather the data
$PE_NIC_ID=$(az network private-endpoint show --name "$($ACR_NAME)-pe" --resource-group $RESOURCE_GROUP --query 'networkInterfaces[0].id' -o tsv)
$REGISTRY_PRIVATE_IP=$(az network nic show --ids $PE_NIC_ID --query "ipConfigurations[?privateLinkConnectionProperties.requiredMemberName=='registry'].privateIPAddress" --output tsv)
$DATA_ENDPOINT_PRIVATE_IP=$(az network nic show --ids $PE_NIC_ID --query "ipConfigurations[?privateLinkConnectionProperties.requiredMemberName=='registry_data_$LOCATION'].privateIPAddress" --output tsv)
$REGISTRY_FQDN=$(az network nic show --ids $PE_NIC_ID --query "ipConfigurations[?privateLinkConnectionProperties.requiredMemberName=='registry'].privateLinkConnectionProperties.fqdns" --output tsv)
$DATA_ENDPOINT_FQDN=$(az network nic show --ids $PE_NIC_ID --query "ipConfigurations[?privateLinkConnectionProperties.requiredMemberName=='registry_data_$LOCATION'].privateLinkConnectionProperties.fqdns" --output tsv)
$ACR_HOST=$(az acr show --name $ACR_NAME --resource-group $RESOURCE_GROUP --query loginServer -o tsv)

## Create the Private DNS records
az network private-dns record-set a create --name $ACR_NAME --zone-name "privatelink.azurecr.io" --resource-group $RESOURCE_GROUP
az network private-dns record-set a create --name "$($ACR_NAME).$($LOCATION).data" --zone-name "privatelink.azurecr.io" --resource-group $RESOURCE_GROUP
az network private-dns record-set a add-record --record-set-name $ACR_NAME --zone-name "privatelink.azurecr.io" --resource-group $RESOURCE_GROUP --ipv4-address $REGISTRY_PRIVATE_IP
az network private-dns record-set a add-record --record-set-name "$($ACR_NAME).$($LOCATION).data" --zone-name "privatelink.azurecr.io" --resource-group $RESOURCE_GROUP --ipv4-address $DATA_ENDPOINT_PRIVATE_IP

# From Private VM, Build the container image and push it to the ACR
az acr build -t "${BUILD_IMAGE_NAME}:${BUILD_IMAGE_TAG}" -r $ACR_NAME containerapps-albumapi-csharp/src


# Create a Storage Account, LAW and an Container App Environment
az storage account create `
    --name $STORAGE_ACCOUNT `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --sku Standard_LRS `
    --kind StorageV2

az monitor log-analytics workspace create `
    --workspace-name $LOG_ANALYTICS_WORKSPACE `
    --resource-group $RESOURCE_GROUP `
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


