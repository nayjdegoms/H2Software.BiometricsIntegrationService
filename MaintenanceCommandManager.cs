
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Xml;
using SmackBio.WebSocketSDK;

namespace H2Software.BiometricsIntegrationService
{
    public static class MaintenanceCommandManager
    {
        // Ensures only one command is executed at a time
        // for each device session.
        private static readonly ConcurrentDictionary<Guid, object>
            _commandLocks =
                new ConcurrentDictionary<Guid, object>();

        public static XmlDocument ExecuteCommand(
            SBWebSocketHandler handler,
            XmlDocument request,
            int timeoutMilliseconds = 15000)
        {
            if (handler == null)
                throw new ArgumentNullException("handler");

            if (request == null || request.DocumentElement == null)
                throw new ArgumentException(
                    "Invalid SDK command XML.", "request");

            if (!handler.loggedIn)
                throw new InvalidOperationException(
                    "The biometric device is not logged in.");

            object commandLock = _commandLocks.GetOrAdd(
                handler.SessionId,
                id => new object());

            lock (commandLock)
            {
                if (!handler.loggedIn)
                    throw new InvalidOperationException(
                        "The biometric device is no longer connected.");

                var completion =
                    new TaskCompletionSource<XmlDocument>();

                IAsyncResult pendingCommand =
                    handler.BeginCommand(
                        request,
                        ar =>
                        {
                            try
                            {
                                XmlDocument response =
                                    handler.EndCommand(ar);

                                completion.TrySetResult(response);
                            }
                            catch (Exception ex)
                            {
                                completion.TrySetException(ex);
                            }
                        },
                        null,
                        timeoutMilliseconds);

                // Allow a little extra time for the SDK callback.
                if (!completion.Task.Wait(
                    timeoutMilliseconds + 3000))
                {
                    handler.CancelCommand(
                        pendingCommand,
                        new TimeoutException(
                            "The biometric device did not respond in time."));

                    if (!completion.Task.Wait(2000))
                    {
                        throw new TimeoutException(
                            "Unable to receive the SDK command response.");
                    }
                }

                return completion.Task.GetAwaiter().GetResult();
            }
        }
    }
}