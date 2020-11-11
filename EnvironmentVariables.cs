using System;

namespace Microsoft.Azure.Edge.Test
{
    static class EnvironmentVariables
    {
        internal static readonly string IotHubHost = Environment.GetEnvironmentVariable("ENV_IOTHUB_HOST");
        internal static readonly string IotHubOwnerSharedAccessKey = Environment.GetEnvironmentVariable("ENV_IOTHUB_OWNER_SHARED_ACCESS_KEY");
        internal static readonly string EdgeHubHost = Environment.GetEnvironmentVariable("ENV_EDGEHUB_HOST");
        internal static readonly string EdgeDeviceId = Environment.GetEnvironmentVariable("ENV_EDGE_DEVICE_ID");
        internal static readonly string LeafDeviceIdPrefix = Environment.GetEnvironmentVariable("ENV_LEAF_DEVICE_ID_PREFIX");
        internal static readonly bool UseNativeMqttClient = Environment.GetEnvironmentVariable("ENV_USE_NATIVE_MQTT_CLIENT") == "ON";
        internal static readonly string CustomPublishTopics = Environment.GetEnvironmentVariable("ENV_PUBLISH_TOPICS");
        internal static readonly string CustomSubscriptionTopics = Environment.GetEnvironmentVariable("ENV_SUBSCRIPTION_TOPICS");
        internal static readonly bool DebugLogOn = Environment.GetEnvironmentVariable("ENV_DEBUG_LOG_ON") == "ON";
        internal static readonly string IoTHubOwnerConnectionString = $"HostName={IotHubHost};SharedAccessKeyName=iothubowner;SharedAccessKey={IotHubOwnerSharedAccessKey}";
    }
}
