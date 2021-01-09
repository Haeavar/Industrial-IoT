#!/bin/bash

# -------------------------------------------------------------------------------
if [[ -n "$AZ_SCRIPTS_OUTPUT_PATH" ]] ; then set -ex; else set -e; fi

usage(){
    echo '
Usage: '"$0"' 
    --sp, --password, --tenant Logs into azure using service principal.
    --identity                 Logs into azure using managed identity.

    --name                     Application name needed if configuration 
                               not provided (see --config).  The name is
                               used as display name prefix for all app
                               registrations and un-registrations.
         --clean               Unregister existing application 
                               registrations before registering.
         --unregister          Only perform un-registration.
         
    --config                   JSON configuration that has been generated
                               by a previous run or by aad-register.ps1. 
                               No registration (see --name) is performed 
                               when --config is provided. 
                               
    --keyvault                 Registration results or configuration is 
                               saved in keyvault if its name is specified. 
         --subscription        Subscription to use in which the keyvault
                               was created.  Default subscription is used 
                               if not provided.
                               
    --help                     Shows this help.
'
    exit 1
}

applicationName=
keyVaultName=
subscription=
msi=
mode=

[ $# -eq 0 ] && usage
while [ "$#" -gt 0 ]; do
    case "$1" in
        --name)            applicationName="$2" ;;
        --keyvault)        keyVaultName="$2" ;;
        --subscription)    subscription="$2" ;;
        --clean)           mode="clean" ;;
        --unregister)      mode="unregisteronly" ;;
        --identity)        mode="unattended" ;;
        --config)          config="$2" ;;
        --sp)              principalId="$2" ;;
        --password)        principalPassword="$2" ;;
        --tenant)          tenantId="$2" ;;
        --help)            usage ;;
    esac
    shift
done

# ---------- Login --------------------------------------------------------------
if [[ "$mode" == "unattended" ]] ; then
    mode=
    az login --identity --allow-no-subscriptions
elif [[ -n "$principalId" ]] && \
     [[ -n "$principalPassword" ]] && \
     [[ -n "$tenantId" ]]; then
    az login --service-principal -u $principalId \
        -p=$principalPassword -t $tenantId --allow-no-subscriptions
elif [[ -z "$AZ_SCRIPTS_OUTPUT_PATH" ]] ; then
    echo "az login"
else
    echo "Must login with service principal or managed identity"
    exit 1
fi

tenantId=$(az account show --query tenantId -o tsv | tr -d '\r')
trustedTokenIssuer="https://sts.windows.net/$tenantId"
authorityUri="https://login.microsoftonline.com"

# ---------- Unregister ---------------------------------------------------------
if [[ "$mode" == "unregisteronly" ]] || [[ "$mode" == "clean" ]] ; then
    if [[ -z "$applicationName" ]] ; then 
        echo "Parameter is empty or missing: --name"; usage
    fi
    for id in $(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications \
        --url-parameters "\$filter=startswith(displayName, '$applicationName-')" \
        --query "value[].id" -o tsv | tr -d '\r'); do
        appName=$(az rest --method get \
            --uri https://graph.microsoft.com/v1.0/applications/$id \
            --query displayName -o tsv | tr -d '\r')
        az rest --method delete \
            --uri https://graph.microsoft.com/v1.0/applications/$id
        echo "'$appName' ($id) unregistered from Graph."
    done
    if [[ "$mode" == "unregisteronly" ]] ; then
        exit 0
    fi
fi

