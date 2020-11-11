using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.Edge.Test.Mqtt;

namespace Microsoft.Azure.Edge.Test
{
    internal class MqttBrokerRouteRunner : IConnectionStatusListener, IMessageHandler
    {
        private const string PropertyName = "Name";
        private const string PropertyValue = "Value";
        private const string MethodName = "test-method";

        // Subscription
        private const string C2DMessagesTopicPattern = "devices/{0}/messages/devicebound";
        private const string TwinUpdateTopic = "$iothub/twin/res";
        private const string MethodRequestTopic = "$iothub/methods/POST";

        // Publish topics
        private const string TelemetryTopicPattern = "devices/{0}/messages/events/";
        private const string GetTwinTopicPattern = "$iothub/twin/GET/?$rid={0}";
        private const string UpdateReportedPropertiesTopicPattern = "$iothub/twin/PATCH/properties/reported/?$rid={0}";
        private const string MethodResponseTopicPattern = "$iothub/methods/res/{0}/?$rid={1}";

        private static readonly TimeSpan OperationTimeOut = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TestPeriod = TimeSpan.FromHours(72);
        private static readonly TimeSpan TestFrequency = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan TokenTTL = TimeSpan.FromMinutes(30);

        private string d2cMessagesTopic;
        private string c2dMessagesTopic;
        private IMqttClient mqttClient;
        private Task reconnectTask;
        private int d2cCounts;
        private int c2dCounts;

        public async Task RunAsync()
        {
            ConsoleLogger.LogInfo($"IotHubHost={EnvironmentVariables.IotHubHost}, GatewayHost={EnvironmentVariables.EdgeHubHost}, ParentEdgeDeviceId={EnvironmentVariables.EdgeDeviceId}, LeafDevicePrefix={EnvironmentVariables.LeafDeviceIdPrefix}.");
            var cancellationTokenSource = new CancellationTokenSource(TestPeriod);
            var device = await DeviceManager.RetrieveDeviceAsync();
            var deviceId = device.Id;

            d2cMessagesTopic = string.Format(CultureInfo.InvariantCulture, TelemetryTopicPattern, deviceId);
            c2dMessagesTopic = string.Format(CultureInfo.InvariantCulture, C2DMessagesTopicPattern, deviceId);
            var host = EnvironmentVariables.EdgeHubHost == null ? EnvironmentVariables.IotHubHost : EnvironmentVariables.EdgeHubHost;
            mqttClient = new MQTTnetClient(host, 8883, true);
            mqttClient.RegisterConnectionStatusListener(this);
            mqttClient.RegisterMessageHandler(this);


            var username = $"{host}/{WebUtility.UrlEncode(deviceId)}/?api-version=2018-06-30";
            var password = DeviceManager.CreateDeviceSasToken(host, deviceId, device.Authentication.SymmetricKey.PrimaryKey, Convert.ToInt32(TokenTTL.TotalSeconds));
            
            await mqttClient.ConnectAsync(deviceId, username, password, new CancellationTokenSource(OperationTimeOut).Token);
            var subscriptions = new Dictionary<string, Qos>()
            {
                [$"{c2dMessagesTopic}/#"] = Qos.AtLeastOnce,
                [$"{TwinUpdateTopic}/#"] = Qos.AtMostOnce,
                [$"{MethodRequestTopic}/#"] = Qos.AtMostOnce
            };

            if (EnvironmentVariables.CustomSubscriptionTopics != null)
            {
                var customTopics = EnvironmentVariables.CustomSubscriptionTopics.Split(";");
                foreach (var topic in customTopics)
                {
                    subscriptions[topic] = Qos.AtLeastOnce;
                }
            }
            
            await mqttClient.SubscribeAsync(subscriptions, new CancellationTokenSource(OperationTimeOut).Token);
            await Task.WhenAll(D2CLoop(deviceId, cancellationTokenSource.Token), C2DLoop(deviceId, cancellationTokenSource.Token));
            await mqttClient.DisconnectAsync(new CancellationTokenSource(OperationTimeOut).Token);
        }

