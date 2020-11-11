using System.Threading.Tasks;

namespace Microsoft.Azure.Edge.Test
{
    class DeviceApp
    {
        public static Task Main(string[] _)
        {
            if (EnvironmentVariables.UseNativeMqttClient)
            {
                return new MqttBrokerRouteRunner().RunAsync();
            }
            else
            {
                return new IoTHubPrimitivesRunner().RunAsync();
            }
        }
    }
}
