using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Collections.Generic;
using SmackBio.WebSocketSDK;

namespace H2Software.BiometricsIntegrationService
{
    public class MaintenanceApiHost
    {
        public class MaintenanceDeviceInfo
        {
            public string DeviceUid { get; set; }

            public string SessionId { get; set; }
        }

        private readonly HttpListener _listener =
            new HttpListener();

        private readonly Func<string, SBWebSocketHandler>
            _handlerResolver;

        private readonly Func<IEnumerable<MaintenanceDeviceInfo>>
            _deviceListResolver;

        private readonly string _token;

        private volatile bool _running;

        public MaintenanceApiHost(
            Func<string, SBWebSocketHandler> handlerResolver,
            Func<IEnumerable<MaintenanceDeviceInfo>> deviceListResolver,
            string token)
        {
            _handlerResolver = handlerResolver;
            _deviceListResolver = deviceListResolver;
            _token = token;

            if (string.IsNullOrWhiteSpace(_token))
                throw new InvalidOperationException(
                    "Maintenance API token is missing.");

            _listener.Prefixes.Add(
                "http://127.0.0.1:5001/");
        }

        public void Start()
        {
            _listener.Start();
            _running = true;

            Task.Run(() => ListenLoopAsync());
        }

        public void Stop()
        {
            _running = false;

            if (_listener.IsListening)
                _listener.Stop();
        }

        private async Task ListenLoopAsync()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext context =
                        await _listener.GetContextAsync();

