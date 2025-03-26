{
    "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
    "contentVersion": "1.0.0.0",
    "parameters": {
        "environmentName": {
            "type": "String",
            "metadata": {
                "description": "Name of the Container Apps Environment"
            }
        },
        "location": {
            "defaultValue": "[resourceGroup().location]",
            "type": "String",
            "metadata": {
                "description": "Location for all resources"
            }
        },
        "useHttpIngress": {
            "defaultValue": false,
            "type": "Bool",
            "metadata": {
                "description": "Enable HTTP/HTTPS ingress"
            }
        },
        "vnetAddressPrefix": {
            "defaultValue": "10.0.0.0/16",
            "type": "String",
            "metadata": {
                "description": "Address space for virtual network"
            }
        },
        "caSubnetPrefix": {
            "defaultValue": "10.0.0.0/23",
            "type": "String",
            "metadata": {
                "description": "Subnet prefix for Container Apps"
            }
        },
        "vmSubnetPrefix": {
            "defaultValue": "10.0.2.0/24",
            "type": "String",
            "metadata": {
                "description": "Subnet prefix for Virtual Machines"
            }
        },
        "adminUsername": {
            "type": "String",
            "metadata": {
                "description": "Username for the Virtual Machine"
            }
        },
        "adminPassword": {
            "type": "SecureString",
            "metadata": {
                "description": "Password for the Virtual Machine"
            }
        },
        "vmSize": {
            "defaultValue": "Standard_D2s_v3",
            "type": "String",
            "metadata": {
                "description": "Size of the Virtual Machine"
            }
        },
        "osType": {
            "defaultValue": "Ubuntu2204",
            "allowedValues": [
                "Ubuntu2204",
                "Windows2022"
            ],
            "type": "String",
            "metadata": {
                "description": "Type of OS for the Virtual Machine"
            }
        },
        "LOCAL_PORT1": {
            "defaultValue": "8080",
            "type": "String",
            "metadata": {
                "description": "Local port 1 (mandatory)"
            }
        },
        "functionAppName": {
            "type": "String",
            "defaultValue": "[concat(parameters('environmentName'), '-startvm-func')]",
            "metadata": {
                "description": "Name of the Function App"
            }
        },
        "storageAccountName": {
            "type": "String",
            "defaultValue": "[concat('stor', uniqueString(resourceGroup().id))]",
            "metadata": {
                "description": "Storage Account for Function App"
            }
        },
        "functionAppServicePlanName": {
            "type": "String",
            "defaultValue": "[concat(parameters('environmentName'), '-func-plan')]",
            "metadata": {
                "description": "Name of App Service Plan for Function App"
            }
        }
    },
    "variables": {
        "appName": "[concat(parameters('environmentName'), '-app')]",
        "vnetName": "[concat(parameters('environmentName'), '-vnet')]",
        "vmName": "[concat(parameters('environmentName'), '-vm01')]",
        "nicName": "[concat(variables('vmName'), '-nic')]",
        "caSubnetName": "CA-subnet",
        "vmSubnetName": "VM-subnet",
        "imageReference": {
            "Ubuntu2204": {
                "publisher": "Canonical",
                "offer": "0001-com-ubuntu-server-jammy",
                "sku": "22_04-lts-gen2",
                "version": "latest"
            },
            "Windows2022": {
                "publisher": "MicrosoftWindowsServer",
                "offer": "WindowsServer",
                "sku": "2022-Datacenter",
                "version": "latest"
            }
        },
        "defaultIngress": {
            "external": true,
            "targetPort": "[int(parameters('LOCAL_PORT1'))]",
            "exposedPort": "[int(parameters('LOCAL_PORT1'))]",
            "transport": "Tcp",
            "traffic": [
                {
                    "weight": 100,
                    "latestRevision": true
                }
            ]
        },
        "httpIngress": {
            "external": true,
            "targetPort": "[int(parameters('LOCAL_PORT1'))]",
            "transport": "auto",
            "allowInsecure": true
        },
        "roleDefinitionId": "b24988ac-6180-42a0-ab88-20f7382dd24c",
        "roleAssignmentName": "[guid(resourceId('Microsoft.Compute/virtualMachines', variables('vmName')), variables('roleDefinitionId'), resourceId('Microsoft.Web/sites', parameters('functionAppName')))]"
    },
    "resources": [
        {
            "type": "Microsoft.Network/virtualNetworks",
            "apiVersion": "2023-05-01",
            "name": "[variables('vnetName')]",
            "location": "[parameters('location')]",
            "properties": {
                "addressSpace": {
                    "addressPrefixes": [
                        "[parameters('vnetAddressPrefix')]"
                    ]
                },
                "subnets": [
                    {
                        "name": "[variables('caSubnetName')]",
                        "properties": {
                            "addressPrefix": "[parameters('caSubnetPrefix')]"
                        }
                    },
                    {
                        "name": "[variables('vmSubnetName')]",
                        "properties": {
                            "addressPrefix": "[parameters('vmSubnetPrefix')]"
                        }
                    }
                ]
            }
        },
        {
            "type": "Microsoft.Network/networkInterfaces",
            "apiVersion": "2023-05-01",
            "name": "[variables('nicName')]",
            "location": "[parameters('location')]",
            "dependsOn": [
                "[resourceId('Microsoft.Network/virtualNetworks', variables('vnetName'))]"
            ],
            "properties": {
                "ipConfigurations": [
                    {
                        "name": "ipconfig1",
                        "properties": {
                            "subnet": {
                                "id": "[resourceId('Microsoft.Network/virtualNetworks/subnets', variables('vnetName'), variables('vmSubnetName'))]"
                            },
                            "privateIPAllocationMethod": "Dynamic"
                        }
                    }
                ]
            }
        },
        {
            "type": "Microsoft.Compute/virtualMachines",
            "apiVersion": "2023-07-01",
            "name": "[variables('vmName')]",
            "location": "[parameters('location')]",
            "dependsOn": [
                "[resourceId('Microsoft.Network/networkInterfaces', variables('nicName'))]"
            ],
            "properties": {
                "hardwareProfile": {
                    "vmSize": "[parameters('vmSize')]"
                },
                "osProfile": {
                    "computerName": "[variables('vmName')]",
                    "adminUsername": "[parameters('adminUsername')]",
                    "adminPassword": "[parameters('adminPassword')]"
                },
                "storageProfile": {
                    "imageReference": "[variables('imageReference')[parameters('osType')]]",
                    "osDisk": {
                        "createOption": "FromImage",
                        "managedDisk": {
                            "storageAccountType": "Premium_LRS"
                        }
                    }
                },
                "networkProfile": {
                    "networkInterfaces": [
                        {
                            "id": "[resourceId('Microsoft.Network/networkInterfaces', variables('nicName'))]"
                        }
                    ]
                }
            }
        },
        {
            "type": "Microsoft.App/managedEnvironments",
            "apiVersion": "2023-05-01",
            "name": "[concat(parameters('environmentName'), '-vnet')]",
            "location": "[parameters('location')]",
            "dependsOn": [
                "[resourceId('Microsoft.Network/virtualNetworks', variables('vnetName'))]"
            ],
            "sku": {
                "name": "Consumption"
            },
            "properties": {
                "zoneRedundant": false,
                "vnetConfiguration": {
                    "infrastructureSubnetId": "[resourceId('Microsoft.Network/virtualNetworks/subnets', variables('vnetName'), variables('caSubnetName'))]",
                    "internal": false
                }
            }
        },
        {
            "type": "Microsoft.App/containerApps",
            "apiVersion": "2023-05-01",
            "name": "[variables('appName')]",
            "location": "[parameters('location')]",
            "dependsOn": [
                "[resourceId('Microsoft.App/managedEnvironments', concat(parameters('environmentName'), '-vnet'))]",
                "[resourceId('Microsoft.Network/networkInterfaces', variables('nicName'))]"
            ],
            "properties": {
                "managedEnvironmentId": "[resourceId('Microsoft.App/managedEnvironments', concat(parameters('environmentName'), '-vnet'))]",
                "configuration": {
                    "activeRevisionsMode": "Single",
                    "ingress": "[if(parameters('useHttpIngress'), variables('httpIngress'), variables('defaultIngress'))]"
                },
                "template": {
                    "containers": [
                        {
                            "name": "app",
                            "image": "mafamafa/haproxy-env-config:202501271157",
                            "env": [
                                {
                                    "name": "LOCAL_PORT1",
                                    "value": "[if(not(parameters('useHttpIngress')), parameters('LOCAL_PORT1'), '')]"
                                },
                                {
                                    "name": "BACKEND_HOST1",
                                    "value": "[if(not(parameters('useHttpIngress')), concat(reference(variables('nicName')).ipConfigurations[0].properties.privateIPAddress, ':', parameters('LOCAL_PORT1')), '')]"
                                },
                                {
                                    "name": "LOCAL_PORT6",
                                    "value": "[if(parameters('useHttpIngress'), parameters('LOCAL_PORT1'), '')]"
                                },
                                {
                                    "name": "BACKEND_HOST6",
                                    "value": "[if(parameters('useHttpIngress'), concat(reference(variables('nicName')).ipConfigurations[0].properties.privateIPAddress, ':', parameters('LOCAL_PORT1')), '')]"
                                }
                            ],
                            "resources": {
                                "cpu": "0.5",
                                "memory": "1Gi"
                            }
                        }
                    ],
                    "scale": {
                        "minReplicas": 0,
                        "maxReplicas": 1
                    }
                }
            }
        },
        {
            "type": "Microsoft.Storage/storageAccounts",
            "apiVersion": "2022-09-01",
            "name": "[parameters('storageAccountName')]",
            "location": "[parameters('location')]",
            "sku": {
                "name": "Standard_LRS"
            },
            "kind": "StorageV2",
            "properties": {
                "supportsHttpsTrafficOnly": true,
                "minimumTlsVersion": "TLS1_2"
            }
        },
        {
            "type": "Microsoft.Web/serverfarms",
            "apiVersion": "2022-03-01",
            "name": "[parameters('functionAppServicePlanName')]",
            "location": "[parameters('location')]",
            "sku": {
                "name": "Y1",
                "tier": "Dynamic",
                "size": "Y1",
                "family": "Y",
                "capacity": 0
            },
            "properties": {},
            "kind": "windows"
        },
        {
            "type": "Microsoft.Web/sites",
            "apiVersion": "2022-03-01",
            "name": "[parameters('functionAppName')]",
            "location": "[parameters('location')]",
            "kind": "functionapp",
            "identity": {
                "type": "SystemAssigned"
            },
            "dependsOn": [
                "[resourceId('Microsoft.Web/serverfarms', parameters('functionAppServicePlanName'))]",
                "[resourceId('Microsoft.Storage/storageAccounts', parameters('storageAccountName'))]"
            ],
            "properties": {
                "serverFarmId": "[resourceId('Microsoft.Web/serverfarms', parameters('functionAppServicePlanName'))]",
                "siteConfig": {
                    "appSettings": [
                        {
                            "name": "AzureWebJobsStorage",
                            "value": "[concat('DefaultEndpointsProtocol=https;AccountName=', parameters('storageAccountName'), ';EndpointSuffix=', environment().suffixes.storage, ';AccountKey=', listKeys(resourceId('Microsoft.Storage/storageAccounts', parameters('storageAccountName')), '2022-09-01').keys[0].value)]"
                        },
                        {
                            "name": "WEBSITE_CONTENTAZUREFILECONNECTIONSTRING",
                            "value": "[concat('DefaultEndpointsProtocol=https;AccountName=', parameters('storageAccountName'), ';EndpointSuffix=', environment().suffixes.storage, ';AccountKey=', listKeys(resourceId('Microsoft.Storage/storageAccounts', parameters('storageAccountName')), '2022-09-01').keys[0].value)]"
                        },
                        {
                            "name": "WEBSITE_CONTENTSHARE",
                            "value": "[toLower(parameters('functionAppName'))]"
                        },
                        {
                            "name": "FUNCTIONS_EXTENSION_VERSION",
                            "value": "~4"
                        },
                        {
                            "name": "FUNCTIONS_WORKER_RUNTIME",
                            "value": "dotnet"
                        },
                        {
                            "name": "RESOURCE_GROUP",
                            "value": "[resourceGroup().name]"
                        },
                        {
                            "name": "VM_NAME",
                            "value": "[variables('vmName')]"
                        }
                    ],
                    "netFrameworkVersion": "v6.0"
                },
                "httpsOnly": true
            },
            "resources": []
        },
        {
            "type": "Microsoft.Web/sites/functions",
            "apiVersion": "2022-03-01",
            "name": "[concat(parameters('functionAppName'), '/StartVM')]",
            "dependsOn": [
                "[resourceId('Microsoft.Web/sites', parameters('functionAppName'))]"
            ],
            "properties": {
                "config": {
                    "bindings": [
                        {
                            "authLevel": "function",
                            "type": "httpTrigger",
                            "direction": "in",
                            "name": "req",
                            "methods": [
                                "get",
                                "post"
                            ]
                        },
                        {
                            "type": "http",
                            "direction": "out",
                            "name": "response"
                        }
                    ]
                },
                "files": {
                    "run.csx": "using System;\nusing System.Net.Http;\nusing System.Net.Http.Headers;\nusing System.Threading.Tasks;\nusing Microsoft.AspNetCore.Mvc;\nusing Microsoft.Extensions.Logging;\nusing Newtonsoft.Json;\nusing System.Text;\n\npublic static async Task<IActionResult> Run(HttpRequest req, ILogger log)\n{\n    log.LogInformation(\"Starting VM function executed\");\n    \n    try\n    {\n        // Get environment variables\n        string resourceGroup = Environment.GetEnvironmentVariable(\"RESOURCE_GROUP\");\n        string vmName = Environment.GetEnvironmentVariable(\"VM_NAME\");\n        string subscriptionId = Environment.GetEnvironmentVariable(\"WEBSITE_OWNER_NAME\").Split('+')[0];\n        \n        // Use DefaultAzureCredential simplified approach\n        string url = $\"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}/start?api-version=2023-03-01\";\n        \n        using (var client = new HttpClient())\n        {\n            // Get token from MSI endpoint\n            var idEndpoint = Environment.GetEnvironmentVariable(\"IDENTITY_ENDPOINT\");\n            var idHeader = Environment.GetEnvironmentVariable(\"IDENTITY_HEADER\");\n            \n            // Log MSI environment info for debugging\n            log.LogInformation($\"Identity Endpoint: {idEndpoint != null}\");\n            log.LogInformation($\"Identity Header: {idHeader != null}\");\n            \n            // Get token\n            using (var tokenRequest = new HttpRequestMessage(HttpMethod.Get, \n                $\"{idEndpoint}?resource=https://management.azure.com&api-version=2019-08-01\"))\n            {\n                tokenRequest.Headers.Add(\"X-IDENTITY-HEADER\", idHeader);\n                \n                var tokenResponse = await client.SendAsync(tokenRequest);\n                string tokenJson = await tokenResponse.Content.ReadAsStringAsync();\n                \n                // Log token response status\n                log.LogInformation($\"Token request status: {tokenResponse.StatusCode}\");\n                \n                if (!tokenResponse.IsSuccessStatusCode)\n                {\n                    log.LogError($\"Token error: {tokenJson}\");\n                    return new ContentResult { \n                        Content = $\"Failed to get token: {tokenResponse.StatusCode}\", \n                        StatusCode = 500 \n                    };\n                }\n                \n                // Parse token\n                var tokenData = JsonConvert.DeserializeObject<dynamic>(tokenJson);\n                string accessToken = tokenData.access_token;\n                \n                // Start VM\n                using (var vmRequest = new HttpRequestMessage(HttpMethod.Post, url))\n                {\n                    vmRequest.Headers.Authorization = new AuthenticationHeaderValue(\"Bearer\", accessToken);\n                    var response = await client.SendAsync(vmRequest);\n                    \n                    string content = await response.Content.ReadAsStringAsync();\n                    log.LogInformation($\"VM start response: {response.StatusCode}\");\n                    \n                    if (response.IsSuccessStatusCode)\n                    {\n                        return new OkObjectResult($\"VM {vmName} start initiated successfully\");\n                    }\n                    else\n                    {\n                        log.LogError($\"VM start error: {content}\");\n                        return new ContentResult { \n                            Content = $\"Failed to start VM: {content}\", \n                            StatusCode = (int)response.StatusCode \n                        };\n                    }\n                }\n            }\n        }\n    }\n    catch (Exception ex)\n    {\n        log.LogError($\"Exception: {ex.Message}\");\n        log.LogError($\"Stack trace: {ex.StackTrace}\");\n        return new ContentResult { \n            Content = $\"Error: {ex.Message}\", \n            StatusCode = 500 \n        };\n    }\n}",
                    "function.proj": "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>netstandard2.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.Azure.Management.Compute\" Version=\"29.1.0\" />\n    <PackageReference Include=\"Microsoft.Azure.Services.AppAuthentication\" Version=\"1.6.2\" />\n    <PackageReference Include=\"Microsoft.NET.Sdk.Functions\" Version=\"3.0.13\" />\n  </ItemGroup>\n</Project>"
                }
            }
        },
        {
            "type": "Microsoft.Authorization/roleAssignments",
            "apiVersion": "2022-04-01",
            "name": "[variables('roleAssignmentName')]",
            "dependsOn": [
                "[resourceId('Microsoft.Web/sites', parameters('functionAppName'))]",
                "[resourceId('Microsoft.Compute/virtualMachines', variables('vmName'))]"
            ],
            "properties": {
                "roleDefinitionId": "[concat('/subscriptions/', subscription().subscriptionId, '/providers/Microsoft.Authorization/roleDefinitions/', variables('roleDefinitionId'))]",
                "principalId": "[reference(resourceId('Microsoft.Web/sites', parameters('functionAppName')), '2022-03-01', 'full').identity.principalId]",
                "principalType": "ServicePrincipal"
            }
        }
    ]
}