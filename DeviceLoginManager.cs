
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml;
using SmackBio.WebSocketSDK;
using SmackBio.WebSocketSDK.Cmd;
using SmackBio.WebSocketSDK.M50.Event;

namespace H2Software.BiometricsIntegrationService
{
    public class DeviceLoginManager : IDeviceLoginManager
    {
        private static readonly AttendanceApiForwarder _apiForwarder =
            new AttendanceApiForwarder();

        // Keep a registration token for each device.
        private static readonly object _monitor = new object();

        private static readonly Dictionary<string, string> _registeredDevices =
            new Dictionary<string, string>();

        public bool Verify(string deviceId, string token)
        {
            ServiceLogger.Log(
                "Device verification requested. Device ID: " + deviceId);

            // Matches the original SDK sample.
            return true;
        }

        public string GetRegisterInfo(string model, string deviceId)
        {
            lock (_monitor)
            {
                if (!_registeredDevices.TryGetValue(
                    deviceId, out string token))
                {
                    token = Guid.NewGuid().ToString();
                    _registeredDevices.Add(deviceId, token);

                    ServiceLogger.Log(
                        "New registration token generated. Model: "
                        + model + ", Device ID: " + deviceId);
                }
                else
                {
                    ServiceLogger.Log(
                        "Existing registration token reused. Device ID: "
                        + deviceId);
                }

                return token;
            }
        }

        public void OnDeviceEvent(XmlDocument xml)
        {
            ServiceLogger.Log(
                "OnDeviceEvent callback was invoked.");

            try
            {
                if (xml == null || xml.DocumentElement == null)
                {
                    ServiceLogger.Log(
                        "Received an empty biometric event.");
                    return;
                }

                ServiceLogger.Log(
                    "Device event XML received: " + xml.OuterXml);

                if (!BaseMessage.IsEventKey(xml, EvtTimeLog.EVT_KEY) &&
                    !BaseMessage.IsEventKey(xml, EvtTimeLog_v2.EVT_KEY))
                {
                    ServiceLogger.Log(
                        "Received a non-attendance device event.");
                    return;
                }

                EvtTimeLog evt = new EvtTimeLog();

                if (!evt.Parse(xml))
                {
                    ServiceLogger.Log(
                        "Could not parse biometric attendance XML.");
                    return;
                }

                long employeeId = evt.log.emp_id;
                DateTime punchTime = evt.log.time;
                string deviceStatus = evt.log.attend_status;

                ServiceLogger.Log(
                    "Punch received. Employee: " + employeeId
                    + ", Time: "
                    + punchTime.ToString("yyyy-MM-dd HH:mm:ss")
                    + ", Status: " + deviceStatus);

                Task.Run(async () =>
                {
                    try
                    {
                        bool sent = await _apiForwarder.ForwardEventAsync(
                            employeeId,
                            punchTime,
                            deviceStatus);

                        ServiceLogger.Log(
                            "Attendance API forwarding result: " + sent
                            + ". Employee: " + employeeId
                            + ", Status: " + deviceStatus);
                    }
                    catch (Exception ex)
                    {
                        ServiceLogger.LogError(
                            "Unexpected attendance forwarding error", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Device event processing error", ex);
            }
        }
    }
}