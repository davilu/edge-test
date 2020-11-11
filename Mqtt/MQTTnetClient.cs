using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Disconnecting;
using MQTTnet.Client.Options;
using MQTTnet.Client.Publishing;
using MQTTnet.Client.Receiving;
using MQTTnet.Client.Subscribing;
using MQTTnet.Client.Unsubscribing;
using MQTTnet.Protocol;

namespace Microsoft.Azure.Edge.Test.Mqtt
{
    public class MQTTnetClient : IMqttClient
    {
        private readonly string host;
        private readonly int port;
        private readonly bool isSsl;
        private readonly MQTTnet.Client.IMqttClient client;
        private readonly MQTTnetApplicationMessageReceivedHandler mqttApplicationMessageReceivedHandler;
        private IConnectionStatusListener connectionStatusListener;

        public MQTTnetClient(string host, int port, bool isSsl)
        {
            this.host = host;
            this.port = port;
            this.isSsl = isSsl;
            client = new MqttFactory().CreateMqttClient();
            mqttApplicationMessageReceivedHandler = new MQTTnetApplicationMessageReceivedHandler();
        }

        public Task ConnectAsync(string clientId, string username, string password, CancellationToken cancellationToken) => ConnectAsync(clientId, username, password, true, default, cancellationToken);

        public async Task ConnectAsync(string clientId, string username, string password, bool cleanSession, TimeSpan keepAlivePeriod, CancellationToken cancellationToken)
        {
            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId(clientId)
                .WithCredentials(username, password)
                .WithCleanSession(cleanSession)
                .WithKeepAlivePeriod(keepAlivePeriod);
            // default is V311, supports V500
            // builder.WithProtocolVersion(MqttProtocolVersion.V500);
            if (isSsl)
            {
                builder.WithTls();
            }

            client.UseConnectedHandler(HandleConnection);
            client.UseDisconnectedHandler(HandleDisconnection);
            client.ApplicationMessageReceivedHandler = mqttApplicationMessageReceivedHandler;

            MqttClientAuthenticateResult connectResult;
            try
            {
                connectResult = await client.ConnectAsync(builder.Build(), cancellationToken);
            }
            catch (Exception e)
            {
                throw new MqttException(cause: e);
            }

            ValidateConnectResult(connectResult);
        }

        public Task ReconnectAsync() => client.ReconnectAsync();

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            client.UseConnectedHandler((Action<MqttClientConnectedEventArgs>)null);
            client.UseDisconnectedHandler((Action<MqttClientDisconnectedEventArgs>)null);
            return client.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);
        }

        public async Task PublishAsync(string topic, byte[] payload, Dictionary<string, string> properties, Qos qos, CancellationToken cancellationToken)
        {
            var mqttMessageBuilder = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MapQos(qos));

            if (properties != null)
            {
                foreach (var entry in properties)
                {
                    mqttMessageBuilder.WithUserProperty(entry.Key, entry.Value);
                }
            }

            MqttClientPublishResult publishResult;
            try
            {
                publishResult = await client.PublishAsync(mqttMessageBuilder.Build(), cancellationToken);
            }
            catch (Exception e)
            {
                throw new MqttException(cause: e);
            }

            ValidatePublishResult(publishResult);
        }

        public async Task<Dictionary<string, Qos>> SubscribeAsync(Dictionary<string, Qos> subscriptions, CancellationToken cancellationToken)
        {
            var builder = new MqttClientSubscribeOptionsBuilder();
            foreach (var entry in subscriptions)
            {
                builder.WithTopicFilter(entry.Key, MapQos(entry.Value));
            }

            try
            {
                var subscribeResult = await client.SubscribeAsync(builder.Build(), cancellationToken);
                var gainedQoses = new Dictionary<string, Qos>();
                foreach (var item in subscribeResult.Items)
                {
                    var gained = (int)item.ResultCode;
                    if (gained < 0 || gained > 2)
                    {
                        throw new MqttException(message: $"Subscribe [topiic={item.TopicFilter.Topic}, qos={item.TopicFilter.QualityOfServiceLevel} failed: [resultCode={item.ResultCode}].");
                    }

                    gainedQoses[item.TopicFilter.Topic] = (Qos)gained;
                }
                return gainedQoses;
            }
            catch (Exception e)
            {
                throw new MqttException(cause: e);
            }
        }

        public Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken)
        {
            var builder = new MqttClientUnsubscribeOptionsBuilder();
            foreach (string topic in topics)
            {
                builder.WithTopicFilter(topic);
            }
            return client.UnsubscribeAsync(builder.Build(), cancellationToken);
        }

        public void RegisterConnectionStatusListener(IConnectionStatusListener connectionStatusListener) => this.connectionStatusListener = connectionStatusListener;

        public void RegisterMessageHandler(IMessageHandler messageHandler) => mqttApplicationMessageReceivedHandler.MessageHandler = messageHandler;

        public bool IsConnected() => client.IsConnected;

        static MqttQualityOfServiceLevel MapQos(Qos qos) => (MqttQualityOfServiceLevel)(int)qos;

        void HandleConnection(MqttClientConnectedEventArgs _) => connectionStatusListener?.OnConnected(this);

        void HandleDisconnection(MqttClientDisconnectedEventArgs disconnectedEvent) => connectionStatusListener?.OnDisconnected(this, disconnectedEvent.Exception);

        void ValidateConnectResult(MqttClientAuthenticateResult authenticateResult)
        {
            if (authenticateResult.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new MqttException(isTemporary: IsTemporaryFailure(authenticateResult.ResultCode), message: $"Authentication failed: [code={authenticateResult.ResultCode}, message={authenticateResult.ReasonString}].");
            }
        }

        void ValidatePublishResult(MqttClientPublishResult publishResult)
        {
            if (publishResult.ReasonCode != MqttClientPublishReasonCode.Success)
            {
                throw new MqttException(isTemporary: IsTemporaryFailure(publishResult.ReasonCode), message: $"Publish failed: [code={publishResult.ReasonCode}, message={publishResult.ReasonString}].");
            }
        }

        bool IsTemporaryFailure(MqttClientConnectResultCode connectResultCode) => connectResultCode == MqttClientConnectResultCode.ServerUnavailable
                || connectResultCode == MqttClientConnectResultCode.ServerBusy
                || connectResultCode == MqttClientConnectResultCode.QuotaExceeded
                || connectResultCode == MqttClientConnectResultCode.ConnectionRateExceeded;

        bool IsTemporaryFailure(MqttClientPublishReasonCode publishResultCode) => publishResultCode == MqttClientPublishReasonCode.QuotaExceeded;
    }

    public class MQTTnetApplicationMessageReceivedHandler : IMqttApplicationMessageReceivedHandler
    {
        public IMessageHandler MessageHandler { set; get; }

        public async Task HandleApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs eventArgs)
        {
            var messageHandler = MessageHandler;
            if (messageHandler == null)
            {
                eventArgs.ProcessingFailed = true;
            }
            else
            {
                var processed = await messageHandler.ProcessMessageAsync(eventArgs.ApplicationMessage.Topic, eventArgs.ApplicationMessage.Payload);
                eventArgs.ProcessingFailed = !processed;
            }
        }
    }
}
