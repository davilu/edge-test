using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Transport.Mqtt;
using Microsoft.Azure.Devices.Shared;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using D2CMessage = Microsoft.Azure.Devices.Client.Message;
using C2DMessage = Microsoft.Azure.Devices.Message;
using TransportType = Microsoft.Azure.Devices.Client.TransportType;
using System.Threading;

namespace WeatherStation_Console
{
    class DeviceApp
    {
        private const string MethodName = "test-method";
        private const int ChildCount = 5;
        private const string PropertyName = "Name";
        private const string PropertyValue = "Value";

        private static readonly string IotHubHost = Environment.GetEnvironmentVariable("ENV_IOTHUB_HOST");
        private static readonly string IotHubOwnerSharedAccessKey = Environment.GetEnvironmentVariable("ENV_IOTHUB_OWNER_SHARED_ACCESS_KEY");
        private static readonly string EdgeHubHost = Environment.GetEnvironmentVariable("ENV_EDGEHUB_HOST");
        private static readonly string EdgeDeviceId = Environment.GetEnvironmentVariable("ENV_EDGE_DEVICE_ID");
        private static readonly string LeafDeviceIdPrefix = Environment.GetEnvironmentVariable("ENV_LEAF_DEVICE_ID_PREFIX");
        private static readonly bool DebugLogOn = Environment.GetEnvironmentVariable("ENV_DEBUG_LOG_ON") == "ON";

        private static readonly Dictionary<string, int> D2COperationCounts = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> C2DOperationCounts = new Dictionary<string, int>();
        private static readonly TimeSpan OperationTimeOut = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TestPeriod = TimeSpan.FromHours(72); 
        private static readonly TimeSpan TestFrequency = TimeSpan.FromSeconds(30);
        private static readonly string IoTHubOwnerConnectionString = $"HostName={IotHubHost};SharedAccessKeyName=iothubowner;SharedAccessKey={IotHubOwnerSharedAccessKey}";
        private static readonly TimeSpan TokenTTL = TimeSpan.FromMinutes(30);

        public static async Task Main(string[] _)
        {
            DeviceApp d = new DeviceApp();
            LogInfo($"GatewayHost={EdgeHubHost}, ParentEdgeDeviceId={EdgeDeviceId}, LeafDevicePrefix={LeafDeviceIdPrefix}.");
            await d.RunAsync();
        }

        public async Task RunAsync()
        {
            var cancellationTokenSource = new CancellationTokenSource(TestPeriod);
            var devices = await RetrieveDevicesAsync();
            var tasks = new List<Task>();
            var serviceClient = ServiceClient.CreateFromConnectionString(IoTHubOwnerConnectionString);
            await serviceClient.OpenAsync();
            var deviceClients = new List<DeviceClient>();

            foreach (var device in devices)
            {
                var deviceClient = await CreateDeviceClientAsync(device);
                deviceClients.Add(deviceClient);
                tasks.Add(D2CLoop(deviceClient, device.Id, cancellationTokenSource.Token));
                tasks.Add(C2DLoop(serviceClient, deviceClient, device.Id, cancellationTokenSource.Token));
            }

            await Task.WhenAll(tasks);
            await serviceClient.CloseAsync();

            foreach (var deviceClient in deviceClients)
            {
                deviceClient.Dispose();
            }
        }

        private static async Task<DeviceClient> CreateDeviceClientAsync(Device device)
        {
            var deviceId = device.Id;
            D2COperationCounts[deviceId] = 0;
            C2DOperationCounts[deviceId] = 0;
            LogInfo($"Device {device.Id} started");
            var tokenRefresher = new DeviceAuthenticationWithSharedAccessKey(IotHubHost, device.Id, device.Authentication.SymmetricKey.PrimaryKey, TokenTTL);
            ITransportSettings transportSetting;
            if (deviceId.GetHashCode()%2 == 0)
            {
                transportSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only)
                {
                    RemoteCertificateValidationCallback = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true
                };
            }
            else
            {
                transportSetting = new AmqpTransportSettings(TransportType.Amqp_Tcp_Only)
                {
                    RemoteCertificateValidationCallback = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true
                };
            }

            LogInfo($"Device {device.Id} transportType: {transportSetting.GetTransportType()}.");

