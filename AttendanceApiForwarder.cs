using System;
using System.Configuration;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace H2Software.BiometricsIntegrationService
{
    public class AttendanceApiForwarder
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private readonly string _apiMode;
        private readonly string _apiUrl;
        private readonly string _cloudApiToken;

        public AttendanceApiForwarder()
        {
            _apiMode = ConfigurationManager.AppSettings["AttendanceApiMode"];

            if (string.IsNullOrWhiteSpace(_apiMode))
                throw new ConfigurationErrorsException(
                    "AttendanceApiMode is missing from App.config.");

            _apiMode = _apiMode.Trim().ToUpperInvariant();

            if (_apiMode == "CLOUD")
            {
                _apiUrl = ConfigurationManager.AppSettings["CloudAttendanceApiUrl"];

                // Retrieve JWT from Windows environment variable.
                _cloudApiToken = Environment.GetEnvironmentVariable(
                    "CloudAttendanceApiToken",
                    EnvironmentVariableTarget.Machine);

                if (string.IsNullOrWhiteSpace(_cloudApiToken))
                    throw new ConfigurationErrorsException(
                        "CloudAttendanceApiToken environment variable is missing.");
            }
            else if (_apiMode == "LOCAL")
            {
                _apiUrl = ConfigurationManager.AppSettings["LocalAttendanceApiUrl"];
                _cloudApiToken = null;
            }
            else
            {
                throw new ConfigurationErrorsException(
                    "Invalid AttendanceApiMode. Use CLOUD or LOCAL.");
            }

            if (string.IsNullOrWhiteSpace(_apiUrl))
                throw new ConfigurationErrorsException(
                    "The API URL for AttendanceApiMode '" + _apiMode
                    + "' is missing from App.config.");

            _apiUrl = _apiUrl.Trim();
        }

        public async Task<bool> ForwardEventAsync(
            long employeeId,
            DateTime punchTime,
            string deviceStatus)
        {
            string punchType;

            // Keep the existing SmackBio device-status mapping.
            switch (deviceStatus)
            {
                case "DutyOn":
                    punchType = "IN";
                    break;

                case "DutyOff":
                    punchType = "OUT";
                    break;

                case "Out":
                    punchType = "BreakOut";
                    break;

                case "In":
                    punchType = "BreakIn";
                    break;

                case "OtOn":
                case "OvertimeOn":
                    punchType = "OTTimeIn";
                    break;

                case "OtOff":
                case "OvertimeOff":
                    punchType = "OTTimeOut";
                    break;

                default:
                    ServiceLogger.Log(
                        "Unsupported biometric status: " + deviceStatus);
                    return false;
            }

            string json;
            string requestUrl;

            if (_apiMode == "CLOUD")
            {
                // CLOUD API payload.
                var record = new[]
                {
                    new
                    {
                        personnelBioCode = employeeId.ToString(
                            CultureInfo.InvariantCulture),

                        dateEntry = punchTime.ToString(
                            "yyyy-MM-dd", CultureInfo.InvariantCulture),

                        timeEntry = punchTime.ToString(
                            "HH:mm:ss", CultureInfo.InvariantCulture),

                        inOutEvent = punchType
                    }
                };

                json = JsonConvert.SerializeObject(record);
                requestUrl = _apiUrl;

                ServiceLogger.Log(
                    "Sending attendance to Cloud API. Employee: " + employeeId
                    + ", Type: " + punchType
                    + ", URL: " + requestUrl);
            }
            else
            {
                // LOCAL API payload (preserved).
                var record = new
                {
                    EmployeeId = employeeId.ToString(
                        CultureInfo.InvariantCulture),

                    PunchTime = punchTime,
                    PunchType = punchType
                };

                json = JsonConvert.SerializeObject(record);

                requestUrl = _apiUrl.TrimEnd('/') + "/api/attendance";

                ServiceLogger.Log(
                    "Sending attendance to Local API. Employee: " + employeeId
                    + ", Type: " + punchType
                    + ", URL: " + requestUrl);
            }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using (var request = new HttpRequestMessage(
                        HttpMethod.Post, requestUrl))
                    {
                        request.Content = new StringContent(
                            json,
                            Encoding.UTF8,
                            "application/json");

                        // Add JWT Bearer authentication for CLOUD only.
                        if (_apiMode == "CLOUD")
                        {
                            request.Headers.Authorization =
                                new AuthenticationHeaderValue(
                                    "Bearer",
                                    _cloudApiToken);
                        }

                        using (var response = await _httpClient.SendAsync(request))
                        {
                            string responseBody = string.Empty;

                            // HTTP 401: Missing, invalid or expired token.
                            if ((int)response.StatusCode == 401)
                            {
                                ServiceLogger.Log(
                                    "Cloud API authentication failed (401). "
                                    + "Check JWT token and expiration.");

                                return false;
                            }

                            // HTTP 403: Authenticated but not authorized.
                            if ((int)response.StatusCode == 403)
                            {
                                ServiceLogger.Log(
                                    "Cloud API access forbidden (403). "
                                    + "Check API authorization permissions.");

                                return false;
                            }

                            // Preserve existing behavior: success or 409
                            // is treated as accepted attendance submission.
                            if (response.IsSuccessStatusCode ||
                                (int)response.StatusCode == 409)
                            {
                                responseBody =
                                await response.Content.ReadAsStringAsync();

                                ServiceLogger.Log(
                                    (_apiMode == "CLOUD" ? "Cloud" : "Local")
                                    + " Attendance API accepted the request. HTTP "
                                    + (int)response.StatusCode
                                    + ". Employee: " + employeeId
                                    + ", Type: " + punchType
                                    + ". Response: " + responseBody);

                                return true;
                            }

                            responseBody =
                                await response.Content.ReadAsStringAsync();

                            ServiceLogger.Log(
                                (_apiMode == "CLOUD" ? "Cloud" : "Local")
                                + " API error. HTTP "
                                + (int)response.StatusCode
                                + ". Response: " + responseBody);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ServiceLogger.LogError(
                        _apiMode + " API forwarding attempt "
                        + attempt + " failed",
                        ex);
                }

                if (attempt < 3)
                    await Task.Delay(attempt * 1000);
            }

            ServiceLogger.Log(
                _apiMode + " attendance forwarding failed after 3 attempts. Employee: "
                + employeeId + ", Type: " + punchType);

            return false;
        }
    }
}