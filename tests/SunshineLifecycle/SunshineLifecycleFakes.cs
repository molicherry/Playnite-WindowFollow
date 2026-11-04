#if SUNSHINE_LIFECYCLE_TESTS
using System;
using System.Collections.Generic;
using System.Threading;

namespace SunshineWindowFollow.LifecycleTests
{
    internal enum LifecycleCallKind
    {
        Enable,
        Disable,
        Status
    }

    internal sealed class LifecycleCall
    {
        public LifecycleCallKind Kind { get; set; }

        public int SessionId { get; set; }

        public string Mode { get; set; }

        public string WindowTitle { get; set; }

        public int ProcessId { get; set; }

        public int ThreadId { get; set; }

        public DateTime TimestampUtc { get; set; }
    }

    internal sealed class LifecycleFakeTransport : ISunshineTransport
    {
        private readonly object sync = new object();
        private readonly List<LifecycleCall> calls = new List<LifecycleCall>();

        public Func<LifecycleCall, SunshineResponse> Handler { get; set; }

        public SunshineResponse EnableWindowFollow(
            SunshineEndpoint endpoint,
            string mode,
            string windowTitle,
            int processId,
            int sessionId,
            bool debugLogging)
        {
            return Record(new LifecycleCall
            {
                Kind = LifecycleCallKind.Enable,
                SessionId = sessionId,
                Mode = mode,
                WindowTitle = windowTitle,
                ProcessId = processId,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                TimestampUtc = DateTime.UtcNow
            });
        }

        public SunshineResponse DisableWindowFollow(
            SunshineEndpoint endpoint,
            int sessionId,
            bool debugLogging)
        {
            return Record(new LifecycleCall
            {
                Kind = LifecycleCallKind.Disable,
                SessionId = sessionId,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                TimestampUtc = DateTime.UtcNow
            });
        }

        public SunshineResponse QueryStatus(
            SunshineEndpoint endpoint,
            bool debugLogging)
        {
            return Record(new LifecycleCall
            {
                Kind = LifecycleCallKind.Status,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                TimestampUtc = DateTime.UtcNow
            });
        }

        private SunshineResponse Record(LifecycleCall call)
        {
            var handler = Handler;
            lock (sync)
            {
                calls.Add(call);
                Monitor.PulseAll(sync);
            }

            if (handler == null)
            {
                return LifecycleResponses.Inactive();
            }

            return handler(call);
        }

        public bool WaitFor(Func<List<LifecycleCall>, bool> predicate, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (sync)
            {
                while (true)
                {
                    if (predicate(calls))
                    {
                        return true;
                    }

                    var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0)
                    {
                        return false;
                    }

                    Monitor.Wait(sync, Math.Min(remaining, 50));
                }
            }
        }

        public List<LifecycleCall> Snapshot()
        {
            lock (sync)
            {
                return new List<LifecycleCall>(calls);
            }
        }

        public int Count(LifecycleCallKind kind)
        {
            return Snapshot().FindAll(call => call.Kind == kind).Count;
        }

        public LifecycleCall Last(LifecycleCallKind kind)
        {
            var snapshot = Snapshot();
            for (var index = snapshot.Count - 1; index >= 0; index--)
            {
                if (snapshot[index].Kind == kind)
                {
                    return snapshot[index];
                }
            }

            return null;
        }
    }

    internal sealed class LifecycleSettingsProvider : ISunshineSettingsProvider
    {
        public SunshineSettings Settings { get; set; }

        public SunshineSettings GetSnapshot()
        {
            return Settings == null ? null : Settings.Clone();
        }
    }

    internal sealed class LifecycleLogger : ISunshineLogger
    {
        private readonly object sync = new object();
        private readonly List<string> lines = new List<string>();

        public void Debug(string message)
        {
            Add("DEBUG", message);
        }

        public void Info(string message)
        {
            Add("INFO", message);
        }

        public void Warn(string message)
        {
            Add("WARN", message);
        }

        public void Error(string message)
        {
            Add("ERROR", message);
        }

        public void Error(Exception exception, string message)
        {
            Add("ERROR", message + " :: " + (exception == null ? string.Empty : exception.Message));
        }

        private void Add(string level, string message)
        {
            lock (sync)
            {
                lines.Add(level + " " + message);
            }
        }

        public string[] Snapshot()
        {
            lock (sync)
            {
                return lines.ToArray();
            }
        }
    }

    internal static class LifecycleResponses
    {
        public static WindowFollowStatus MakeStatus(bool active, int sessionId, string clientUuid)
        {
            return new WindowFollowStatus
            {
                Ok = true,
                Active = active,
                Mode = CaptureModes.Foreground,
                SessionId = sessionId,
                ClientUuid = clientUuid
            };
        }

        public static SunshineResponse Active(int sessionId)
        {
            return Http(200, MakeStatus(true, sessionId, "test-client"));
        }

        public static SunshineResponse Inactive()
        {
            return Http(200, MakeStatus(false, 0, null));
        }

        public static SunshineResponse Http(int statusCode, WindowFollowStatus status)
        {
            return new SunshineResponse
            {
                HasResponse = true,
                StatusCode = statusCode,
                Status = status,
                Body = string.Empty,
                EndpointDisplay = "test-endpoint"
            };
        }

        public static SunshineResponse TransportFailure(string message)
        {
            return new SunshineResponse
            {
                HasResponse = false,
                StatusCode = 0,
                TransportError = message,
                EndpointDisplay = "test-endpoint"
            };
        }
    }

    internal sealed class LifecycleSettings
    {
        public static LifecycleSettingsProvider Create()
        {
            return new LifecycleSettingsProvider
            {
                Settings = new SunshineSettings
                {
                    Enabled = true,
                    ServerUrl = "https://127.0.0.1:47990",
                    CaptureMode = CaptureModes.Foreground,
                    StartDelayMs = 0
                }
            };
        }
    }
}
#endif
