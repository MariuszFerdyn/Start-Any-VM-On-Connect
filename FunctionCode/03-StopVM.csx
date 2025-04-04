#r "Newtonsoft.Json"
#r "System.Net.Http"

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

public static async Task Run(TimerInfo myTimer, ILogger log)
{
    log.LogInformation($"AutoStopVM function executed at: {DateTime.Now}");
    
    // Create a StringBuilder to collect all debug information
    var debugInfo = new StringBuilder();
    debugInfo.AppendLine($"AutoStopVM Execution - {DateTime.Now}");
    debugInfo.AppendLine("=======================================");
    
    try
    {
        // Get environment variables
        string resourceGroup = Environment.GetEnvironmentVariable("RESOURCE_GROUP");
        string vmName = Environment.GetEnvironmentVariable("VM_NAME");
        string containerAppName = Environment.GetEnvironmentVariable("CONTAINER_APP_NAME");
        string subscriptionId = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID") ?? 
                               Environment.GetEnvironmentVariable("WEBSITE_OWNER_NAME")?.Split('+')[0];
        
        // Log environment variables
        debugInfo.AppendLine("Environment Variables:");
        debugInfo.AppendLine($"- Resource Group: {resourceGroup ?? "NOT SET"}");
        debugInfo.AppendLine($"- VM Name: {vmName ?? "NOT SET"}");
        debugInfo.AppendLine($"- Container App Name: {containerAppName ?? "NOT SET"}");
        debugInfo.AppendLine($"- Subscription ID: {subscriptionId ?? "NOT SET"}");
        log.LogInformation(debugInfo.ToString());
        
        // Check if valid subscription ID is available
        if (string.IsNullOrEmpty(subscriptionId))
        {
            log.LogError("No valid subscription ID found in environment variables");
            return;
        }
        
        // Get access token for ARM API
        debugInfo.Clear();
        debugInfo.AppendLine("Retrieving access token...");
        string accessToken = await GetAccessToken(log, debugInfo);
        log.LogInformation(debugInfo.ToString());
        
        if (string.IsNullOrEmpty(accessToken))
        {
            log.LogError("Failed to get access token");
            return;
        }
        
        // Check container app scaling status with detailed debugging
        debugInfo.Clear();
        debugInfo.AppendLine($"Checking if container app {containerAppName} is scaled to zero...");
        var (isScaledToZero, scaleDebugInfo) = await CheckContainerAppScaling(
            subscriptionId, resourceGroup, containerAppName, accessToken, log);
        
        debugInfo.Append(scaleDebugInfo);
        log.LogInformation(debugInfo.ToString());
        
        if (isScaledToZero)
        {
            log.LogInformation($"Container app {containerAppName} is scaled to zero. Stopping VM {vmName}.");
            
            // Stop the VM
            await StopVirtualMachine(subscriptionId, resourceGroup, vmName, accessToken, log);
        }
        else
        {
            log.LogInformation($"Container app {containerAppName} is not scaled to zero. VM {vmName} will continue running.");
        }
    }
    catch (Exception ex)
    {
        log.LogError($"Error in AutoStopVM function: {ex.Message}");
        log.LogError($"Stack trace: {ex.StackTrace}");
    }
}

private static async Task<string> GetAccessToken(ILogger log, StringBuilder debugInfo)
{
    try
    {
        var idEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var idHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
        
        debugInfo.AppendLine($"- Identity Endpoint: {(string.IsNullOrEmpty(idEndpoint) ? "NOT SET" : "Available")}");
        debugInfo.AppendLine($"- Identity Header: {(string.IsNullOrEmpty(idHeader) ? "NOT SET" : "Available")}");
        
        if (string.IsNullOrEmpty(idEndpoint) || string.IsNullOrEmpty(idHeader))
        {
            debugInfo.AppendLine("  ERROR: Managed identity endpoint or header not found");
            return null;
        }
        
        using (var client = new HttpClient())
        {
            debugInfo.AppendLine("- Requesting token from managed identity endpoint...");
            
            using (var request = new HttpRequestMessage(HttpMethod.Get, 
                $"{idEndpoint}?resource=https://management.azure.com&api-version=2019-08-01"))
            {
                request.Headers.Add("X-IDENTITY-HEADER", idHeader);
                
                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();
                
                debugInfo.AppendLine($"  Response status: {response.StatusCode}");
                
                if (!response.IsSuccessStatusCode)
                {
                    debugInfo.AppendLine($"  ERROR: Failed to get token: {response.StatusCode}, {content}");
                    return null;
                }
                
                var tokenData = JsonConvert.DeserializeObject<dynamic>(content);
                debugInfo.AppendLine("  Token successfully retrieved");
                return tokenData.access_token;
            }
        }
    }
    catch (Exception ex)
    {
        debugInfo.AppendLine($"  ERROR: Exception getting access token: {ex.Message}");
        return null;
    }
}