# ---------- Register -----------------------------------------------------------
# see https://docs.microsoft.com/en-us/graph/api/resources/application?view=graph-rest-1.0
if [[ -z "$config" ]] || [[ "$config" == "{}" ]] ; then
    if [[ -z "$applicationName" ]] ; then 
        echo "Parameter is empty or missing: --name"; usage
    fi

    # ---------- client app -----------------------------------------------------
    clientId=$(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications \
        --uri-parameters "\$filter=displayName eq '$applicationName-client'" \
        --query value[0].id -o tsv | tr -d '\r')
    if [[ -z "$clientId" ]] ; then
        clientId=$(az rest --method post \
            --uri https://graph.microsoft.com/v1.0/applications \
            --headers Content-Type=application/json \
            --body '{ 
                "displayName": "'"$applicationName"'-client",
                "signInAudience": "AzureADMyOrg"
            }' --query id -o tsv | tr -d '\r')
        echo "'$applicationName-client' registered in graph as $clientId..."
    else
        echo "'$applicationName-client' found in graph as $clientId..."
    fi
    IFS=$'\n'; client=($(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications/$clientId \
        --query "[appId, publisherDomain, id]" -o tsv | tr -d '\r')); unset IFS;
    clientAppId=${client[0]}

    # ---------- web app --------------------------------------------------------
    webappId=$(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications \
        --uri-parameters "\$filter=displayName eq '$applicationName-web'" \
        --query value[0].id -o tsv | tr -d '\r')
    if [[ -z "$webappId" ]] ; then
        webappId=$(az rest --method post \
            --uri https://graph.microsoft.com/v1.0/applications \
            --headers Content-Type=application/json \
            --body '{ 
                "displayName": "'"$applicationName"'-web",
                "signInAudience": "AzureADMyOrg"
            }' --query id -o tsv | tr -d '\r')
        echo "'$applicationName-web' registered in graph as $webappId..."
    else
        echo "'$applicationName-web' found in graph as $webappId..."
    fi
    IFS=$'\n'; webapp=($(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications/$webappId \
        --query "[appId, publisherDomain, id]" -o tsv | tr -d '\r')); unset IFS;
    webappAppId=${webapp[0]}
  
    # ---------- service --------------------------------------------------------
    user_impersonationScopeId=be8ef2cb-ee19-4f25-bc45-e2d27aac303b
    serviceId=$(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications \
        --uri-parameters "\$filter=displayName eq '$applicationName-service'" \
        --query value[0].id -o tsv | tr -d '\r')
    if [[ -z "$serviceId" ]] ; then
        serviceId=$(az rest --method post \
            --uri https://graph.microsoft.com/v1.0/applications \
            --headers Content-Type=application/json \
            --body '{ 
                "displayName": "'"$applicationName"'-service",
                "signInAudience": "AzureADMyOrg",
                "api": {
                   "oauth2PermissionScopes": [ {
                       "adminConsentDescription": 
"Allow the application to access '"$applicationName"' on behalf of the signed-in user.",
                       "adminConsentDisplayName": "Access '"$applicationName"'",
                       "id": "'"$user_impersonationScopeId"'",
                       "isEnabled": true,
                       "type": "User",
                       "userConsentDescription": 
"Allow the application to access '"$applicationName"' on your behalf.",
                       "userConsentDisplayName": "Access'"$applicationName"'",
                       "value": "user_impersonation"
                   } ]
                }
            }' --query id -o tsv | tr -d '\r')
        echo "'$applicationName-service' registered in graph as $serviceId..."
    else
        echo "'$applicationName-service' found in graph as $serviceId..."
    fi
    permissionScopeIds=$(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications/$serviceId \
        --query "api.oauth2PermissionScopes[].id" -o json | tr -d '\r')
    IFS=$'\n'; service=($(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications/$serviceId \
        --query "[appId, publisherDomain, id]" -o tsv | tr -d '\r')); unset IFS;
    serviceAppId=${service[0]}

    # todo - require resource accss to all permission scopes

    # ---------- update client app ----------------------------------------------
    az rest --method patch \
        --uri https://graph.microsoft.com/v1.0/applications/$clientId \
        --headers Content-Type=application/json \
        --body '{
            "isFallbackPublicClient": true,
            "publicClient": {
                "redirectUris": ["urn:ietf:wg:oauth:2.0:oob", 
                    "https://localhost", "http://localhost"]
            },
            "requiredResourceAccess": [ {
              "resourceAccess": [ 
                {"id": "'"$user_impersonationScopeId"'", "type": "Scope" }
              ],
              "resourceAppId": "'"$serviceAppId"'"
            } ]
        }'
    echo "'$applicationName-client' updated..."

    # ---------- update web app -------------------------------------------------
    az rest --method patch \
        --uri https://graph.microsoft.com/v1.0/applications/$webappId \
        --headers Content-Type=application/json \
        --body '{
            "isFallbackPublicClient": false,
            "web": {
                "redirectUris": ["urn:ietf:wg:oauth:2.0:oob", 
                    "https://localhost", "http://localhost"]
            },
            "requiredResourceAccess": [ {
              "resourceAccess": [ 
                {"id": "'"$user_impersonationScopeId"'", "type": "Scope" }
              ],
              "resourceAppId": "'"$serviceAppId"'"
            } ]
        }'
    # add service secret
    webappAppSecret=$(az rest --method post \
        --uri https://graph.microsoft.com/v1.0/applications/$webappId/addPassword \
        --headers Content-Type=application/json --body '{}' \
        --query secretText -o tsv | tr -d '\r')
    echo "'$applicationName-web' updated..."
        
    # ---------- update service app ---------------------------------------------
    # Add 1) Azure CLI and 2) Visual Studio to allow login the platform as clients
    az rest --method patch \
        --uri https://graph.microsoft.com/v1.0/applications/$serviceId \
        --headers Content-Type=application/json \
        --body '{
            "isFallbackPublicClient": false,
            "identifierUris": [ "'"https://${service[1]}/$applicationName-service"'" ],
            "api": {
                "requestedAccessTokenVersion": null,
                "knownClientApplications": [
                    "04b07795-8ddb-461a-bbee-02f9e1bf7b46", 
                    "872cd9fa-d31f-45e0-9eab-6e460a02d1f1",
                    "'"$clientAppId"'", "'"$webappAppId"'" ],
                "preAuthorizedApplications": [
                    {"appId": "04b07795-8ddb-461a-bbee-02f9e1bf7b46", 
                        "delegatedPermissionIds": '"$permissionScopeIds"' },
                    {"appId": "872cd9fa-d31f-45e0-9eab-6e460a02d1f1",
                        "delegatedPermissionIds": '"$permissionScopeIds"' },
                    {"appId": "'"$clientAppId"'", 
                        "delegatedPermissionIds": '"$permissionScopeIds"' },
                    {"appId": "'"$webappAppId"'", 
                        "delegatedPermissionIds": '"$permissionScopeIds"' }
                ]
            }
        }'
    echo "'$applicationName-service' updated..."
        
    # add service secret
    serviceAppSecret=$(az rest --method post \
        --uri https://graph.microsoft.com/v1.0/applications/$serviceId/addPassword \
        --headers Content-Type=application/json --body '{}' \
        --query secretText -o tsv | tr -d '\r')
    serviceAudience=$(az rest --method get \
        --uri https://graph.microsoft.com/v1.0/applications/$serviceId \
        --query "[identifierUris[0]]" -o tsv | tr -d '\r')
