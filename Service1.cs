
using SmackBio.WebSocketSDK;
using SuperSocket.SocketBase;
using SuperSocket.SocketEngine;
using SuperWebSocket;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using static H2Software.BiometricsIntegrationService.MaintenanceApiHost;

namespace H2Software.BiometricsIntegrationService
{
    public partial class Service1 : ServiceBase
    {
        private IBootstrap _bootstrap;

        private readonly ConcurrentDictionary<string, SBWebSocketHandler>
            _handlers =
                new ConcurrentDictionary<string, SBWebSocketHandler>();

        private MaintenanceApiHost _maintenanceApi;

        public Service1()
        {
            InitializeComponent();
            ServiceName = "H2SoftwareBiometricsIntegrationService";
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                ServiceLogger.Log("Starting biometrics integration service...");

                _bootstrap = BootstrapFactory.CreateBootstrap();

                if (_bootstrap == null || !_bootstrap.Initialize())
                {
                    throw new InvalidOperationException(
                        "SuperSocket bootstrap initialization failed.");
                }

                var socketServer = _bootstrap.AppServers
                    .FirstOrDefault(s => s.Name == "SuperWebSocket")
                    as WebSocketServer;

                if (socketServer == null)
                {
                    throw new InvalidOperationException(
                        "SuperWebSocket was not found in App.config.");
                }

                socketServer.NewSessionConnected +=
                    SocketServer_NewSessionConnected;

                socketServer.NewMessageReceived +=
                    SocketServer_NewMessageReceived;

                socketServer.SessionClosed +=
                    SocketServer_SessionClosed;

                var startResult = _bootstrap.Start();

                if (socketServer.State != ServerState.Running)
                {
                    throw new InvalidOperationException(
                        "WebSocket server did not start. Start result: "
                        + startResult);
                }

                ServiceLogger.Log(
                    "WebSocket server is listening on port 482.");

                string maintenanceToken =
                    System.Configuration.ConfigurationManager
                        .AppSettings["MaintenanceApiToken"];

                _maintenanceApi = new MaintenanceApiHost(
                    FindActiveDeviceHandler,
                    GetActiveDevices,
                    maintenanceToken);

                _maintenanceApi.Start();

                ServiceLogger.Log(
                    "Maintenance API is listening on 127.0.0.1:5001.");
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Service startup error", ex);

                try
                {
                    _bootstrap?.Stop();
                }
                catch (Exception stopEx)
                {
                    ServiceLogger.LogError(
                        "Error stopping bootstrap after startup failure",
                        stopEx);
                }

                _bootstrap = null;

                throw;
            }
        }

        private void SocketServer_NewSessionConnected(
            WebSocketSession session)
        {
            try
            {
                var handler = new SBWebSocketHandler();

                if (_handlers.TryAdd(session.SessionID, handler))
                {
                    handler.OnOpen(session);

                    ServiceLogger.Log(
                        "Biometric device connected. Session: "
                        + session.SessionID);
                }
                else
                {
                    ServiceLogger.Log(
                        "Duplicate WebSocket session: "
                        + session.SessionID);
                }
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Device connection error", ex);

                try
                {
                    session.Close();
                }
                catch (Exception closeEx)
                {
                    ServiceLogger.LogError(
                        "Error closing failed device session", closeEx);
                }
            }
        }

        private void SocketServer_NewMessageReceived(
            WebSocketSession session,
            string message)
        {
            try
            {
                if (_handlers.TryGetValue(
                    session.SessionID, out var handler))
                {
                    handler.OnMessage(message);
                }
                else
                {
                    ServiceLogger.Log(
                        "Message received for unknown session: "
                        + session.SessionID);
                }
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Device message error", ex);
            }
        }

        private void SocketServer_SessionClosed(
            WebSocketSession session,
            CloseReason reason)
        {
            try
            {
                if (_handlers.TryRemove(
                    session.SessionID, out var handler))
                {
                    handler.OnClose();
                }

                ServiceLogger.Log(
                    "Biometric device disconnected. Session: "
                    + session.SessionID
                    + ", Reason: " + reason);
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Session close error", ex);
            }
        }
        
        internal SBWebSocketHandler FindActiveDeviceHandler(
            string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return _handlers.Values.FirstOrDefault(
                    h => h != null && h.loggedIn);
            }

            return _handlers.Values.FirstOrDefault(
                h => h != null
                && h.loggedIn
                && string.Equals(
                    h.deviceId,
                    deviceId,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal IEnumerable<MaintenanceDeviceInfo>
            GetActiveDevices()
        {
            return _handlers.Values
                .Where(h => h != null && h.loggedIn)
                .Select(h => new MaintenanceDeviceInfo
                {
                    DeviceUid = h.deviceId,
                    SessionId = h.SessionId.ToString()
                })
                .ToList();
        }

        protected override void OnStop()
        {
            ServiceLogger.Log(
                "Stopping biometrics integration service...");

            try
            {
                _maintenanceApi?.Stop();
                _maintenanceApi = null;

                _bootstrap?.Stop();
                _bootstrap = null;

                _handlers.Clear();

                ServiceLogger.Log(
                    "Biometrics integration service stopped.");
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Service shutdown error", ex);
            }
        }
    }
}