private static async Task<(bool IsScaledToZero, string DebugInfo)> CheckContainerAppScaling(
    string subscriptionId, string resourceGroup, string containerAppName, string accessToken, ILogger log)
{
    var debugInfo = new StringBuilder();
    try
    {
        // API URL for the Container App
        string url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.App/containerApps/{containerAppName}?api-version=2023-05-01";
        debugInfo.AppendLine($"- API URL: {url}");
        
        using (var client = new HttpClient())
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                
                debugInfo.AppendLine("- Sending request to get container app status...");
                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();
                
                debugInfo.AppendLine($"- Response status: {response.StatusCode}");
                
                if (!response.IsSuccessStatusCode)
                {
                    debugInfo.AppendLine($"- ERROR: Failed to get container app info: {response.StatusCode}");
                    debugInfo.AppendLine($"- Response content: {content}");
                    return (false, debugInfo.ToString());
                }
                
                // Parse the response
                var appData = JObject.Parse(content);
                
                // Extract and log key information
                string provisioningState = appData["properties"]?["provisioningState"]?.ToString();
                var minReplicas = appData["properties"]?["template"]?["scale"]?["minReplicas"]?.Value<int>();
                var maxReplicas = appData["properties"]?["template"]?["scale"]?["maxReplicas"]?.Value<int>();
                string latestRevision = appData["properties"]?["latestRevisionName"]?.ToString();
                
                debugInfo.AppendLine("- Container App Details:");
                debugInfo.AppendLine($"  • Provisioning state: {provisioningState}");
                debugInfo.AppendLine($"  • Min replicas: {minReplicas}");
                debugInfo.AppendLine($"  • Max replicas: {maxReplicas}");
                debugInfo.AppendLine($"  • Latest revision: {latestRevision}");
                
                // Method 1: Look for direct replica count properties
                debugInfo.AppendLine("- Method 1: Direct properties check");
                var mainReplicaCount = appData["properties"]?["replicas"]?.Value<int>();
                debugInfo.AppendLine($"  • replicas property: {(mainReplicaCount.HasValue ? mainReplicaCount.Value.ToString() : "not found")}");
                
                if (mainReplicaCount.HasValue && mainReplicaCount.Value == 0)
                {
                    debugInfo.AppendLine("  ✓ Found direct evidence of zero replicas");
                    return (true, debugInfo.ToString());
                }
                
                // Method 2: Check the latest revision information
                if (!string.IsNullOrEmpty(latestRevision))
                {
                    debugInfo.AppendLine("- Method 2: Latest revision check");
                    string revisionUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.App/containerApps/{containerAppName}/revisions/{latestRevision}?api-version=2023-05-01";
                    debugInfo.AppendLine($"  • Revision API URL: {revisionUrl}");
                    
                    using (var revisionRequest = new HttpRequestMessage(HttpMethod.Get, revisionUrl))
                    {
                        revisionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                        
                        var revisionResponse = await client.SendAsync(revisionRequest);
                        string revisionContent = await revisionResponse.Content.ReadAsStringAsync();
                        
                        debugInfo.AppendLine($"  • Revision response status: {revisionResponse.StatusCode}");
                        
                        if (revisionResponse.IsSuccessStatusCode)
                        {
                            var revisionData = JObject.Parse(revisionContent);
                            var revisionReplicaCount = revisionData["properties"]?["replicas"]?.Value<int>();
                            debugInfo.AppendLine($"  • Revision replicas: {revisionReplicaCount}");
                            
                            if (revisionReplicaCount.HasValue && revisionReplicaCount.Value == 0)
                            {
                                debugInfo.AppendLine("  ✓ Found zero replicas in revision");
                                return (true, debugInfo.ToString());
                            }
                        }
                        else
                        {
                            debugInfo.AppendLine($"  • ERROR: Failed to get revision info: {revisionResponse.StatusCode}");
                        }
                    }
                }
                
                // Method 3: Search for specific properties in JSON
                debugInfo.AppendLine("- Method 3: JSON property search");
                bool foundZeroReplicas = FindPropertyWithValue(appData, "replicas", 0);
                debugInfo.AppendLine($"  • Found 'replicas: 0' in JSON: {foundZeroReplicas}");
                
                if (foundZeroReplicas)
                {
                    debugInfo.AppendLine("  ✓ Found zero replicas through JSON search");
                    return (true, debugInfo.ToString());
                }
                
                // Method 4: String search in raw JSON
                debugInfo.AppendLine("- Method 4: String content search");
                bool hasZeroReplicasString = content.Contains("\"replicas\":0") || 
                                          content.Contains("\"replicaCount\":0");
                debugInfo.AppendLine($"  • Found zero replicas string in content: {hasZeroReplicasString}");
                
                if (hasZeroReplicasString)
                {
                    debugInfo.AppendLine("  ✓ Found zero replicas through string search");
                    return (true, debugInfo.ToString());
                }
                
                // Method 5: Infer from configuration
                debugInfo.AppendLine("- Method 5: Configuration inference");
                bool isScaleToZeroEnabled = minReplicas == 0;
                debugInfo.AppendLine($"  • Scale to zero enabled: {isScaleToZeroEnabled}");
                
                // Log raw content for debugging (truncated to avoid huge logs)
                if (content.Length > 2000)
                {
                    debugInfo.AppendLine($"- Raw content (truncated): {content.Substring(0, 2000)}...");
                }
                
                // Final conclusion - consider scaled to zero if any strong indicators are present
                bool isScaledToZero = foundZeroReplicas || hasZeroReplicasString;
                debugInfo.AppendLine($"- Final conclusion: Container app is {(isScaledToZero ? "SCALED TO ZERO" : "NOT SCALED TO ZERO")}");
                
                return (isScaledToZero, debugInfo.ToString());
            }
        }
    }
    catch (Exception ex)
    {
        debugInfo.AppendLine($"ERROR checking container app scale status: {ex.Message}");
        debugInfo.AppendLine($"Stack trace: {ex.StackTrace}");
        return (false, debugInfo.ToString());
    }
}

// Helper method to find a property with a specific value anywhere in the JSON
private static bool FindPropertyWithValue(JToken token, string propertyName, int value)
{
    if (token is JObject obj)
    {
        foreach (var property in obj.Properties())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) && 
                property.Value.Type == JTokenType.Integer &&
                property.Value.Value<int>() == value)
            {
                return true;
            }
            
            if (FindPropertyWithValue(property.Value, propertyName, value))
            {
                return true;
            }
        }
    }
    else if (token is JArray array)
    {
        foreach (var item in array)
        {
            if (FindPropertyWithValue(item, propertyName, value))
            {
                return true;
            }
        }
    }
    
    return false;
}

private static async Task StopVirtualMachine(string subscriptionId, string resourceGroup, string vmName, 
    string accessToken, ILogger log)
{
    try
    {
        string url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}/deallocate?api-version=2023-03-01";
        
        log.LogInformation($"Stopping VM {vmName} - API URL: {url}");
        
        using (var client = new HttpClient())
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                
                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    log.LogInformation($"Successfully initiated VM {vmName} stop operation");
                }
                else
                {
                    log.LogError($"Failed to stop VM: {response.StatusCode}, {content}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        log.LogError($"Error stopping VM: {ex.Message}");
    }
}