                    await ProcessRequestAsync(context);
                }
                catch (HttpListenerException)
                {
                    if (!_running)
                        break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ServiceLogger.LogError(
                        "Maintenance API listener error", ex);
                }
            }
        }

        private async Task ProcessRequestAsync(
            HttpListenerContext context)
        {
            try
            {
                string path =
                    context.Request.Url.AbsolutePath.TrimEnd('/');

                if (path == "/api/device/list"
                    && context.Request.HttpMethod == "GET")
                {
                    if (!IsAuthorized(context))
                    {
                        WriteResponse(
                            context, 401, false,
                            "Unauthorized", null);
                        return;
                    }

                    var devices = _deviceListResolver();

                    WriteDeviceListResponse(
                        context,
                        devices);

                    return;
                }

                if (path == "/api/device/status"
                    && context.Request.HttpMethod == "GET")
                {
                    if (!IsAuthorized(context))
                    {
                        WriteResponse(
                            context, 401, false,
                            "Unauthorized", null);
                        return;
                    }

                    var statusHandler = _handlerResolver(null);

                    if (statusHandler == null)
                    {
                        WriteResponse(
                            context, 200, true,
                            "No logged-in device.", null);
                    }
                    else
                    {
                        WriteResponse(
                            context, 200, true,
                            "Device connected.",
                            statusHandler.deviceId);
                    }

                    return;
                }

                if (path != "/api/device/command"
                    || context.Request.HttpMethod != "POST")
                {
                    WriteResponse(
                        context, 404, false,
                        "Endpoint not found.", null);
                    return;
                }

                if (!IsAuthorized(context))
                {
                    WriteResponse(
                        context, 401, false,
                        "Unauthorized", null);
                    return;
                }

                if (context.Request.ContentLength64 > 1048576)
                {
                    WriteResponse(
                        context, 413, false,
                        "Request is too large.", null);
                    return;
                }

                string body;

                using (var reader = new StreamReader(
                    context.Request.InputStream,
                    context.Request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync();
                }

                XmlDocument envelope = new XmlDocument();
                envelope.LoadXml(body);

                string deviceId =
                    envelope.SelectSingleNode(
                        "/MaintenanceRequest/DeviceId")
                    ?.InnerText;

                string commandBase64 =
                    envelope.SelectSingleNode(
                        "/MaintenanceRequest/CommandXml")
                    ?.InnerText;

                if (string.IsNullOrWhiteSpace(commandBase64))
                {
                    WriteResponse(
                        context, 400, false,
                        "Command XML is required.", null);
                    return;
                }

                byte[] commandBytes =
                    Convert.FromBase64String(commandBase64);

                string commandXml =
                    Encoding.UTF8.GetString(commandBytes);

                XmlDocument command = new XmlDocument();
                command.LoadXml(commandXml);

                var handler =
                    _handlerResolver(deviceId);

                if (handler == null)
                {
                    WriteResponse(
                        context, 409, false,
                        "No matching logged-in device was found.",
                        null);
                    return;
                }

                XmlDocument deviceResponse =
                    await Task.Run(() =>
                        MaintenanceCommandManager.ExecuteCommand(
                            handler,
                            command,
                            15000));

                string responseBase64 =
                    Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(
                            deviceResponse.OuterXml));

                WriteResponse(
                    context, 200, true,
                    "Command completed.",
                    responseBase64);
            }
            catch (TimeoutException ex)
            {
                WriteResponse(
                    context, 504, false,
                    ex.Message, null);
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Maintenance command error", ex);

                WriteResponse(
                    context, 500, false,
                    "Maintenance command failed.", null);
            }
        }

        private void WriteDeviceListResponse(
            HttpListenerContext context,
            IEnumerable<MaintenanceDeviceInfo> devices)
        {
            try
            {
                XmlDocument response = new XmlDocument();

                XmlElement root =
                    response.CreateElement("MaintenanceResponse");

                response.AppendChild(root);

                AddElement(response, root,
                    "Success", "true");

                AddElement(response, root,
                    "Message", "Device list retrieved.");

                XmlElement data =
                    response.CreateElement("Data");

                root.AppendChild(data);

                foreach (var device in devices)
                {
                    XmlElement deviceElement =
                        response.CreateElement("Device");

                    data.AppendChild(deviceElement);

                    AddElement(response, deviceElement,
                        "device_uid", device.DeviceUid);

                    AddElement(response, deviceElement,
                        "session_id", device.SessionId);
                }

                byte[] bytes =
                    Encoding.UTF8.GetBytes(response.OuterXml);

                context.Response.StatusCode = 200;

                context.Response.ContentType =
                    "application/xml; charset=utf-8";

                context.Response.ContentLength64 = bytes.Length;

                context.Response.OutputStream.Write(
                    bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Device list response error", ex);
            }
            finally
            {
                context.Response.Close();
            }
        }

        private bool IsAuthorized(
            HttpListenerContext context)
        {
            string supplied =
                context.Request.Headers["X-Maintenance-Token"];

            if (string.IsNullOrEmpty(supplied)
                || supplied.Length != _token.Length)
                return false;

            int difference = 0;

            for (int i = 0; i < _token.Length; i++)
            {
                difference |= supplied[i] ^ _token[i];
            }

            return difference == 0;
        }

        private void WriteResponse(
            HttpListenerContext context,
            int statusCode,
            bool success,
            string message,
            string data)
        {
            try
            {
                XmlDocument response = new XmlDocument();

                XmlElement root =
                    response.CreateElement("MaintenanceResponse");

                response.AppendChild(root);

                AddElement(response, root,
                    "Success", success.ToString().ToLowerInvariant());

                AddElement(response, root,
                    "Message", message);

                AddElement(response, root,
                    "Data", data ?? "");

                byte[] bytes =
                    Encoding.UTF8.GetBytes(response.OuterXml);

                context.Response.StatusCode = statusCode;
                context.Response.ContentType =
                    "application/xml; charset=utf-8";

                context.Response.ContentLength64 = bytes.Length;

                context.Response.OutputStream.Write(
                    bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                ServiceLogger.LogError(
                    "Maintenance API response error", ex);
            }
            finally
            {
                context.Response.Close();
            }
        }

        private void AddElement(
            XmlDocument document,
            XmlElement parent,
            string name,
            string value)
        {
            XmlElement element =
                document.CreateElement(name);

            element.InnerText = value ?? "";

            parent.AppendChild(element);
        }
    }
}
