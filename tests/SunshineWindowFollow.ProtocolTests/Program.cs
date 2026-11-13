using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace SunshineWindowFollow.ProtocolTests
{
    internal static class Program
    {
        private const string TwoSessionFixture = @"{
  ""ok"": true,
  ""feature"": ""window-follow"",
  ""supported"": true,
  ""sessions"": [
    {
      ""session_id"": 3,
      ""client_uuid"": ""uuid-a"",
      ""client_name"": ""Living Room"",
      ""app_name"": ""Desktop"",
      ""running"": true,
      ""control_only"": false,
      ""override"": { ""enabled"": false, ""mode"": ""foreground"", ""window_title"": """", ""process_id"": 0 },
      ""observed"": {
        ""active"": true,
        ""fallback_to_display"": false,
        ""window_title"": ""Other Window"",
        ""process_id"": 11,
        ""hwnd"": ""0x1"",
        ""fallback_reason"": """"
      }
    },
    {
      ""session_id"": 7,
      ""client_uuid"": ""uuid-b"",
      ""client_name"": ""Bedroom"",
      ""app_name"": ""Playnite"",
      ""running"": true,
      ""control_only"": false,
      ""override"": { ""enabled"": true, ""mode"": ""pid"", ""window_title"": """", ""process_id"": 4242 },
      ""observed"": {
        ""active"": false,
        ""fallback_to_display"": true,
        ""window_title"": ""Hades"",
        ""process_id"": 4242,
        ""hwnd"": ""0xABC"",
        ""fallback_reason"": ""window_not_found""
      }
    }
  ],
  ""selected_session_id"": 7,
  ""selected_client_uuid"": ""uuid-b""
}";

        private const string SingleSessionFixture = @"{
  ""ok"": true,
  ""sessions"": [
    {
      ""session_id"": 9,
      ""client_uuid"": ""uuid-single"",
      ""running"": true,
      ""override"": { ""enabled"": true, ""mode"": ""title"", ""window_title"": ""Hades"", ""process_id"": 0 },
      ""observed"": {
        ""active"": false,
        ""fallback_to_display"": true,
        ""window_title"": ""Hades"",
        ""process_id"": 900,
        ""hwnd"": ""0x900"",
        ""fallback_reason"": ""waiting_for_window""
      }
    }
  ]
}";

        public static async Task<int> Main()
        {
            var tests = new (string Name, Func<Task> Run)[]
            {
                ("parses nested selected session and uses override.enabled", ParseNestedSelectedSessionAsync),
                ("ignores old root-level status fields", IgnoreLegacyRootFieldsAsync),
                ("falls back to the only session", FallBackToUniqueSessionAsync),
                ("selects session by selected_client_uuid", SelectByClientUuidAsync),
                ("supports selected marker and null observed", ParseSelectedMarkerAsync),
                ("builds request JSON with session_id only", BuildRequestJsonAsync),
                ("rejects non-loopback, userinfo and plaintext credentials", EndpointSecurityAsync),
                ("certificate policy rejects null/foreign request URIs", CertificatePolicyAsync),
                ("non-loopback client endpoint is refused before sending", NonLoopbackClientAsync),
                ("HTTP plaintext credentials are refused before sending", PlaintextCredentialsAsync),
                ("HTTPS self-signed loopback works and global TLS state stays untouched", HttpsRequestAsync),
                ("disable request carries the target session id", DisableRequestAsync),
                ("redirects are not followed", RedirectAsync),
                ("self-signed certificate errors are reported clearly", CertificateErrorMessageAsync),
                ("malformed JSON returns null", MalformedJsonAsync)
            };

            var failed = 0;
            foreach (var test in tests)
            {
                try
                {
                    await test.Run();
                    Console.WriteLine("[PASS] " + test.Name);
                }
                catch (Exception exception)
                {
                    failed++;
                    Console.WriteLine("[FAIL] " + test.Name);
                    Console.WriteLine("       " + exception.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                "{0}/{1} protocol/TLS checks passed.",
                tests.Length - failed,
                tests.Length);
            return failed == 0 ? 0 : 1;
        }

        private static Task ParseNestedSelectedSessionAsync()
        {
            var status = WindowFollowStatus.FromJson(TwoSessionFixture);
            Assert.NotNull(status, "status");
            Assert.Equal(true, status.Ok, "ok");
            Assert.Equal(7, status.SessionId, "selected session id");
            Assert.Equal("uuid-b", status.ClientUuid, "selected client uuid");
            Assert.Equal(2, status.Sessions.Count, "sessions retained");
            Assert.NotNull(status.SelectedSession, "selected session object");
            Assert.Equal(7, status.SelectedSession.SessionId, "selected session object id");

            // The selected session's observed.active is false, but the user's
            // override is enabled: Active must follow override.enabled.
            Assert.Equal(true, status.Active, "active = override.enabled");
            Assert.Equal("pid", status.Mode, "mode");
            Assert.NotNull(status.Window, "window");
            Assert.Equal("Hades", status.Window.Title, "window title");
            Assert.Equal(4242, status.Window.ProcessId, "window process");
            Assert.Equal("0xABC", status.Window.Hwnd, "window hwnd");
            Assert.Equal("window_not_found", status.Fallback, "fallback reason");
            Assert.Equal(true, status.FallbackToDisplay, "fallback_to_display");
            Assert.Equal(false, status.Sessions[0].Override.Enabled, "other session override");
            Assert.Equal(true, status.Sessions[0].Observed.Active, "other session observed");
            return Task.CompletedTask;
        }

        private static Task IgnoreLegacyRootFieldsAsync()
        {
            const string legacy = @"{
  ""ok"": true,
  ""active"": true,
  ""mode"": ""title"",
  ""session_id"": 77,
  ""client_uuid"": ""legacy-client"",
  ""window"": { ""title"": ""Legacy Window"", ""process_id"": 77, ""hwnd"": ""0x77"" },
  ""fallback"": ""legacy-fallback"",
  ""sessions"": [
    {
      ""session_id"": 5,
      ""client_uuid"": ""uuid-5"",
      ""override"": { ""enabled"": false, ""mode"": ""foreground"" },
      ""observed"": {
        ""active"": true,
        ""fallback_to_display"": false,
        ""window_title"": ""Real Window"",
        ""process_id"": 55,
        ""hwnd"": ""0x55"",
        ""fallback_reason"": """"
      }
    }
  ]
}";

            var status = WindowFollowStatus.FromJson(legacy);
            Assert.NotNull(status, "status");
            Assert.Equal(false, status.Active, "legacy root active ignored");
            Assert.Equal("foreground", status.Mode, "legacy root mode ignored");
            Assert.Equal(5, status.SessionId, "unique session id used");
            Assert.Equal("uuid-5", status.ClientUuid, "unique session uuid used");
            Assert.Equal("Real Window", status.Window.Title, "nested window used");
            Assert.Equal(string.Empty, status.Fallback, "nested empty fallback used");
            return Task.CompletedTask;
        }

        private static Task FallBackToUniqueSessionAsync()
        {
            var status = WindowFollowStatus.FromJson(SingleSessionFixture);
            Assert.NotNull(status, "status");
            Assert.Equal(9, status.SessionId, "unique session id");
            Assert.Equal("uuid-single", status.ClientUuid, "unique session uuid");
            Assert.Equal(true, status.Active, "override enabled");
            Assert.Equal("title", status.Mode, "override mode");
            Assert.Equal("Hades", status.Window.Title, "observed window title");
            Assert.Equal("waiting_for_window", status.Fallback, "observed fallback");
            return Task.CompletedTask;
        }

        private static Task SelectByClientUuidAsync()
        {
            const string fixture = @"{
  ""ok"": true,
  ""selected_client_uuid"": ""uuid-b"",
  ""sessions"": [
    { ""session_id"": 3, ""client_uuid"": ""uuid-a"", ""override"": { ""enabled"": false }, ""observed"": { ""active"": false } },
    { ""session_id"": 7, ""client_uuid"": ""uuid-b"", ""override"": { ""enabled"": true, ""mode"": ""foreground"" },
      ""observed"": { ""active"": true, ""window_title"": ""B"", ""process_id"": 7, ""hwnd"": ""0xB"", ""fallback_reason"": """" } }
  ]
}";

            var status = WindowFollowStatus.FromJson(fixture);
            Assert.NotNull(status, "status");
            Assert.Equal(7, status.SessionId, "uuid-selected session id");
            Assert.Equal("uuid-b", status.ClientUuid, "uuid-selected client");
            Assert.Equal("B", status.Window.Title, "uuid-selected window");
            return Task.CompletedTask;
        }

        private static Task ParseSelectedMarkerAsync()
        {
            const string fixture = @"{
  ""ok"": true,
  ""sessions"": [
    { ""session_id"": 3, ""client_uuid"": ""uuid-a"", ""override"": { ""enabled"": false } },
    { ""session_id"": 7, ""client_uuid"": ""uuid-b"", ""selected"": true,
      ""override"": { ""enabled"": true, ""mode"": ""foreground"" }, ""observed"": null }
  ]
}";

            var status = WindowFollowStatus.FromJson(fixture);
            Assert.NotNull(status, "status");
            Assert.Equal(true, status.Active, "selected marker override");
            Assert.Equal(7, status.SessionId, "selected marker id");
            Assert.Equal(null, status.Window, "null observed keeps window null");
            Assert.Equal(null, status.Fallback, "null observed keeps fallback null");
            return Task.CompletedTask;
        }

        private static Task BuildRequestJsonAsync()
        {
            var body = SunshineClient.BuildWindowFollowJson(
                "enable",
                "title",
                "He said \"hi\"\n中文",
                4242,
                42);

            var json = JsonLite.Parse(body);
            Assert.Equal("enable", json.GetString("action"), "action");
            Assert.Equal("title", json.GetString("mode"), "mode");
            Assert.Equal("He said \"hi\"\n中文", json.GetString("window_title"), "escaped title");
            Assert.Equal(4242, json.GetInt("process_id"), "process_id");
            Assert.Equal(42, json.GetInt("session_id"), "session_id");
            Assert.Equal(null, json.Get("id"), "unexpected id field");

            var defaultBody = SunshineClient.BuildWindowFollowJson(
                "disable",
                CaptureModes.Foreground,
                string.Empty,
                0,
                0);
            var defaultJson = JsonLite.Parse(defaultBody);
            Assert.Equal(0, defaultJson.GetInt("session_id"), "default session_id");
            Assert.Equal(null, defaultJson.Get("id"), "default unexpected id field");
            return Task.CompletedTask;
        }

        private static Task EndpointSecurityAsync()
        {
            SunshineEndpoint endpoint;
            string error;

            Assert.True(
                SunshineEndpoint.TryCreate(Settings("https://127.0.0.1:47990"), out endpoint, out error),
                "loopback https: " + error);
            Assert.True(endpoint.IsLoopbackHost, "endpoint is loopback");
            Assert.Equal("https://127.0.0.1:47990/api/capture/window-follow", endpoint.ApiUri.ToString(), "api uri");

            Assert.True(
                SunshineEndpoint.TryCreate(Settings("https://localhost:47990/path/ignored"), out endpoint, out error),
                "localhost https: " + error);
            Assert.Equal("https://localhost:47990/api/capture/window-follow", endpoint.ApiUri.ToString(), "path ignored");

            Assert.True(
                SunshineEndpoint.TryCreate(Settings("https://[::1]:47990"), out endpoint, out error),
                "IPv6 loopback https: " + error);
            Assert.True(endpoint.IsLoopbackHost, "IPv6 endpoint is loopback");

            Assert.True(
                SunshineEndpoint.TryCreate(Settings("http://127.0.0.1:47990"), out endpoint, out error),
                "loopback http without credentials: " + error);

            Assert.False(
                SunshineEndpoint.TryCreate(Settings("https://192.168.1.10:47990"), out endpoint, out error),
                "non-loopback must be rejected");
            Assert.Contains(error, "回环", "non-loopback error");

            Assert.False(
                SunshineEndpoint.TryCreate(Settings("http://127.0.0.1:47990", "user", "pass"), out endpoint, out error),
                "plaintext credentials must be rejected");
            Assert.Contains(error, "http", "plaintext credentials error");

            Assert.False(
                SunshineEndpoint.TryCreate(Settings("https://user:pass@127.0.0.1:47990"), out endpoint, out error),
                "userinfo in URL must be rejected");
            Assert.Contains(error, "用户名", "userinfo error");

            Assert.True(
                SunshineEndpoint.TryCreate(Settings("https://127.0.0.1:47990", "user", "pass"), out endpoint, out error),
                "loopback https with credentials: " + error);
            Assert.True(endpoint.HasCredentials, "credentials retained");
            Assert.False(endpoint.IsInsecureHttp, "https is secure");
            return Task.CompletedTask;
        }

        private static Task CertificatePolicyAsync()
        {
            SunshineEndpoint endpoint;
            string error;
            Assert.True(
                SunshineEndpoint.TryCreate(Settings("https://127.0.0.1:47990"), out endpoint, out error),
                "endpoint: " + error);

            // A null request URI must never be enough to trust a certificate.
            Assert.False(
                SunshineClient.IsCertificateAllowed(
                    null,
                    endpoint,
                    SslPolicyErrors.RemoteCertificateNameMismatch),
                "null request URI rejected");
            Assert.False(
                SunshineClient.ValidateCertificate(
                    null,
                    endpoint,
                    SslPolicyErrors.RemoteCertificateNameMismatch),
                "null request URI rejected by compatibility entry point");

            // Playnite hosts on .NET Framework may invoke the handler callback
            // without an HttpRequestMessage; the production callback resolves that
            // to the configured loopback URI before validating.
            Assert.Equal(
                endpoint.ApiUri,
                SunshineClient.ResolveCertificateRequestUri(null, endpoint),
                "null request URI resolves to configured loopback endpoint");
            Assert.True(
                SunshineClient.IsCertificateAllowed(
                    SunshineClient.ResolveCertificateRequestUri(null, endpoint),
                    endpoint,
                    SslPolicyErrors.RemoteCertificateNameMismatch),
                "resolved loopback endpoint accepted for self-signed certificate");
            var forgedRemote = ForgeEndpoint(
                new Uri("https://192.0.2.1:47990/api/capture/window-follow"),
                string.Empty,
                string.Empty);
            Assert.True(
                SunshineClient.ResolveCertificateRequestUri(null, forgedRemote) == null,
                "non-loopback endpoint is not used as a null request URI fallback");

            Assert.True(
                SunshineClient.IsCertificateAllowed(
                    endpoint.ApiUri,
                    endpoint,
                    SslPolicyErrors.RemoteCertificateNameMismatch),
                "matching loopback endpoint accepted");

            Assert.True(
                SunshineClient.IsCertificateAllowed(
                    endpoint.ApiUri,
                    endpoint,
                    SslPolicyErrors.None),
                "valid certificate accepted");

            Assert.False(
                SunshineClient.IsCertificateAllowed(
                    new Uri("https://127.0.0.1:47991/api/capture/window-follow"),
                    endpoint,
                    SslPolicyErrors.RemoteCertificateNameMismatch),
                "different port rejected");

            Assert.False(
                SunshineClient.IsCertificateAllowed(
                    new Uri("http://127.0.0.1:47990/api/capture/window-follow"),
                    endpoint,
                    SslPolicyErrors.RemoteCertificateNameMismatch),
                "different scheme rejected");

            Assert.False(endpoint.MatchesRequestUri(null), "MatchesRequestUri(null)");
            return Task.CompletedTask;
        }

        private static async Task PlaintextCredentialsAsync()
        {
            await using (var server = await TestServer.StartAsync(false, request => TestReply.Json("{\"ok\":true}")))
            {
                var endpoint = ForgeEndpoint(
                    new Uri(server.BaseUri, "api/capture/window-follow"),
                    "user",
                    "pass");

                var client = new SunshineClient(new RecordingLogger(), true);
                var response = client.QueryStatus(endpoint);

                Assert.False(response.HasResponse, "request not sent");
                Assert.Contains(response.TransportError, "明文", "plaintext error");
                Assert.Equal(0, server.Requests.Count, "server saw no request");
            }
        }

        private static Task NonLoopbackClientAsync()
        {
            var endpoint = ForgeEndpoint(
                new Uri("https://192.0.2.1:47990/api/capture/window-follow"),
                string.Empty,
                string.Empty);

            var client = new SunshineClient(new RecordingLogger(), true);
            var response = client.QueryStatus(endpoint);

            Assert.False(response.HasResponse, "non-loopback request not sent");
            Assert.Contains(response.TransportError, "回环", "non-loopback client error");
            return Task.CompletedTask;
        }

        private static async Task HttpsRequestAsync()
        {
            await using (var server = await TestServer.StartAsync(
                true,
                request => TestReply.Json(TwoSessionFixture)))
            {
                var endpoint = CreateEndpoint(server.BaseUri.AbsoluteUri, "用户", "p@ss");
                var client = new SunshineClient(new RecordingLogger(), true);

                RemoteCertificateValidationCallback sentinel = (sender, certificate, chain, errors) => false;
                var original = ServicePointManager.ServerCertificateValidationCallback;
                ServicePointManager.ServerCertificateValidationCallback = sentinel;
                try
                {
                    var response = client.EnableWindowFollow(endpoint, "title", "Hades \"中文\"", 4242, 7);

                    Assert.True(response.HasResponse, "HTTPS response received: " + response.TransportError);
                    Assert.Equal(200, response.StatusCode, "HTTP status");
                    Assert.NotNull(response.Status, "parsed status");
                    Assert.Equal(true, response.Status.Active, "status active");
                    Assert.Equal(7, response.Status.SessionId, "status session");
                    Assert.Equal(1, server.Requests.Count, "one request");

                    CapturedRequest request;
                    Assert.True(server.Requests.TryDequeue(out request), "captured request");
                    Assert.Equal("POST", request.Method, "method");
                    Assert.Equal("/api/capture/window-follow", request.Path, "path");
                    Assert.Contains(request.ContentType, "application/json", "content type");
                    Assert.Equal(
                        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("用户:p@ss")),
                        request.Authorization,
                        "basic auth");

                    var body = JsonLite.Parse(request.Body);
                    Assert.Equal("enable", body.GetString("action"), "body action");
                    Assert.Equal("title", body.GetString("mode"), "body mode");
                    Assert.Equal("Hades \"中文\"", body.GetString("window_title"), "body title");
                    Assert.Equal(4242, body.GetInt("process_id"), "body process_id");
                    Assert.Equal(7, body.GetInt("session_id"), "body session_id");
                    Assert.Equal(null, body.Get("id"), "body unexpected id field");

                    // The client must not replace/restore the process-wide callback.
                    Assert.True(
                        ReferenceEquals(sentinel, ServicePointManager.ServerCertificateValidationCallback),
                        "global certificate callback unchanged");
                }
                finally
                {
                    ServicePointManager.ServerCertificateValidationCallback = original;
                }
            }
        }

        private static async Task DisableRequestAsync()
        {
            await using (var server = await TestServer.StartAsync(
                false,
                request => TestReply.Json(SingleSessionFixture)))
            {
                var endpoint = CreateEndpoint(server.BaseUri.AbsoluteUri);
                var client = new SunshineClient(new RecordingLogger(), false);
                var response = client.DisableWindowFollow(endpoint, 12);

                Assert.True(response.HasResponse, "disable response: " + response.TransportError);
                Assert.Equal(200, response.StatusCode, "disable HTTP status");
                Assert.Equal(1, server.Requests.Count, "disable request count");

                CapturedRequest request;
                Assert.True(server.Requests.TryDequeue(out request), "disable captured request");
                var body = JsonLite.Parse(request.Body);
                Assert.Equal("disable", body.GetString("action"), "disable action");
                Assert.Equal("foreground", body.GetString("mode"), "disable mode");
                Assert.Equal(12, body.GetInt("session_id"), "disable session_id");
                Assert.Equal(null, body.Get("id"), "disable unexpected id field");
            }
        }

        private static async Task RedirectAsync()
        {
            await using (var server = await TestServer.StartAsync(
                true,
                request =>
                {
                    if (request.Path == "/api/capture/window-follow")
                    {
                        return TestReply.Status(302, string.Empty, "/should-not-be-followed");
                    }

                    return TestReply.Json("{\"ok\":true,\"reached\":true}");
                }))
            {
                var endpoint = CreateEndpoint(server.BaseUri.AbsoluteUri, "user", "pass");
                var client = new SunshineClient(new RecordingLogger(), true);
                var response = client.QueryStatus(endpoint);

                Assert.True(response.HasResponse, "redirect response received");
                Assert.Equal(302, response.StatusCode, "redirect returned as-is");
                Assert.Equal(1, server.Requests.Count, "redirect was not followed");
                Assert.Equal(
                    "/api/capture/window-follow",
                    server.Requests.First().Path,
                    "only the original path was requested");
            }
        }

        private static Task CertificateErrorMessageAsync()
        {
            var exception = new HttpRequestException(
                "The SSL connection could not be established.",
                new AuthenticationException(
                    "The remote certificate is invalid because of errors in the certificate chain: SelfSigned"));

            var response = SunshineClient.CreateTransportFailure(exception);
            Assert.False(response.HasResponse, "transport failure");
            Assert.Contains(response.TransportError, "自签名", "self-signed hint");
            Assert.Contains(response.TransportError, "受信任", "trust store hint");
            return Task.CompletedTask;
        }

        private static Task MalformedJsonAsync()
        {
            Assert.Equal(null, WindowFollowStatus.FromJson("{"), "truncated JSON");
            Assert.Equal(null, WindowFollowStatus.FromJson("[]"), "array root");
            Assert.Equal(null, WindowFollowStatus.FromJson(string.Empty), "empty body");
            return Task.CompletedTask;
        }

        private static SunshineSettings Settings(string url, string username = "", string password = "")
        {
            return new SunshineSettings
            {
                ServerUrl = url,
                Username = username,
                Password = password
            };
        }

        private static SunshineEndpoint CreateEndpoint(
            string url,
            string username = "",
            string password = "")
        {
            SunshineEndpoint endpoint;
            string error;
            if (!SunshineEndpoint.TryCreate(Settings(url, username, password), out endpoint, out error))
            {
                throw new Exception("Failed to create endpoint: " + error);
            }

            return endpoint;
        }

        /// <summary>Build an endpoint that the public factory would reject, to test client-side defense in depth.</summary>
        private static SunshineEndpoint ForgeEndpoint(Uri apiUri, string username, string password)
        {
            var endpoint = new SunshineEndpoint();
            var baseUri = new Uri(apiUri.GetLeftPart(UriPartial.Authority) + "/");
            SetPrivateProperty(endpoint, "BaseUri", baseUri);
            SetPrivateProperty(endpoint, "ApiUri", apiUri);
            SetPrivateProperty(endpoint, "Username", username);
            SetPrivateProperty(endpoint, "Password", password);
            return endpoint;
        }

        private static void SetPrivateProperty(object target, string name, object value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                throw new Exception("Missing property: " + name);
            }

            var setter = property.GetSetMethod(true);
            if (setter == null)
            {
                throw new Exception("Missing setter: " + name);
            }

            setter.Invoke(target, new[] { value });
        }
    }

    internal static class Assert
    {
        public static void True(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Assertion failed: " + name);
            }
        }

        public static void False(bool condition, string name)
        {
            True(!condition, name);
        }

        public static void Equal<T>(T expected, T actual, string name)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception(
                    string.Format("Assertion failed: {0} (expected={1}, actual={2})", name, expected, actual));
            }
        }

        public static void NotNull(object value, string name)
        {
            if (value == null)
            {
                throw new Exception("Assertion failed: " + name + " is null");
            }
        }

        public static void Contains(string value, string expected, string name)
        {
            if (value == null || !value.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception(
                    string.Format("Assertion failed: {0} (expected substring '{1}', actual='{2}')", name, expected, value));
            }
        }
    }
}
