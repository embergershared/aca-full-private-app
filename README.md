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
if (-not $LOCATION) {
    $LOCATION = Read-Host "Enter your Azure region (e.g., eastus, westeurope)"
}
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

## 3. Controlled Full deployment Option - Private

Now we deploy, adding VNet integration and private access on all the resources.
It will lead to deploy and use a jumpbox in the VNet to complete the deployment.

```pwsh

#----FUNCTIONS----
## Generate admin password and store it in Key vault
function GeneratePassword {
    param(
        [ValidateRange(12, 256)]
        [int]$length = 14
    )

     $symbols = '@-_*()^$#!{}[]|\~'.ToCharArray()
    $characterList = @(97..122 + 65..90 + 48..57 | ForEach-Object { [char]$_ }) + $symbols

    do {
        $password = -join (1..$length | ForEach-Object { $characterList | Get-Random })
        $hasLowerChar = $password -cmatch '[a-z]'
        $hasUpperChar = $password -cmatch '[A-Z]'
        $hasDigit     = $password -match '[0-9]'
        $hasSymbol    = $password.IndexOfAny($symbols) -ne -1
    }
    until ($hasLowerChar -and $hasUpperChar -and $hasDigit -and $hasSymbol)

    return $password
}
#--END FUNCTIONS----

#----BEGIN VARIABLES----
$RANDOM_SUFFIX = $(Get-Random -Minimum 100 -Maximum 999)

$RESOURCE_GROUP="rg-aca-quickstart-album-api-$($RANDOM_SUFFIX)"
$LOCATION="eastus2"

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

$JUMPBOX_NAME="vm-win-albumapi-$($RANDOM_SUFFIX)"
$JUMPBOX_ADMIN_USERNAME="acaadmin"
$JUMPBOX_ADMIN_USERNAME_KV_SECRET_NAME = "$($JUMPBOX_NAME)-admin-username"
$JUMPBOX_ADMIN_PASSWORD_KV_SECRET_NAME="$($JUMPBOX_NAME)-admin-password"

# VM and image creation variables
$BUILD_AGENT_VM_NAME = "vm-ubuntu-ado-agent-$($RANDOM_SUFFIX)"
$BUILD_AGENT_VM_ADMIN_USERNAME = "adoagentadmin"
$BUILD_AGENT_VM_ADMIN_PASSWORD = GeneratePassword 18
$BUILD_AGENT_ADMIN_USERNAME_KV_SECRET_NAME = "$($BUILD_AGENT_VM_NAME)-admin-username"
$BUILD_AGENT_ADMIN_PASSWORD_KV_SECRET_NAME="$($BUILD_AGENT_VM_NAME)-admin-password"
$BUILD_AGENT_MANAGED_IMAGE_NAME = "mi-ubuntu-ado-agent"
$BUILD_AGENT_IMAGE_VERSION_NAME = "1.0.0"

# Container App build variables
$BUILD_IMAGE_NAME = "eb-apps/album-api"
$BUILD_IMAGE_TAG = "original"

$ENVIRONMENT="aca-env-private-album-api"
$API_NAME="aca-app-private-album-api"

#----END VARIABLES----

# Create Resource Group
if (-not $LOCATION) {
    $LOCATION = Read-Host "Enter your Azure region (e.g., eastus, westeurope)"
}

#Login to Azure
az login
# Select the subscription to use
az account set --subscription "Your Subscription Name or ID"

# Create the Resource Group
if (az group exists --name $RESOURCE_GROUP) {
    Write-Host "Resource Group $RESOURCE_GROUP already exists. Skipping creation."
} else {
    Write-Host "Creating Resource Group $RESOURCE_GROUP in $LOCATION..."
}
az group create --name $RESOURCE_GROUP --location $LOCATION

# Create a Virtual Network and Subnets
# Ref: To use a VNet with Container Apps, the VNet must have a dedicated subnet with a CIDR range of /23 or larger when using the Consumption only environemnt, or a CIDR range of /27 or larger when using the workload profiles environment (https://learn.microsoft.com/en-us/azure/container-apps/vnet-custom?tabs=bash&pivots=azure-portal#create-a-virtual-network)
if (az network vnet exists --name $VNET_NAME --resource-group $RESOURCE_GROUP) {
    Write-Host "Virtual Network $VNET_NAME already exists in $RESOURCE_GROUP. Skipping creation."
} else {
    Write-Host "Creating Virtual Network $VNET_NAME in $RESOURCE_GROUP..."
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
}

# Create Azure Bastion PIP
if (az network public-ip exists --name $BASTION_PIP_NAME --resource-group $RESOURCE_GROUP) {
    Write-Host "Public IP $BASTION_PIP_NAME already exists in $RESOURCE_GROUP. Skipping creation."
} else {
    Write-Host "Creating Public IP $BASTION_PIP_NAME in $RESOURCE_GROUP..."
}
az network public-ip create `
  --name $BASTION_PIP_NAME `
  --resource-group $RESOURCE_GROUP `
  --location $LOCATION `
  --sku Standard

# Create Azure Bastion Host
if (az network bastion exists --name $BASTION_NAME --resource-group $RESOURCE_GROUP) {
    Write-Host "Bastion Host $BASTION_NAME already exists in $RESOURCE_GROUP. Skipping creation."
} else {
    Write-Host "Creating Bastion Host $BASTION_NAME in $RESOURCE_GROUP..."
}
az network bastion create `
  --name $BASTION_NAME `
  --resource-group $RESOURCE_GROUP `
  --location $LOCATION `
  --public-ip-address $BASTION_PIP_NAME `
  --vnet-name $VNET_NAME `
  --sku Standard `
  --file-copy true `
  --enable-tunneling true

# Create and link the required Private DNS Zones on the VNet

# Define global (non-region-specific) private DNS zones
$globalZones = @(
    "privatelink.blob.core.windows.net",
    "privatelink.file.core.windows.net",
    "privatelink.queue.core.windows.net",
    "privatelink.table.core.windows.net",
    "privatelink.vaultcore.azure.net",
    "privatelink.azurecr.io",
    "privatelink.database.windows.net",
    "privatelink.documents.azure.com",
    "privatelink.mongo.cosmos.azure.com",
    "privatelink.gremlin.cosmos.azure.com",
    "privatelink.cassandra.cosmos.azure.com",
    "privatelink.table.cosmos.azure.com",
    "privatelink.redis.cache.windows.net",
    "privatelink.postgres.database.azure.com",
    "privatelink.mysql.database.azure.com",
    "privatelink.mariadb.database.azure.com",
    "privatelink.azuresynapse.net",
    "privatelink.dev.azuresynapse.net",
    "privatelink.web.core.windows.net",
    "privatelink.monitor.azure.com",
    "privatelink.oms.opinsights.azure.com",
    "privatelink.agentsvc.azure-automation.net",
    "privatelink.azure-api.net",
    "privatelink.servicebus.windows.net",
    "privatelink.eventgrid.azure.net",
    "privatelink.eventhub.windows.net",
    "privatelink.azurewebsites.net",
    "privatelink.batch.azure.com",
    "privatelink.search.windows.net",
    "privatelink.siterecovery.windowsazure.com",
    "privatelink.azurearcdata.com"
)

# Define region-specific zones
$regionSpecificZones = @(
    "privatelink.$location.azurecontainerapps.io",
    "privatelink.$location.azmk8s.io",
    "privatelink.$location.applicationinsights.azure.com",
    "privatelink.$location.datafactory.azure.net"
)

# Combine all zones
$privateDnsZones = $globalZones + $regionSpecificZones

foreach ($zone in $privateDnsZones) {
    #Check to see if the Private DNS Zone already exists
    if (az network private-dns zone exists --name $zone --resource-group $RESOURCE_GROUP) {
        Write-Host "Private DNS Zone $zone already exists in $RESOURCE_GROUP. Skipping creation."
        continue
    }
    az network private-dns zone create `
        --resource-group $RESOURCE_GROUP `
        --name $zone
    #check if the VNet link already exists
    if (az network private-dns link vnet exists --zone-name $zone --resource-group $RESOURCE_GROUP --name "vnet-link") {
        Write-Host "Private DNS Zone link for $zone already exists in $RESOURCE_GROUP. Skipping creation."
        continue
    }
    az network private-dns link vnet create `
        --resource-group $RESOURCE_GROUP `
        --zone-name $zone `
        --name "vnet-link" `
        --virtual-network $VNET_NAME `
        --registration-enabled false
}

