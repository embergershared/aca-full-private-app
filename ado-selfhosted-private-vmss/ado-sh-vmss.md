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
# $JUMPBOX_ADMIN_USERNAME="acaadmin"
# $JUMPBOX_ADMIN_PASSWORD_KV_SECRET_NAME="$($JUMPBOX_NAME)-admin-password"

# $BUILD_IMAGE_NAME = "eb-apps/album-api"
# $BUILD_IMAGE_TAG = "original"

# $ENVIRONMENT="aca-env-private-album-api"
# $API_NAME="aca-app-private-album-api"

$ADO_VMSS_NAME = "vmss-lin-ado-aca-albumapi-$($RANDOM_SUFFIX)"
$WKLD_SUBNET_ID = $(az network vnet subnet show --resource-group $RESOURCE_GROUP --vnet-name $VNET_NAME --name $WKLD_SUBNET_NAME --query id -o tsv)

# Create a VMSS for Azure DevOps self-hosted agents
## Ref: https://learn.microsoft.com/en-us/cli/azure/vmss?view=azure-cli-latest#az-vmss-create
az vmss create `
  --name $ADO_VMSS_NAME `
  --resource-group $RESOURCE_GROUP `
  --image Ubuntu2204 `
  --vm-sku Standard_D8s_v6 `
  --storage-sku StandardSSD_LRS `
  --authentication-type SSH `
  --generate-ssh-keys `
  --instance-count 2 `
  --disable-overprovision `
  --upgrade-policy-mode manual `
  --single-placement-group false `
  --platform-fault-domain-count 1 `
  --load-balancer '""' `
  --orchestration-mode Uniform `
  --subnet $WKLD_SUBNET_ID


# Add Azure DevOps self-hosted agent extension to the VMSS
az vmss extension set `
  --vmss-name $ADO_VMSS_NAME `
  --resource-group $RESOURCE_GROUP `
  --name CustomScript `
  --version 2.0 `
  --publisher Microsoft.Azure.Extensions `
  --settings '{ 
    "fileUris": ["https://raw.githubusercontent.com/embergershared/aca-full-private-app/main/ado-selfhosted-private-vmss/post-install.sh"], 
    "commandToExecute": "bash ./post-install.sh" 
  }'



```





## References

- [Azure DevOps Self-hosted agents on Azure VMSS](https://learn.microsoft.com/en-us/azure/devops/pipelines/agents/vmss?view=azure-devops)
- [Azure DevOps Self-hosted agents outbound requirements](https://learn.microsoft.com/en-us/azure/devops/pipelines/agents/linux-agent?view=azure-devops&tabs=IP-V4#im-running-a-firewall-and-my-code-is-in-azure-repos-what-urls-does-the-agent-need-to-communicate-with)
- [Azure DevOps Self-hosted VMSS agent customization script](https://learn.microsoft.com/en-us/azure/devops/pipelines/agents/scale-set-agents?view=azure-devops#customizing-virtual-machine-startup-via-the-custom-script-extension)
