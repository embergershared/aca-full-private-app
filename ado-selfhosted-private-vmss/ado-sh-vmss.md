# ADO Self-hosted agent on Azure VMSS

## Overview

## Steps

```pwsh
$RANDOM_SUFFIX = "384"

$RESOURCE_GROUP="rg-aca-quickstart-album-api-04"
#$LOCATION="southcentralus"

$VNET_NAME = "vnet-aca-albumapi-$($RANDOM_SUFFIX)"
$WKLD_SUBNET_NAME = "wkld-snet"

# $ACA_ENV_SUBNET_NAME = "aca-env-snet"
# $PE_SUBNET_NAME = "pe-snet"
# $BASTION_NAME = "bastion-aca-albumapi-$($RANDOM_SUFFIX)"
# $BASTION_PIP_NAME = "$($BASTION_NAME)-pip"

# $ACR_NAME = "acracaalbumapi$($RANDOM_SUFFIX)"
$LOG_ANALYTICS_WORKSPACE="law-aca-albumapi-$($RANDOM_SUFFIX)"
$STORAGE_ACCOUNT="stacaalbumapi$($RANDOM_SUFFIX)"
# $KV_NAME = "kv-aca-albumapi-$($RANDOM_SUFFIX)"

# $JUMPBOX_NAME="vm-win-albumapi-$($RANDOM_SUFFIX)"
# $JUMPBOX_ID=$(az vm show --name $JUMPBOX_NAME --resource-group $RESOURCE_GROUP --query 'id' -o tsv)
# $BASTION_NAME = "bastion-aca-albumapi-$($RANDOM_SUFFIX)"
# az network bastion rdp --name $BASTION_NAME --resource-group $RESOURCE_GROUP --target-resource-id  $JUMPBOX_ID

# $JUMPBOX_ADMIN_USERNAME="acaadmin"
# $ADO_VMSS_ADMIN_PASSWORD_KV_SECRET_NAME="$($JUMPBOX_NAME)-admin-password"


# $BUILD_IMAGE_NAME = "eb-apps/album-api"
# $BUILD_IMAGE_TAG = "original"

# $ENVIRONMENT="aca-env-private-album-api"
# $API_NAME="aca-app-private-album-api"

$RANDOM_SUFFIX = "384"
$RESOURCE_GROUP="rg-aca-quickstart-album-api-04"
$VNET_NAME = "vnet-aca-albumapi-$($RANDOM_SUFFIX)"
$WKLD_SUBNET_NAME = "wkld-snet"

$ADO_VMSS_NAME = "vmss-ubu22-ado-aca-albumapi-$($RANDOM_SUFFIX)"
$ADO_VMSS_ADMIN_PASSWORD_KV_SECRET_NAME="$($ADO_VMSS_NAME)-admin-password"
$ADO_VMSS_ADMIN_NAME = "adoadmin"

$WKLD_SUBNET_ID = $(az network vnet subnet show --resource-group $RESOURCE_GROUP --vnet-name $VNET_NAME --name $WKLD_SUBNET_NAME --query id -o tsv)

$KV_NAME = "kv-aca-albumapi-$($RANDOM_SUFFIX)"
$KV_ID = $(az keyvault show --name $KV_NAME --resource-group $RESOURCE_GROUP --query id -o tsv)


# Create a Windows Jumpbox VM in the VNet with a Public IP
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

$ADO_VMSS_ADMIN_PASSWORD = GeneratePassword 18

az keyvault secret set `
  --vault-name $KV_NAME `
  --name $ADO_VMSS_ADMIN_PASSWORD_KV_SECRET_NAME `
  --value $ADO_VMSS_ADMIN_PASSWORD


# Create a VMSS for Azure DevOps self-hosted agents
## Ref: https://learn.microsoft.com/en-us/cli/azure/vmss?view=azure-cli-latest#az-vmss-create
# https://learn.microsoft.com/en-us/azure/virtual-machines/linux/using-cloud-init
az vmss create `
  --name $ADO_VMSS_NAME `
  --resource-group $RESOURCE_GROUP `
  --image Ubuntu2204 `
  --vm-sku Standard_D8s_v6 `
  --admin-username $ADO_VMSS_ADMIN_NAME `
  --admin-password $ADO_VMSS_ADMIN_PASSWORD `
  --storage-sku StandardSSD_LRS `
  --authentication-type password `
  --instance-count 1 `
  --custom-data cloud-init_ado.yaml `
  --disable-overprovision `
  --upgrade-policy-mode manual `
  --single-placement-group false `
  --platform-fault-domain-count 1 `
  --load-balancer '""' `
  --orchestration-mode Uniform `
  --subnet $WKLD_SUBNET_ID

# Add Azure DevOps self-hosted agent extension to the VMSS
# az vmss extension set `
#   --vmss-name $ADO_VMSS_NAME `
#   --resource-group $RESOURCE_GROUP `
#   --name CustomScript `
#   --version 2.0 `
#   --publisher Microsoft.Azure.Extensions `
#   --settings '{ \"fileUris\": [\"https://raw.githubusercontent.com/embergershared/aca-full-private-app/main/ado-selfhosted-private-vmss/post-install.sh\"], \"commandToExecute\": \"bash ./post-install.sh\" }'
```

## References

- [Azure DevOps Self-hosted agents on Azure VMSS](https://learn.microsoft.com/en-us/azure/devops/pipelines/agents/vmss?view=azure-devops)
- [Azure DevOps Self-hosted agents outbound requirements](https://learn.microsoft.com/en-us/azure/devops/pipelines/agents/linux-agent?view=azure-devops&tabs=IP-V4#im-running-a-firewall-and-my-code-is-in-azure-repos-what-urls-does-the-agent-need-to-communicate-with)
- [Azure DevOps Self-hosted VMSS agent customization script](https://learn.microsoft.com/en-us/azure/devops/pipelines/agents/scale-set-agents?view=azure-devops#customizing-virtual-machine-startup-via-the-custom-script-extension)