# Create an Azure Key Vault to store the VM Password
$MY_PUBLIC_IP = $((Invoke-WebRequest ifconfig.me/ip).Content.Trim())
# Get the public IP address of the machine running this script
Write-Host "My Public IP: $MY_PUBLIC_IP"
# Get the current user ID to assign Key Vault permissions
$CURRENT_USER_ID = az ad signed-in-user show --query userPrincipalName -o tsv
Write-Host "Current User ID: $CURRENT_USER_ID"

# Create the Key Vault
Write-Host "Creating Key Vault $KV_NAME in $RESOURCE_GROUP..."
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
Write-Host "Key Vault $KV_NAME created. Id is $KV_ID"

# Assign the Key Vault Administrator role to the current user
Write-Host "Assigning Key Vault Administrator role to $CURRENT_USER_ID for Key Vault $KV_NAME..."
az role assignment create `
--assignee $CURRENT_USER_ID `
--role "Key Vault Administrator" `
--scope $KV_ID

#Create an Image Gallery to store the Dev Build VM images
$GALLERY_NAME = "galleryacaalbumapi$($RANDOM_SUFFIX)"

# Check if the gallery exists
$galleryExists = $false
try {
    $galleryInfo = az sig gallery show --name $GALLERY_NAME --resource-group $RESOURCE_GROUP --query "name" -o tsv 2>$null
    if ($galleryInfo -eq $GALLERY_NAME) {
        $galleryExists = $true
    }
} catch {}

if ($galleryExists) {
    Write-Host "Shared Image Gallery $GALLERY_NAME already exists in $RESOURCE_GROUP. Skipping creation."
} else {
    Write-Host "Creating Shared Image Gallery $GALLERY_NAME in $RESOURCE_GROUP..."
    az sig create `
        --resource-group $RESOURCE_GROUP `
        --gallery-name $GALLERY_NAME `
        --location $LOCATION
}

