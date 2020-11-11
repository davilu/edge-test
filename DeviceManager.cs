using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Microsoft.Azure.Edge.Test
{
    static class DeviceManager
    {
        internal static async Task<Device> RetrieveDeviceAsync() 
        {
            var devices = await RetrieveDevicesAsync(1);
            return devices.First();
        }

        internal static async Task<List<Device>> RetrieveDevicesAsync(int childCount)
        {
            var registryManager = RegistryManager.CreateFromConnectionString(EnvironmentVariables.IoTHubOwnerConnectionString);
            var parentEdgeDevice = await RetrieveOrCreateDeviceAsync(registryManager, EnvironmentVariables.EdgeDeviceId, true);
            var list = new List<Device>();
            for (int i = 1; i <= childCount; i++)
            {
                var child = await RetrieveOrCreateDeviceAsync(registryManager, $"{EnvironmentVariables.LeafDeviceIdPrefix}_{i}", scope: parentEdgeDevice.Scope);
                list.Add(child);
            }

            return list;
        }

        internal static string CreateDeviceSasToken(string host, string deviceId, string sharedAccessKey, int timeToLive)
        {
            ConsoleLogger.LogDebug($"Creating token for {deviceId} with TTL {timeToLive}s.");
            var builder = new SharedAccessSignatureBuilder()
            {
                Key = sharedAccessKey,
                TimeToLive = TimeSpan.FromSeconds(timeToLive),
                Target = $"{host}/devices/{WebUtility.UrlEncode(deviceId)}"
            };

            return builder.ToSignature();
        }

        private static async Task<Device> RetrieveOrCreateDeviceAsync(RegistryManager registryManager, string deviceId, bool isEdge = false, string scope = null)
        {
            try
            {
                var existing = await registryManager.GetDeviceAsync(deviceId);
                if (existing != null)
                {
                    ConsoleLogger.LogInfo($"Retrieved device: [id={deviceId}, isEdge={existing.Capabilities?.IotEdge}, scope={existing.Scope}]");
                    return existing;
                }
            }
            catch (Exception ex)
            {
                ConsoleLogger.LogInfo($"Error retrieving device {deviceId}:{ex}");
            }

            ConsoleLogger.LogInfo($"Creating device {deviceId}...");
            var creating = new Device(deviceId);
            if (scope != null)
            {
                creating.Scope = scope;
            }

            if (isEdge)
            {
                creating.Capabilities = new DeviceCapabilities()
                {
                    IotEdge = true
                };
            }

            var created = await registryManager.AddDeviceAsync(creating);
            ConsoleLogger.LogInfo($"Created device: [id={deviceId}, isEdge={created.Capabilities?.IotEdge}, scope={created.Scope}]");
            return created;
        }

    }
}