        private async Task D2CLoop(string deviceId, CancellationToken token)
        {
            string[] customTopics = null;
            if (EnvironmentVariables.CustomPublishTopics != null)
            {
                customTopics = EnvironmentVariables.CustomPublishTopics.Split(";");
            }
               
            while (!token.IsCancellationRequested)
            {
                var identity = $"[Device={deviceId}, Index={d2cCounts}, Direction=D2C]";
                ConsoleLogger.LogDebug($"{identity}: Enter D2C loop...");
                var cancellationToken = new CancellationTokenSource(OperationTimeOut).Token;

                var operationName = "UpdatReportedProperties";
                try
                {
                    var name = $"Name: {identity}";
                    var value = $"Value: {identity}";
                    var reportedProperties = new TwinCollection();
                    reportedProperties[PropertyName] = name;
                    reportedProperties[PropertyValue] = value;
                    var updateReportedPropertiesTopic = string.Format(CultureInfo.InvariantCulture, UpdateReportedPropertiesTopicPattern, d2cCounts);
                    var updateReportedPropertiesPayload = Encoding.UTF8.GetBytes(reportedProperties.ToJson());
                    await mqttClient.PublishAsync(updateReportedPropertiesTopic, updateReportedPropertiesPayload, null, Qos.AtLeastOnce, cancellationToken);
                    ConsoleLogger.LogDebug($"{identity}: Updated reported properties successfully.");

                    operationName = "GetTwin";
                    var getTwinTopic = string.Format(CultureInfo.InvariantCulture, GetTwinTopicPattern, d2cCounts);
                    await mqttClient.PublishAsync(getTwinTopic, null, null, Qos.AtLeastOnce, cancellationToken);
                    ConsoleLogger.LogDebug($"{identity}: Get twin successfully.");
                    
                    operationName = "SendTelemetryMessage";
                    var messagePayload = Encoding.UTF8.GetBytes($"Telemetry: {identity}");
                    var properties = new Dictionary<string, string>()
                    {
                        ["$.ct"] = "application/json",
                        ["$.ce"] = "utf-8"
                    };
                    await mqttClient.PublishAsync(d2cMessagesTopic, messagePayload, properties, Qos.AtLeastOnce, cancellationToken);
                    ConsoleLogger.LogDebug($"{identity}: Sent message successfully.");

                    if (customTopics != null)
                    {
                        foreach (var topic in customTopics)
                        {
                            var publishPayload = Encoding.UTF8.GetBytes($"Publish: {identity}");
                            await mqttClient.PublishAsync(topic, publishPayload, properties, Qos.AtLeastOnce, cancellationToken);
                            ConsoleLogger.LogDebug($"{identity}: Publish message with topic {topic} successfully.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLogger.LogInfo($"{identity}: Operation {operationName} failed: {ex}");
                }
                finally
                {
                    d2cCounts++;
                    if (d2cCounts % 100 == 0)
                    {
                        ConsoleLogger.LogInfo($"{identity}: finished {d2cCounts} D2C loop.");
                    }

                    ConsoleLogger.LogDebug($"{identity}: Exit D2C loop.");
                    await Task.Delay(TestFrequency);
                }
            }
        }

        private async Task C2DLoop(string deviceId, CancellationToken token)
        {
            var serviceClient = ServiceClient.CreateFromConnectionString(EnvironmentVariables.IoTHubOwnerConnectionString);
            await serviceClient.OpenAsync();

            while (!token.IsCancellationRequested)
            {
                var identity = $"[Device={deviceId}, Index={c2dCounts}, Direction=C2D]";
                ConsoleLogger.LogDebug($"{identity}: Enter C2D loop...");

                var operationName = "SendC2DMessage";
                var messagePayload = $"C2D message: {identity}";
                var message = new Message(Encoding.UTF8.GetBytes(messagePayload));
                try
                {
                    await serviceClient.SendAsync(deviceId, message);
                    ConsoleLogger.LogDebug($"{identity}: Sent C2D message successfully.");

                    operationName = "InvokeDeviceMethod";
                    var methodPayload = new TwinCollection();
                    methodPayload["Operation"] = $"Operation: {identity}";
                    methodPayload["Args"] = $"Args: {identity}";
                    var methodRequest = new CloudToDeviceMethod(MethodName);
                    methodRequest.SetPayloadJson(methodPayload.ToJson());
                    var methodResponse = await serviceClient.InvokeDeviceMethodAsync(deviceId, methodRequest);
                    var status = methodResponse.Status;
                    ConsoleLogger.LogDebug($"{identity}: Invoke method response: status={status}, payload={methodResponse.GetPayloadAsJson()}.");
                    if (status != 200)
                    {
                        ConsoleLogger.LogInfo($"{identity}: Invoke method failed: status={status}, payload={methodResponse.GetPayloadAsJson()}.");
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLogger.LogInfo($"{identity}: Operation {operationName} failed: {ex}");
                }
                finally
                {
                    c2dCounts++;
                    if (c2dCounts % 100 == 0)
                    {
                        ConsoleLogger.LogInfo($"{identity}: finished {c2dCounts} C2D loop.");
                    }

                    ConsoleLogger.LogDebug($"{identity}: Exit C2D loop.");
                    await Task.Delay(TestFrequency);
                }
            }
        }

        public void OnConnected(IMqttClient mqttClient)
        {
            ConsoleLogger.LogInfo("Connected");
        }

        public void OnDisconnected(IMqttClient mqttClient, Exception exception)
        {
            ConsoleLogger.LogInfo("Disconnected");
            if (reconnectTask == null || reconnectTask.Status != TaskStatus.Running)
            {
                reconnectTask = this.mqttClient.ReconnectAsync();
            }
        }

        public async Task<bool> ProcessMessageAsync(string topic, byte[] payload)
        {
            var content = Encoding.UTF8.GetString(payload);
            if (topic.StartsWith(c2dMessagesTopic))
            {
                ConsoleLogger.LogInfo($"Received C2D message: {content}");
            }
            else if (topic.StartsWith(TwinUpdateTopic))
            {
                ConsoleLogger.LogInfo($"Received twin update: {content}");
            }
            else if (topic.StartsWith(MethodRequestTopic))
            {
                var tokens = Regex.Split(topic, "/", RegexOptions.Compiled);
                var rid = tokens[4].Substring(6);
                ConsoleLogger.LogInfo($"Received direct method request: [Name={tokens[3]},Payload={content}]");
                var responseTopic = string.Format(CultureInfo.InvariantCulture, MethodResponseTopicPattern, 200, rid);
                await mqttClient.PublishAsync(responseTopic, payload, null, Qos.AtLeastOnce, new CancellationTokenSource(OperationTimeOut).Token);
            }
            else
            {
                ConsoleLogger.LogInfo($"Received message: [Topic={topic}, Payload={content}].");
            }

            return true;
        }
    }
}