# Create a Shared Image Gallery Image Definition for the Ubuntu VM
$IMAGE_DEFINITION_NAME = "ubuntuimgdefacaalbumapi$($RANDOM_SUFFIX)"

# Check if the image definition exists
$imageDefinitionExists = $false
try {
    $imageDefinitionInfo = az sig image-definition show --gallery-name $GALLERY_NAME --gallery-image-definition $IMAGE_DEFINITION_NAME --resource-group $RESOURCE_GROUP --query "name" -o tsv 2>$null
    if ($imageDefinitionInfo -eq $IMAGE_DEFINITION_NAME) {
        $imageDefinitionExists = $true
    }
} catch {}

if ($imageDefinitionExists) {
    Write-Host "Shared Image Gallery Image Definition $IMAGE_DEFINITION_NAME already exists in $RESOURCE_GROUP. Skipping creation."
} else {
    Write-Host "Creating Shared Image Gallery Image Definition $IMAGE_DEFINITION_NAME in $RESOURCE_GROUP..."
    az sig image-definition create `
        --gallery-name $GALLERY_NAME `
        --gallery-image-definition $IMAGE_DEFINITION_NAME `
        --resource-group $RESOURCE_GROUP `
        --os-type Linux `
        --publisher "EmbergerShared" `
        --offer "Ubuntu-Dev" `
        --sku "Ubuntu-Dev-SKU"
}

# Create a Shared Image Gallery Image Version for the Ubuntu VM

# Create a temporary VM to serve as the basis for our image
Write-Host "Creating temporary VM $BUILD_AGENT_VM_NAME to prepare as Azure DevOps agent..."

# Save VM credentials to Key Vault
az keyvault secret set `
    --vault-name $KV_NAME `
    --name $BUILD_AGENT_ADMIN_USERNAME_KV_SECRET_NAME `
    --value $BUILD_AGENT_VM_ADMIN_USERNAME

az keyvault secret set `
    --vault-name $KV_NAME `
    --name $BUILD_AGENT_ADMIN_PASSWORD_KV_SECRET_NAME `
    --value $BUILD_AGENT_VM_ADMIN_PASSWORD

# Create the VM with Standard security type (not TrustedLaunch)
Write-Host "Creating temporary VM $BUILD_AGENT_VM_NAME in $RESOURCE_GROUP..."
az vm create `
    --resource-group $RESOURCE_GROUP `
    --name $BUILD_AGENT_VM_NAME `
    --image "Canonical:0001-com-ubuntu-server-jammy:22_04-lts-gen2:latest" `
    --admin-username "$BUILD_AGENT_VM_ADMIN_USERNAME" `
    --admin-password "$BUILD_AGENT_VM_ADMIN_PASSWORD" `
    --vnet-name $VNET_NAME `
    --subnet $WKLD_SUBNET_NAME `
    --size "Standard_D4s_v3" `
    --public-ip-address '""' `
    --nsg-rule NONE `
    --enable-secure-boot false `
    --enable-vtpm false

# Create the installation script content as a PowerShell variable
$installScript = @'
#!/bin/bash
# Update and install basic tools
apt-get update
apt-get upgrade -y
apt-get install -y curl git jq build-essential unzip zip nodejs npm

# Install Docker
curl -fsSL https://get.docker.com -o get-docker.sh
sh get-docker.sh
usermod -aG docker adoagentadmin

# Install Azure CLI
curl -sL https://aka.ms/InstallAzureCLIDeb | bash

# Install PowerShell
curl -sSL https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -o packages-microsoft-prod.deb
dpkg -i packages-microsoft-prod.deb
apt-get update
apt-get install -y powershell

# Install .NET SDK
apt-get install -y apt-transport-https
apt-get update
apt-get install -y dotnet-sdk-8.0

# Create directory for Azure DevOps agent
mkdir -p /home/adoagentadmin/ado-agent
chown -R adoagentadmin:adoagentadmin /home/adoagentadmin/ado-agent

# Note: The actual ADO agent setup will be done when deploying a VM from this image
'@

# Create a temporary file for the script
$tempFile = New-TemporaryFile
$installScript | Out-File -FilePath $tempFile.FullName -Encoding ascii

# Install Azure DevOps agent prerequisites
Write-Host "Installing Azure DevOps agent prerequisites on $BUILD_AGENT_VM_NAME..."
az vm run-command invoke `
    --resource-group $RESOURCE_GROUP `
    --name $BUILD_AGENT_VM_NAME `
    --command-id RunShellScript `
    --scripts "@$($tempFile.FullName)"

# Remove the temporary file
Remove-Item -Path $tempFile.FullName

# Allow the VM to settle
Write-Host "Waiting for installations to complete..."
Start-Sleep -Seconds 60

# Generalize (Sysprep) the VM - this makes it suitable for creating an image
Write-Host "Generalizing VM $BUILD_AGENT_VM_NAME..."
az vm deallocate --resource-group $RESOURCE_GROUP --name $BUILD_AGENT_VM_NAME
az vm generalize --resource-group $RESOURCE_GROUP --name $BUILD_AGENT_VM_NAME

# Create a managed image from the VM
Write-Host "Creating managed image $BUILD_AGENT_MANAGED_IMAGE_NAME from VM $BUILD_AGENT_VM_NAME..."
az image create `
    --resource-group $RESOURCE_GROUP `
    --name $BUILD_AGENT_MANAGED_IMAGE_NAME `
    --source $BUILD_AGENT_VM_NAME

# Get the managed image ID
$MANAGED_IMAGE_ID = $(az image show --resource-group $RESOURCE_GROUP --name $BUILD_AGENT_MANAGED_IMAGE_NAME --query id -o tsv)

# Create an image version in the Shared Image Gallery
Write-Host "Creating image version $BUILD_AGENT_IMAGE_VERSION_NAME in gallery $GALLERY_NAME..."
az sig image-version create `
    --resource-group $RESOURCE_GROUP `
    --gallery-name $GALLERY_NAME `
    --gallery-image-definition $IMAGE_DEFINITION_NAME `
    --gallery-image-version $BUILD_AGENT_IMAGE_VERSION_NAME `
    --managed-image $MANAGED_IMAGE_ID `
    --target-regions $LOCATION `
    --replica-count 1

# Optional: Delete the temporary VM to save costs
Write-Host "Cleaning up temporary resources..."
az vm delete --resource-group $RESOURCE_GROUP --name $BUILD_AGENT_VM_NAME --yes

---

# Create a Windows Jumpbox VM in the VNet
# Generate the password for the Developer VM
$JUMPBOX_ADMIN_PASSWORD = GeneratePassword 18

## Create a Windows 11 VM to be the jumpbox. This will mimic the local dev machine.
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

Write-Host "Jumpbox VM $JUMPBOX_NAME created in $RESOURCE_GROUP with username $JUMPBOX_ADMIN_USERNAME."

# Store the id and password KeyVault
az keyvault secret set `
  --vault-name $KV_NAME `
  --name $JUMPBOX_ADMIN_USERNAME_KV_SECRET_NAME `
  --value $JUMPBOX_ADMIN_USERNAME

az keyvault secret set `
  --vault-name $KV_NAME `
  --name $JUMPBOX_ADMIN_PASSWORD_KV_SECRET_NAME `
  --value $JUMPBOX_ADMIN_PASSWORD

Write-Host "Jumpbox VM credentials stored in Key Vault $KV_NAME."
```

### Use the Jumpbox VM

#### Login to the Jumpbox VM

##### Using Azure Bastion in Browser RDP client

- In the Azure Portal, use `Connect via Bastion` to login the Jumpbox VM

- Select:
  
  - Authentication Type: `Password from Azure Key Vault`
  
  - Enter the user name (default is `acaadmin` from the script)
  
  - Select the Key Vault
  
  - Select the Kay Vault Secret

  - Click `Connect`

#### Using Azure Bastion with Remote Desktop Tunneling

```pwsh
$JUMPBOX_ID=$(az vm show --name $JUMPBOX_NAME --resource-group $RESOURCE_GROUP --query 'id' -o tsv)

az network bastion rdp --name $BASTION_NAME --resource-group $RESOURCE_GROUP --target-resource-id  $JUMPBOX_ID
```

#### Install the required tools on the Jumpbox VM

- Follow the Startup wizard (Next, Accept)

- Launch a PowerShell terminal **as Administrator**

- Execute these commands to get the elements and tools required (there are many ways to achieve the same result and some of the tools are personal preferences)

```pwsh
# 1. Install Chocolatey
Set-ExecutionPolicy Bypass -Scope Process -Force
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))

# 2. Chocolatey functions
Function Install-ChocoPackage {
    param (
        [Parameter(Mandatory = $true)]
        [Object]$Packages
    )

    foreach ($package in $Packages) {
        $command = "choco install $package -y"
        Write-Host
        Write-Host "Install-ChocoPackage => Executing: $command"
        Invoke-Expression $command
    }
}

# 3. Install packages
$base_core = @(
    "azure-cli",
    "docker-desktop",
    "cascadiacode",
    "cascadiamono",
    "firefox",
    "git",
    "jq",
    "nerd-fonts-cascadiacode",
    "nerd-fonts-firacode",
    "nerd-fonts-firamono",
    "nerd-fonts-jetbrainsmono",
    "notepadplusplus",
    "powershell-core",
    "vscode",
    "openshift-cli"
)
Install-ChocoPackage -Packages $base_core

# 4. Install WSL to enable Docker Desktop WSL integration
wsl --install

# 5. Reboot

# 6. Finish Docker Desktop installation (`Accept`, `Skip`, Check the 'Engine starts`)

