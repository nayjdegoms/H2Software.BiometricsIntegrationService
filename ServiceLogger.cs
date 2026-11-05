
using System;
using System.IO;

namespace H2Software.BiometricsIntegrationService
{
    public static class ServiceLogger
    {
        private static readonly object _lock = new object();

        private static readonly string LogDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                "H2Software",
                "BiometricsIntegrationService");

        private static readonly string LogFile =
            Path.Combine(LogDirectory, "service.log");

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(LogDirectory);

                    File.AppendAllText(
                        LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                        " | " + message + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must not crash the service.
            }
        }

        public static void LogError(string context, Exception ex)
        {
            Log(context + " | " + ex);
        }
    }
}