            var deviceClient = DeviceClient.Create(IotHubHost, EdgeHubHost, tokenRefresher, new ITransportSettings[] { transportSetting });
            deviceClient.SetConnectionStatusChangesHandler((state, reason) => 
                { LogInfo($"Device {device.Id} Connection state change: state={state}, reason={reason}"); });
            await deviceClient.SetDesiredPropertyUpdateCallbackAsync((desiredProperties, context) =>
                {
                    LogInfo($"Device {device.Id} Desired properties change: desiredProperties={desiredProperties.ToJson()}");
                    return Task.CompletedTask;
                }, 
                deviceClient);
            await deviceClient.SetMethodHandlerAsync(
                MethodName,
                (request, _) =>
                {
                    var echoResponse = new MethodResponse(request.Data, 200);
                    return Task.FromResult(echoResponse);
                },
                deviceClient);
            return deviceClient;
        }

        private static async Task D2CLoop(DeviceClient deviceClient, string deviceId, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var index = D2COperationCounts[deviceId];
                var identity = $"[Device={deviceId}, Index={index}, Direction=D2C]";
                LogDebug($"{identity}: Enter D2C loop...");

                var operationName = "UpdatReportedProperties";
                try
                {
                    var name = $"Name: {identity}";
                    var value = $"Value: {identity}";
                    var reportedProperties = new TwinCollection();
                    reportedProperties[PropertyName] = name;
                    reportedProperties[PropertyValue] = value;
                
                    await deviceClient.UpdateReportedPropertiesAsync(reportedProperties);
                    LogDebug($"{identity}: Updated reported properties successfully.");

                    operationName = "GetTwin";
                    var twin = await deviceClient.GetTwinAsync();
                    var retrievedName = twin.Properties.Reported[PropertyName];
                    var retrievedValue = twin.Properties.Reported[PropertyValue];

                    if (retrievedName == name && retrievedValue == value)
                    {
                        LogDebug($"{identity}: Get twin successfully.");
                    }
                    else
                    {
                        LogInfo($"{identity}: Get twin failed, expected ({PropertyName}, {PropertyValue})=({name}, {value}) but was ({retrievedName}, {retrievedValue}).");
                    }

                    operationName = "SendTelemetryMessage";
                    var messagePayload = $"Telemetry: {identity}";
                    var message = new D2CMessage(Encoding.UTF8.GetBytes(messagePayload))
                    {
                        ContentEncoding = "utf-8",
                        ContentType = "application/json"
                    };
                    message.Properties.Add("MachineName", deviceId);

                    await deviceClient.SendEventAsync(message);
                    LogDebug($"{identity}: Sent message successfully.");
                }
                catch (Exception ex)
                {
                    LogInfo($"{identity}: Operation {operationName} failed: {ex}");
                }
                finally
                {
                    if ((index + 1) % 100 == 0)
                    {
                        LogInfo($"{identity}: finished {index + 1} D2C loop.");
                    }

                    D2COperationCounts[deviceId] = index + 1;
                    LogDebug($"{identity}: Exit D2C loop.");
                    await Task.Delay(TestFrequency);
                }
            }
        }

        private static async Task C2DLoop(ServiceClient serviceClient, DeviceClient deviceClient, string deviceId, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var index = C2DOperationCounts[deviceId];
                var identity = $"[Device={deviceId}, Index={index}, Direction=C2D]";
                LogDebug($"{identity}: Enter C2D loop...");

                var operationName = "SendC2DMessage";
                var messagePayload = $"C2D message: {identity}";
                var message = new C2DMessage(Encoding.UTF8.GetBytes(messagePayload));
                try
                {
                    await serviceClient.SendAsync(deviceId, message);
                    LogDebug($"{identity}: Sent C2D message successfully.");

                    operationName = "ReceiveC2DMessage";
                    var received = await deviceClient.ReceiveAsync(OperationTimeOut);
                    while (received != null)
                    {
                        var content = Encoding.UTF8.GetString(received.GetBytes());
                        LogDebug($"{identity}: Received C2D message successfully.");
                        try
                        {
                            await deviceClient.CompleteAsync(received);
                        }
                        catch (Exception e)
                        {
                            // swallow CompleteAsync failure 
                            LogInfo($"{identity}: Complete C2D message failed: {e}.");
                        }

                        if (content == messagePayload)
                        {
                            // quit loop when message is received
                            break;
                        }
                    }

                    if (message == null)
                    {
                        LogInfo($"{identity}: Receive C2D message failed: not received.");
                    }
                    else
                    {
                        LogDebug($"{identity}: Receive C2D message successfully.");
                    }

                    operationName = "InvokeDeviceMethod";
                    var methodPayload = new TwinCollection();
                    methodPayload["Operation"] = $"Operation: {identity}";
                    methodPayload["Args"] = $"Args: {identity}";
                    var methodRequest = new CloudToDeviceMethod(MethodName);
                    methodRequest.SetPayloadJson(methodPayload.ToJson());
                    var methodResponse = await serviceClient.InvokeDeviceMethodAsync(deviceId, methodRequest);
                    var status = methodResponse.Status;
                    LogDebug($"{identity}: Invoke method response: status={status}, payload={methodResponse.GetPayloadAsJson()}.");
                    if (status != 200)
                    {
                        LogInfo($"{identity}: Invoke method failed: status={status}, payload={methodResponse.GetPayloadAsJson()}.");
                    }
                }
                catch (Exception ex)
                {
                    LogInfo($"{identity}: Operation {operationName} failed: {ex}");
                }
                finally
                {
                    if ((index + 1) % 100 == 0)
                    {
                        LogInfo($"{identity}: finished {index + 1} C2D loop.");
                    }

                    C2DOperationCounts[deviceId] = index + 1;
                    LogDebug($"{identity}: Exit C2D loop.");
                    await Task.Delay(TestFrequency);
                }
            }
        }

        private static void LogDebug(string log)
        {
            if (DebugLogOn)
            {
                LogInfo(log);
            }
        }

        private static void LogInfo(string log)
        {
            Console.WriteLine($"{DateTime.Now} - {log}");
        }

        private async Task<List<Device>> RetrieveDevicesAsync()
        {
            var registryManager = RegistryManager.CreateFromConnectionString(IoTHubOwnerConnectionString);
            var parentEdgeDevice = await this.RetrieveOrCreateDeviceAsync(registryManager, EdgeDeviceId, true);
            var list = new List<Device>();
            for (int i = 1; i <= ChildCount; i++)
            {
                var child = await this.RetrieveOrCreateDeviceAsync(registryManager, $"{LeafDeviceIdPrefix}_{i}", scope: parentEdgeDevice.Scope);
                list.Add(child);
            }

            return list;                
        }

        async Task<Device> RetrieveOrCreateDeviceAsync(RegistryManager registryManager, string deviceId, bool isEdge = false, string scope = null)
        {
            try
            {
                var existing = await registryManager.GetDeviceAsync(deviceId);
                if (existing != null)
                {
                    LogInfo($"Retrieved device: [id={deviceId}, isEdge={existing.Capabilities?.IotEdge}, scope={existing.Scope}]");
                    return existing;
                }
            }
            catch (Exception ex)
            {
                LogInfo($"Error retrieving device {deviceId}:{ex}");
            }

            LogInfo($"Creating device {deviceId}...");
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
            LogInfo($"Created device: [id={deviceId}, isEdge={created.Capabilities?.IotEdge}, scope={created.Scope}]");
            return created;
        }

        private class DeviceAuthenticationWithSharedAccessKey : DeviceAuthenticationWithTokenRefresh
        {
            private string host;
            private string deviceId;
            private string sharedAccessKey;

            internal DeviceAuthenticationWithSharedAccessKey(string host, string deviceId, string sharedAccessKey, TimeSpan timeToLive, int timeBufferPercentage = 20) : base(deviceId, Convert.ToInt32(timeToLive.TotalSeconds), timeBufferPercentage)
            {
                this.host = host;
                this.deviceId = deviceId;
                this.sharedAccessKey = sharedAccessKey;
            }

            protected override Task<string> SafeCreateNewToken(string iotHub, int suggestedTimeToLive)
            {
                LogDebug($"Creating token for {deviceId} with TTL {suggestedTimeToLive}s.");
                var builder = new SharedAccessSignatureBuilder()
                {
                    Key = sharedAccessKey,
                    TimeToLive = TimeSpan.FromSeconds(suggestedTimeToLive),
                    Target = $"{host}/devices/{WebUtility.UrlEncode(deviceId)}"
                };

                return Task.FromResult(builder.ToSignature());
            }
        }
    }
}
