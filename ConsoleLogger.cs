using System;

namespace Microsoft.Azure.Edge.Test
{
    class ConsoleLogger
    {
        internal static void LogDebug(string log)
        {
            if (EnvironmentVariables.DebugLogOn)
            {
                LogInfo(log);
            }
        }

        internal static void LogInfo(string log)
        {
            Console.WriteLine($"{DateTime.Now} - {log}");
        }
    }
}
