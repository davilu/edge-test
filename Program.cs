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
        private static readonly string IotHubHost = Environment.GetEnvironmentVariable("ENV_IOTHUB_HOST");
        private static readonly string IotHubOwnerSharedAccessKey = Environment.GetEnvironmentVariable("ENV_IOTHUB_OWNER_SHARED_ACCESS_KEY");
        private static readonly string EdgeHubHost = Environment.GetEnvironmentVariable("ENV_EDGEHUB_HOST");
        private static readonly string EdgeDeviceId = Environment.GetEnvironmentVariable("ENV_EDGE_DEVICE_ID");
        private static readonly string LeafDeviceIdPrefix = Environment.GetEnvironmentVariable("ENV_LEAF_DEVICE_ID_PREFIX");


        private static readonly TimeSpan OperationTimeOut = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TestPeriod = TimeSpan.FromHours(72); 
        private static readonly TimeSpan TestFrequency = TimeSpan.FromSeconds(30);
        private static readonly string IoTHubOwnerConnectionString = $"HostName={IotHubHost};SharedAccessKeyName=iothubowner;SharedAccessKey={IotHubOwnerSharedAccessKey}";
        private static readonly TimeSpan TokenTTL = TimeSpan.FromMinutes(30);

        public static async Task Main(string[] _)
        {
            DeviceApp d = new DeviceApp();
            Console.WriteLine($"{DateTime.Now}: GatewayHost={EdgeHubHost}, ParentEdgeDeviceId={EdgeDeviceId}, LeafDevicePrefix={LeafDeviceIdPrefix}.");
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
            Console.WriteLine($"{DateTime.Now}: Device {device.Id} started");
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

            Console.WriteLine($"TransportType of {deviceId}: {transportSetting.GetTransportType()}.");

            var deviceClient = DeviceClient.Create(IotHubHost, EdgeHubHost, tokenRefresher, new ITransportSettings[] { transportSetting });
            deviceClient.SetConnectionStatusChangesHandler((state, reason) => 
                { Console.WriteLine($"{deviceId} Connection state change: state={state}, reason={reason}"); });
            await deviceClient.SetDesiredPropertyUpdateCallbackAsync((desiredProperties, context) =>
                { 
                    Console.WriteLine($"{deviceId} Desired properties change: desiredProperties={desiredProperties.ToJson()}");
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
                var name = $"Random name: {Guid.NewGuid()}";
                var value = $"Random value: {Guid.NewGuid()}";
                var reportedProperties = new TwinCollection();
                reportedProperties["Name"] = name;
                reportedProperties["Value"] = value;
                await deviceClient.UpdateReportedPropertiesAsync(reportedProperties);
                Console.WriteLine($"{DateTime.Now} Device:{deviceId} > Update reported properties from {deviceId}: Name={name}, Value={value}");

                var messageString = $"Random Telemetry: {Guid.NewGuid()}";
                var message = new D2CMessage(Encoding.UTF8.GetBytes(messageString))
                {
                    ContentEncoding = "utf-8",
                    ContentType = "application/json"
                };
                message.Properties.Add("MachineName", deviceId);

                await deviceClient.SendEventAsync(message);
                Console.WriteLine($"{DateTime.Now} Device:{deviceId} > Sent message successfully from {deviceId}: {messageString}");
                await Task.Delay(TestFrequency);
            }
        }

        private static async Task C2DLoop(ServiceClient serviceClient, DeviceClient deviceClient, string deviceId, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var messageString = $"Random C2D message: {Guid.NewGuid()}";
                var message = new C2DMessage(Encoding.UTF8.GetBytes(messageString));
                await serviceClient.SendAsync(deviceId, message);
                Console.WriteLine($"{DateTime.Now} Device:{deviceId} < Sent C2D message to {deviceId}: {messageString}");

                var received = await deviceClient.ReceiveAsync(OperationTimeOut);
                if (received != null)
                {
                    Console.WriteLine($"{DateTime.Now} Device:{deviceId} < Received C2D message from cloud: {Encoding.UTF8.GetString(received.GetBytes())}");
                    try
                    {
                        await deviceClient.CompleteAsync(received);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"{DateTime.Now} Device:{deviceId} < Complete C2D message failed: {e}");
                    }
                }

                var payload = new TwinCollection();
                payload["Operation"] = $"Random operation: {Guid.NewGuid()}";
                payload["Args"] = $"Random args: {Guid.NewGuid()}";
                var payloadString = payload.ToJson();
                var methodRequest = new CloudToDeviceMethod(MethodName);
                methodRequest.SetPayloadJson(payloadString);
                var methodResponse = await serviceClient.InvokeDeviceMethodAsync(deviceId, methodRequest);
                Console.WriteLine($"{DateTime.Now} Device:{deviceId} < Invoke method {payloadString} to {deviceId} response: status={methodResponse.Status}, payload={methodResponse.GetPayloadAsJson()}.");
             
                await Task.Delay(TestFrequency);
            }
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
                    Console.WriteLine($"Retrieving device: [id={deviceId}, isEdge={existing.Capabilities?.IotEdge}, scope={existing.Scope}]");
                    return existing;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving device {deviceId}:{ex}");
            }

            Console.WriteLine($"Creating device {deviceId}...");
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
            Console.WriteLine($"Created device: [id={deviceId}, isEdge={created.Capabilities?.IotEdge}, scope={created.Scope}]");
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
                Console.WriteLine($"Creating token for {deviceId} with TTL {suggestedTimeToLive}s.");
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