# 7. Clone this repository
mkdir C:\data && cd C:\data
git clone https://github.com/embergershared/aca-full-private-app.git
cd aca-full-private-app
vscode .
```

#### Lock existing resources and Deploy the Container App privately

In the Jumpbox VM, open a PowerShell terminal and execute the following commands to deploy the Container App privately.

```pwsh
####################   LOG IN TO THE JUMPBOX VM with Tools  ####################
cd C:\data\aca-full-private-app
$RANDOM_SUFFIX = "XXX" # Replace with the actual random suffix used in previous step.

az login
# Enter credentials
# Select the subscription to use

$RESOURCE_GROUP="rg-aca-quickstart-album-api-04"
$LOCATION="southcentralus"

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

$JUMPBOX_NAME="vm-win-albumapi-$($RANDOM_SUFFIX)"
$JUMPBOX_ADMIN_USERNAME="acaadmin"
$JUMPBOX_ADMIN_PASSWORD_KV_SECRET_NAME="$($JUMPBOX_NAME)-admin-password"

$BUILD_IMAGE_NAME = "eb-apps/album-api"
$BUILD_IMAGE_TAG = "original"

$ENVIRONMENT="aca-env-private-album-api"
$API_NAME="aca-app-private-album-api"


# 1. Update Key Vault to disable public network access
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
$KV_FQDN="$($KV_NAME).vault.azure.net"

