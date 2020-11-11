using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Edge.Test.Mqtt
{
    public interface IMqttClient
    {
        public Task ConnectAsync(string clientId, string username, string password, CancellationToken cancellationToken);
        public Task ConnectAsync(string clientId, string username, string password, bool cleanSession, TimeSpan keepAlivePeriod, CancellationToken cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken);
        public Task PublishAsync(string topic, byte[] payload, Dictionary<string,string> properties, Qos qos, CancellationToken cancellationToken);
        public Task<Dictionary<string, Qos>> SubscribeAsync(Dictionary<string, Qos> subscriptions, CancellationToken cancellationToken);
        public void RegisterMessageHandler(IMessageHandler messageHandler);
        public Task UnsubscribeAsync(IEnumerable<string> topics, CancellationToken cancellationToken);
        public void RegisterConnectionStatusListener(IConnectionStatusListener connectionStatusListener);
        public bool IsConnected();
        public Task ReconnectAsync();
    }

    public enum Qos
    {
        AtMostOnce = 0,
        AtLeastOnce = 1,
        ExactlyOnce = 2
    }

    public interface IMessageHandler
    {
        Task<bool> ProcessMessageAsync(string topic, byte[] payload);
    }

    public interface IConnectionStatusListener
    {
        public void OnConnected(IMqttClient mqttClient);
        public void OnDisconnected(IMqttClient mqttClient, Exception exception);
    }

    public class MqttException : Exception
    {
        public MqttException(bool isTemporary = false, string message = null, Exception cause = null) : base(message, cause)
        {
            IsTemporary = isTemporary;
        }

        public bool IsTemporary { get; }
    }
}