else
    # parse config
              tenantId=$(jq -r ".tenantId? // empty" <<< $config)
    trustedTokenIssuer=$(jq -r ".trustedTokenIssuer? // empty" <<< $config)
          authorityUri=$(jq -r ".authorityUri? // empty" <<< $config)
          serviceAppId=$(jq -r ".serviceAppId? // empty" <<< $config)
      serviceAppSecret=$(jq -r ".serviceAppSecret? // empty" <<< $config)
       serviceAudience=$(jq -r ".serviceAudience? // empty" <<< $config)
           clientAppId=$(jq -r ".clientAppId? // empty" <<< $config)
           webappAppId=$(jq -r ".webappAppId? // empty" <<< $config)
       webappAppSecret=$(jq -r ".webappAppSecret? // empty" <<< $config)
fi
        
# ---------- Save results -------------------------------------------------------
if [[ -n "$keyVaultName" ]] ; then
    # log in using the managed service identity and write secrets to keyvault
    if [[ -n "$subscription" ]] ; then
        az account set --subscription $subscription
    fi
    
    az keyvault secret set --vault-name $keyVaultName \
                   -n pcs-auth-required --value true
    az keyvault secret set --vault-name $keyVaultName \
                     -n pcs-auth-tenant --value "$tenantId"
    az keyvault secret set --vault-name $keyVaultName \
                     -n pcs-auth-issuer --value "$trustedTokenIssuer"
    az keyvault secret set --vault-name $keyVaultName \
                   -n pcs-auth-instance --value "$authorityUri"
    az keyvault secret set --vault-name $keyVaultName \
              -n pcs-auth-service-appid --value "$serviceAppId"
    az keyvault secret set --vault-name $keyVaultName \
             -n pcs-auth-service-secret --value "$serviceAppSecret"
    az keyvault secret set --vault-name $keyVaultName \
                   -n pcs-auth-audience --value "$serviceAudience"
    az keyvault secret set --vault-name $keyVaultName \
        -n pcs-auth-public-client-appid --value "$clientAppId"
    az keyvault secret set --vault-name $keyVaultName \
               -n pcs-auth-client-appid --value "$webappAppId"
    az keyvault secret set --vault-name $keyVaultName \
              -n pcs-auth-client-secret --value "$webappAppSecret"

    webappAppSecret=
    serviceAppSecret=
fi

# ---------- Return results -----------------------------------------------------
echo '
{
    "serviceAppId": "'"$serviceAppId"'",
    "serviceAppSecret": "'"$serviceAppSecret"'",
    "serviceAudience": "'"$serviceAudience"'",
    "webappAppId": "'"$webappAppId"'",
    "webappAppSecret": "'"$webappAppSecret"'",
    "clientAppId": "'"$clientAppId"'",
    "tenantId": "'"$tenantId"'",
    "trustedTokenIssuer": "'"$trustedTokenIssuer"'",
    "authorityUri": "'"$authorityUri"'"
}' | tee $AZ_SCRIPTS_OUTPUT_PATH
# -------------------------------------------------------------------------------

