# 5. Create the private DNS record
az network private-dns record-set a create --name $KV_NAME --zone-name "privatelink.vaultcore.azure.net" --resource-group $RESOURCE_GROUP
az network private-dns record-set a add-record --record-set-name $KV_NAME --zone-name "privatelink.vaultcore.azure.net" --resource-group $RESOURCE_GROUP --ipv4-address $KV_PRIVATE_IP


# 6. Create a Private endpoint capable Container Registry
az acr create `
    --name $ACR_NAME `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --sku Premium `
    --public-network-enabled false

$ACR_ID=$(az acr show --name $ACR_NAME --resource-group $RESOURCE_GROUP --query id -o tsv)

# 7. Create a Private Endpoint for the ACR
az network private-endpoint create `
    --name "$($ACR_NAME)-pe" `
    --resource-group $RESOURCE_GROUP `
    --vnet-name $VNET_NAME `
    --subnet $PE_SUBNET_NAME `
    --private-connection-resource-id $ACR_ID `
    --group-id registry `
    --nic-name "$($ACR_NAME)-pe-nic" `
    --connection-name "conn-acr"

# 8. Create DNS record for the ACR's private endpoint
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

# 9. From Private VM, Build the container image and push it to the ACR
az acr login -n $ACR_NAME

# Pull into ACR the images used to build the App
az acr import -n $ACR_NAME --source 'mcr.microsoft.com/dotnet/sdk:6.0' -t 'mcr/dotnet/sdk:6.0'
az acr import -n $ACR_NAME --source 'mcr.microsoft.com/dotnet/aspnet:6.0' -t 'mcr/dotnet/aspnet:6.0'

# Update Dockerfile with ACR name
(Get-Content -Path "containerapps-albumapi-csharp\src\Dockerfile") -replace '<acr_name>', $ACR_NAME | Set-Content -Path "containerapps-albumapi-csharp\src\Dockerfile"

# Build & Push to ACR the App image, using dotnet images from the ACR
# $BUILD_IMAGE_TAG="emm-03"
docker build -t "${ACR_NAME}.azurecr.io/${BUILD_IMAGE_NAME}:${BUILD_IMAGE_TAG}" containerapps-albumapi-csharp/src
docker push "${ACR_NAME}.azurecr.io/${BUILD_IMAGE_NAME}:${BUILD_IMAGE_TAG}"

# 10. Deploy the Container App Environment
## Create a Storage Account
az storage account create `
    --name $STORAGE_ACCOUNT `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --sku Standard_LRS `
    --kind StorageV2 `
    --public-network-access Disabled

## Create a Private Endpoint for Storage Account
$STORAGE_ID=$(az storage account show --name $STORAGE_ACCOUNT --resource-group $RESOURCE_GROUP --query id -o tsv)

az network private-endpoint create `
    --name "$($STORAGE_ACCOUNT)-pe" `
    --resource-group $RESOURCE_GROUP `
    --vnet-name $VNET_NAME `
    --subnet $PE_SUBNET_NAME `
    --private-connection-resource-id $STORAGE_ID `
    --group-id blob `
    --nic-name "$($STORAGE_ACCOUNT)-pe-nic" `
    --connection-name "conn-storage"

# Create DNS records for the Storage Account private endpoint
$STORAGE_PE_NIC_ID=$(az network private-endpoint show --name "$($STORAGE_ACCOUNT)-pe" --resource-group $RESOURCE_GROUP --query 'networkInterfaces[0].id' -o tsv)
$STORAGE_PRIVATE_IP=$(az network nic show --ids $STORAGE_PE_NIC_ID --query "ipConfigurations[0].privateIPAddress" -o tsv)

az network private-dns record-set a create --name $STORAGE_ACCOUNT --zone-name "privatelink.blob.core.windows.net" --resource-group $RESOURCE_GROUP
az network private-dns record-set a add-record --record-set-name $STORAGE_ACCOUNT --zone-name "privatelink.blob.core.windows.net" --resource-group $RESOURCE_GROUP --ipv4-address $STORAGE_PRIVATE_IP

## Create a Log Analytics Workspace
az monitor log-analytics workspace create `
    --workspace-name $LOG_ANALYTICS_WORKSPACE `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION

## Get the Log Analytics Client ID and Client Secret + App Environment ID
$LOG_ANALYTICS_WORKSPACE_CLIENT_ID=az monitor log-analytics workspace show --resource-group $RESOURCE_GROUP --workspace-name $LOG_ANALYTICS_WORKSPACE --query customerId -o tsv
$LOG_ANALYTICS_WORKSPACE_CLIENT_SECRET=az monitor log-analytics workspace get-shared-keys --resource-group $RESOURCE_GROUP --workspace-name $LOG_ANALYTICS_WORKSPACE --query primarySharedKey -o tsv
$ACA_ENV_SUBNET_ID = $(az network vnet subnet show --resource-group $RESOURCE_GROUP --vnet-name $VNET_NAME --name $ACA_ENV_SUBNET_NAME --query id -o tsv)

## Create the Container App Environment
az containerapp env create `
    --name $ENVIRONMENT `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --logs-workspace-id $LOG_ANALYTICS_WORKSPACE_CLIENT_ID `
    --logs-workspace-key $LOG_ANALYTICS_WORKSPACE_CLIENT_SECRET `
    --enable-workload-profiles true `
    --internal-only true `
    --infrastructure-subnet-resource-id $ACA_ENV_SUBNET_ID `
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
az network private-dns record-set a create --name "*" --zone-name "privatelink.${LOCATION}.azurecontainerapps.io" --resource-group $RESOURCE_GROUP
az network private-dns record-set a add-record --record-set-name "*" --zone-name "privatelink.${LOCATION}.azurecontainerapps.io" --resource-group $RESOURCE_GROUP --ipv4-address $ACA_ENV_PRIVATE_IP


# 11. Create and deploy the Container App WebAPI
## Get the ACR login server
$ACR_LOGIN_SERVER=$(az acr show --name $ACR_NAME --resource-group $RESOURCE_GROUP --query loginServer -o tsv)

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
    --registry-identity 'system' `
    --query properties.configuration.ingress.fqdn


# 12. Create and deploy an API Management instance (Standard v2)

$APIM_NAME = "apim-aca-albumapi-$($RANDOM_SUFFIX)"
az apim create `
  --name $APIM_NAME `
  --resource-group $RESOURCE_GROUP `
  --publisher-name Emm `
  --publisher-email admin@contoso.com `
  --enable-managed-identity true `
  --public-network-access false `
  --sku-name StandardV2 `
  --virtual-network Internal

```

