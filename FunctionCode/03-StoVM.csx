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
    
    try
    {
        // Get environment variables
        string resourceGroup = Environment.GetEnvironmentVariable("RESOURCE_GROUP");
        string vmName = Environment.GetEnvironmentVariable("VM_NAME");
        string containerAppName = Environment.GetEnvironmentVariable("CONTAINER_APP_NAME");
        string subscriptionId = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID") ?? 
                               Environment.GetEnvironmentVariable("WEBSITE_OWNER_NAME")?.Split('+')[0];
        
        // Check if valid subscription ID is available
        if (string.IsNullOrEmpty(subscriptionId))
        {
            log.LogError("No valid subscription ID found in environment variables");
            return;
        }
        
        // Get access token for ARM API
        string accessToken = await GetAccessToken(log);
        if (string.IsNullOrEmpty(accessToken))
        {
            log.LogError("Failed to get access token");
            return;
        }
        
        // Check container app scaling status
        bool isScaledToZero = await IsContainerAppScaledToZero(subscriptionId, resourceGroup, containerAppName, accessToken, log);
        
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

private static async Task<string> GetAccessToken(ILogger log)
{
    try
    {
        var idEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var idHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
        
        if (string.IsNullOrEmpty(idEndpoint) || string.IsNullOrEmpty(idHeader))
        {
            log.LogError("Managed identity endpoint or header not found");
            return null;
        }
        
        using (var client = new HttpClient())
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, 
                $"{idEndpoint}?resource=https://management.azure.com&api-version=2019-08-01"))
            {
                request.Headers.Add("X-IDENTITY-HEADER", idHeader);
                
                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    log.LogError($"Failed to get token: {response.StatusCode}, {content}");
                    return null;
                }
                
                var tokenData = JsonConvert.DeserializeObject<dynamic>(content);
                return tokenData.access_token;
            }
        }
    }
    catch (Exception ex)
    {
        log.LogError($"Error getting access token: {ex.Message}");
        return null;
    }
}

private static async Task<bool> IsContainerAppScaledToZero(string subscriptionId, string resourceGroup, 
    string containerAppName, string accessToken, ILogger log)
{
    try
    {
        // First, let's check the Container App status via the primary API endpoint
        string url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.App/containerApps/{containerAppName}?api-version=2023-05-01";
        
        using (var client = new HttpClient())
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                
                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    log.LogError($"Failed to get container app info: {response.StatusCode}, {content}");
                    return false;
                }
                
                // Log full response for debugging (careful with sensitive data in production)
                log.LogInformation($"Container app API full response: {content}");
                
                // Parse the response
                var appData = JObject.Parse(content);
                
                // Try different approaches to determine if scaled to zero
                
                // Approach 1: Check if provisioning state is Succeeded and no replicas are reported
                string provisioningState = appData["properties"]?["provisioningState"]?.ToString();
                log.LogInformation($"Provisioning state: {provisioningState}");
                
                // Approach 2: Check if scale rules indicate it's scaled to zero
                var minReplicas = appData["properties"]?["template"]?["scale"]?["minReplicas"]?.Value<int>();
                var maxReplicas = appData["properties"]?["template"]?["scale"]?["maxReplicas"]?.Value<int>();
                log.LogInformation($"Scale settings - minReplicas: {minReplicas}, maxReplicas: {maxReplicas}");
                
                // Approach 3: Check specific revision status
                string latestRevision = appData["properties"]?["latestRevisionName"]?.ToString();
                log.LogInformation($"Latest revision: {latestRevision}");
                
                // Let's try another approach - get revision info directly
                if (!string.IsNullOrEmpty(latestRevision))
                {
                    string revisionUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.App/containerApps/{containerAppName}/revisions/{latestRevision}?api-version=2023-05-01";
                    
                    using (var revisionRequest = new HttpRequestMessage(HttpMethod.Get, revisionUrl))
                    {
                        revisionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                        
                        var revisionResponse = await client.SendAsync(revisionRequest);
                        string revisionContent = await revisionResponse.Content.ReadAsStringAsync();
                        
                        if (revisionResponse.IsSuccessStatusCode)
                        {
                            log.LogInformation($"Revision API response: {revisionContent}");
                            var revisionData = JObject.Parse(revisionContent);
                            var revisionReplicaCount = revisionData["properties"]?["replicas"]?.Value<int>();
                            log.LogInformation($"Revision replica count: {revisionReplicaCount}");
                            
                            if (revisionReplicaCount.HasValue && revisionReplicaCount.Value == 0)
                            {
                                return true;
                            }
                        }
                    }
                }
                
                // Approach 4: Directly search for "replicas" property anywhere in the JSON
                bool foundZeroReplicas = FindPropertyWithValue(appData, "replicas", 0);
                log.LogInformation($"Found zero replicas directly in JSON: {foundZeroReplicas}");
                
                // As a final check, look for any sign of scaling to zero in the JSON
                bool hasScaleToZeroIndicator = 
                    (provisioningState == "Succeeded" && minReplicas == 0) ||
                    foundZeroReplicas ||
                    content.Contains("\"replicas\":0") ||
                    content.Contains("\"replicaCount\":0") ||
                    content.Contains("\"scale to 0\"") ||
                    content.Contains("\"scaled to zero\"");
                
                log.LogInformation($"Scale to zero indicator found: {hasScaleToZeroIndicator}");
                
                return hasScaleToZeroIndicator;
            }
        }
    }
    catch (Exception ex)
    {
        log.LogError($"Error checking container app scale status: {ex.Message}");
        log.LogError($"Stack trace: {ex.StackTrace}");
        return false;
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