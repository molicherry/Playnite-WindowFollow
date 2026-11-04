using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Playnite.SDK;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 与 Sunshine 补丁端点 /api/capture/window-follow 通信的 HTTP 客户端。
    ///
    /// 请求（POST JSON）：
    ///   {"action":"enable"|"disable","mode":"foreground|title|pid",
    ///    "window_title":"...","process_id":0,"session_id":0}
    /// GET 返回会话列表状态。
    ///
    /// 状态码：200 成功；400 参数错误；401 认证失败；403 非本机；404 无活动串流会话（静默跳过）；
    ///        409 多会话/不支持；500 服务端错误。
    ///
    /// TLS/安全说明：
    ///   - 端点只允许回环地址（127.0.0.1 / ::1 / localhost），且明文 http:// 不允许携带凭据；
    ///   - 禁止自动重定向，避免把 Basic 凭据/控制请求带到其它主机；
    ///   - 不修改进程级 ServicePointManager 证书策略。现代 .NET Framework 使用
    ///     HttpClientHandler 的每 handler 回调；旧运行时使用 HttpWebRequest 的每请求回调；
    ///   - 证书错误只对“当前请求 URI 与配置端点完全一致且为回环地址”放行自签名证书；
    ///     回调拿不到请求 URI 时一律拒绝。旧运行时若无法设置每请求回调，则使用默认校验，
    ///     自签名证书会失败，并在日志中给出明确提示（安装证书到受信任根存储或改用受信任证书）。
    /// </summary>
    public sealed class SunshineClient
    {
        public const int TimeoutMs = 3000;
        public const int MaxAttempts = 2;
        private const int RetryDelayMs = 300;

        /// <summary>
        /// .NET Framework 4.7.1+ 的 HttpClientHandler 才有的属性；反射获取，避免把插件绑定到 4.7.1+。
        /// </summary>
        private static readonly PropertyInfo HandlerCertificateCallbackProperty =
            typeof(HttpClientHandler).GetProperty(
                "ServerCertificateCustomValidationCallback",
                BindingFlags.Public | BindingFlags.Instance);

        private static readonly bool SupportsPerHandlerCertificateCallback =
            HandlerCertificateCallbackProperty != null &&
            HandlerCertificateCallbackProperty.PropertyType ==
                typeof(Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>);

        /// <summary>
        /// .NET Framework 的 HttpWebRequest 每请求证书回调（不是进程级
        /// ServicePointManager 回调）；旧运行时没有该属性时回退到默认证书校验。
        /// </summary>
        private static readonly PropertyInfo WebRequestCertificateCallbackProperty =
            typeof(HttpWebRequest).GetProperty(
                "ServerCertificateValidationCallback",
                BindingFlags.Public | BindingFlags.Instance);

        private static readonly bool SupportsPerRequestCertificateCallback =
            WebRequestCertificateCallbackProperty != null &&
            WebRequestCertificateCallbackProperty.PropertyType == typeof(RemoteCertificateValidationCallback);

        private readonly ILogger logger;
        private readonly bool debugLogging;

        public SunshineClient(ILogger logger, bool debugLogging)
        {
            this.logger = logger;
            this.debugLogging = debugLogging;
        }

        /// <summary>请求 Sunshine 把当前串流会话的捕获目标切换为游戏窗口。</summary>
        public SunshineResponse EnableWindowFollow(
            SunshineEndpoint endpoint,
            string mode,
            string windowTitle,
            int processId,
            int sessionId = 0)
        {
            var body = BuildWindowFollowJson("enable", mode, windowTitle, processId, sessionId);
            return SendWithRetry(endpoint, "POST", body, "enable");
        }

        /// <summary>请求 Sunshine 恢复整屏（显示器）捕获。</summary>
        public SunshineResponse DisableWindowFollow(SunshineEndpoint endpoint, int sessionId = 0)
        {
            var body = BuildWindowFollowJson("disable", CaptureModes.Foreground, string.Empty, 0, sessionId);
            return SendWithRetry(endpoint, "POST", body, "disable");
        }

        /// <summary>查询当前窗口跟随状态。</summary>
        public SunshineResponse QueryStatus(SunshineEndpoint endpoint)
        {
            return SendWithRetry(endpoint, "GET", null, "status");
        }

        #region 请求发送

        private SunshineResponse SendWithRetry(SunshineEndpoint endpoint, string method, string body, string operation)
        {
            SunshineResponse lastResponse = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                lastResponse = ExecuteOnce(endpoint, method, body);
                lastResponse.Attempts = attempt;
                lastResponse.EndpointDisplay = endpoint == null ? string.Empty : endpoint.DisplayUrl;

                WriteDebug(string.Format(
                    CultureInfo.InvariantCulture,
                    "Sunshine {0} 请求（第 {1} 次）：{2} {3} -> {4}",
                    operation,
                    attempt,
                    method,
                    endpoint == null ? "(null)" : endpoint.ApiUri.ToString(),
                    lastResponse.HasResponse ? "HTTP " + lastResponse.StatusCode : "传输失败: " + lastResponse.TransportError));

                if (lastResponse.HasResponse)
                {
                    WriteDebug("Sunshine 响应正文：" + SunshineText.Truncate(lastResponse.Body));
                }

                if (attempt >= MaxAttempts || !ShouldRetry(lastResponse))
                {
                    break;
                }

                if (logger != null)
                {
                    logger.Warn(string.Format(
                        "Sunshine {0} 请求失败（{1}），{2} 毫秒后重试一次。",
                        operation,
                        lastResponse.HasResponse ? "HTTP " + lastResponse.StatusCode : lastResponse.TransportError,
                        RetryDelayMs));
                }
                Thread.Sleep(RetryDelayMs);
            }

            return lastResponse;
        }

        private static bool ShouldRetry(SunshineResponse response)
        {
            if (response == null)
            {
                return true;
            }

            if (!response.HasResponse)
            {
                // 连接失败/超时/证书错误：重试一次（可能只是瞬时问题）。
                return true;
            }

            // 客户端错误（400/401/403/404/409）重试没有意义；服务端 5xx 重试一次。
            return response.StatusCode >= 500;
        }

        private SunshineResponse ExecuteOnce(SunshineEndpoint endpoint, string method, string body)
        {
            if (endpoint == null || endpoint.ApiUri == null)
            {
                return CreateTransportFailure("Sunshine 端点未配置");
            }

            if (!endpoint.IsLoopbackHost)
            {
                return CreateTransportFailure(
                    "Sunshine 控制端点只允许本机回环地址（127.0.0.1、::1 或 localhost）");
            }

            if (endpoint.IsInsecureHttp && endpoint.HasCredentials)
            {
                return CreateTransportFailure("拒绝通过明文 http:// 发送 Sunshine 凭据；请使用 https://127.0.0.1:47990");
            }

            if (SupportsPerHandlerCertificateCallback)
            {
                return ExecuteWithHttpClient(endpoint, method, body);
            }

            return ExecuteWithHttpWebRequest(endpoint, method, body);
        }

        /// <summary>
        /// 首选实现：HttpClient + 每 handler 证书回调（.NET Framework 4.7.1+）。
        /// 不修改进程级 ServicePointManager 回调。
        /// </summary>
        private SunshineResponse ExecuteWithHttpClient(SunshineEndpoint endpoint, string method, string body)
        {
            using (var handler = new HttpClientHandler())
            {
                // localhost-only 控制 API：不走系统代理，也不接受/跟随重定向。
                handler.AllowAutoRedirect = false;
                handler.UseProxy = false;
                handler.UseCookies = false;

                var callback = new Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>(
                    delegate (HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
                    {
                        // 旧版 .NET Framework 可能在回调里传 null HttpRequestMessage；
                        // 此时无法确认请求目标，绝不因“已配置端点”而放行。
                        return IsCertificateAllowed(request == null ? null : request.RequestUri, endpoint, errors);
                    });

                try
                {
                    HandlerCertificateCallbackProperty.SetValue(handler, callback);
                }
                catch (Exception exception)
                {
                    WriteDebug("无法设置 HttpClientHandler 每请求证书回调，回退到 HttpWebRequest：" + exception.Message);
                    return ExecuteWithHttpWebRequest(endpoint, method, body);
                }

                using (var client = new HttpClient(handler, false))
                {
                    client.Timeout = TimeSpan.FromMilliseconds(TimeoutMs);

                    using (var request = new HttpRequestMessage(new HttpMethod(method), endpoint.ApiUri))
                    {
                        var authorization = CreateAuthorizationHeader(endpoint);
                        if (authorization != null)
                        {
                            request.Headers.Authorization = authorization;
                        }

                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        if (body != null)
                        {
                            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        }

                        try
                        {
                            using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                            {
                                var responseBody = response.Content == null
                                    ? string.Empty
                                    : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                return BuildResponse((int)response.StatusCode, responseBody);
                            }
                        }
                        catch (Exception exception)
                        {
                            return CreateTransportFailure(exception);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 兼容实现（.NET Framework 4.6.2 ~ 4.7.0）：HttpWebRequest + 每请求证书回调。
        /// 绝不替换 ServicePointManager.ServerCertificateValidationCallback。
        /// </summary>
        private SunshineResponse ExecuteWithHttpWebRequest(SunshineEndpoint endpoint, string method, string body)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(endpoint.ApiUri);
                request.Method = method;
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
                request.AllowAutoRedirect = false;
                request.Proxy = null;

                if (SupportsPerRequestCertificateCallback)
                {
                    var callback = new RemoteCertificateValidationCallback(
                        delegate (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
                        {
                            // 这是“本次 request”的回调，因此直接用本次 request 的 URI；
                            // 不读取进程级状态，也不修改全局回调。
                            return IsCertificateAllowed(request.RequestUri, endpoint, errors);
                        });

                    try
                    {
                        WebRequestCertificateCallbackProperty.SetValue(request, callback);
                    }
                    catch (Exception exception)
                    {
                        WriteDebug("无法设置 HttpWebRequest 每请求证书回调，使用默认证书校验：" + exception.Message);
                    }
                }

                var authorization = CreateAuthorizationHeader(endpoint);
                if (authorization != null)
                {
                    request.Headers[HttpRequestHeader.Authorization] = authorization.ToString();
                }

                request.Accept = "application/json";
                if (body != null)
                {
                    var requestBytes = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/json; charset=utf-8";
                    request.ContentLength = requestBytes.Length;
                    using (var requestStream = request.GetRequestStream())
                    {
                        requestStream.Write(requestBytes, 0, requestBytes.Length);
                    }
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return BuildResponse((int)response.StatusCode, ReadResponseBody(response));
                }
            }
            catch (WebException webException)
            {
                var httpResponse = webException.Response as HttpWebResponse;
                if (httpResponse != null)
                {
                    using (httpResponse)
                    {
                        return BuildResponse((int)httpResponse.StatusCode, ReadResponseBody(httpResponse));
                    }
                }

                return CreateTransportFailure(webException);
            }
            catch (Exception exception)
            {
                return CreateTransportFailure(exception);
            }
        }

        #endregion

        #region 响应与证书

        private static SunshineResponse BuildResponse(int statusCode, string body)
        {
            return new SunshineResponse
            {
                HasResponse = true,
                StatusCode = statusCode,
                Body = body,
                Status = ParseStatus(body)
            };
        }

        private static SunshineResponse CreateTransportFailure(string message)
        {
            return new SunshineResponse
            {
                HasResponse = false,
                StatusCode = 0,
                TransportError = message
            };
        }

        internal static SunshineResponse CreateTransportFailure(Exception exception)
        {
            var message = DescribeException(exception);
            var isCertificateError = IsCertificateFailure(exception);
            var isTimeout = IsTimeoutFailure(exception);

            if (isTimeout)
            {
                message = "请求超时（" + TimeoutMs + " 毫秒）";
            }
            else if (isCertificateError)
            {
                var hint = "TLS 证书校验失败。Sunshine 默认使用本机自签名证书；若当前 .NET 运行时无法使用" +
                           "每请求证书回调，请把 Sunshine 证书安装到本机“受信任的根证书颁发机构”，" +
                           "或为 Sunshine 配置受信任证书。";
                message = hint + " 原始错误：" + message;
            }
            else if (exception is WebException)
            {
                var webException = (WebException)exception;
                if (webException.Status == WebExceptionStatus.Timeout ||
                    webException.Status == WebExceptionStatus.RequestCanceled)
                {
                    message = "请求超时（" + TimeoutMs + " 毫秒）";
                }
                else
                {
                    message = webException.Status + ": " + message;
                }
            }

            return new SunshineResponse
            {
                HasResponse = false,
                StatusCode = 0,
                TransportError = message
            };
        }

        private static bool IsTimeoutFailure(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return true;
            }

            var webException = exception as WebException;
            return webException != null &&
                   (webException.Status == WebExceptionStatus.Timeout ||
                    webException.Status == WebExceptionStatus.RequestCanceled);
        }

        private static bool IsCertificateFailure(Exception exception)
        {
            var current = exception;
            var depth = 0;
            while (current != null && depth < 8)
            {
                if (current is AuthenticationException)
                {
                    return true;
                }

                var webException = current as WebException;
                if (webException != null &&
                    (webException.Status == WebExceptionStatus.TrustFailure ||
                     webException.Status == WebExceptionStatus.SecureChannelFailure))
                {
                    return true;
                }

                var message = current.Message ?? string.Empty;
                if (message.IndexOf("self-signed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("self signed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("自签名", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("证书", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("trust relationship", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                current = current.InnerException;
                depth++;
            }

            return false;
        }

        /// <summary>
        /// 把异常及其内部异常的消息串起来，便于定位 TLS/代理/证书等底层问题。
        /// </summary>
        private static string DescribeException(Exception exception)
        {
            if (exception == null)
            {
                return "未知错误";
            }

            var messages = new List<string>();
            var current = exception;
            var depth = 0;
            while (current != null && depth < 4)
            {
                if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message))
                {
                    messages.Add(current.Message);
                }

                current = current.InnerException;
                depth++;
            }

            return messages.Count == 0 ? exception.GetType().Name : string.Join(" -> ", messages.ToArray());
        }

        /// <summary>
        /// 仅接受“常规校验通过”或“本次请求目标就是配置的本机 Sunshine 端点”两种情况。
        /// requestUri 为 null 时拒绝（无法证明请求目标）；非回环地址一律拒绝。
        /// </summary>
        internal static bool IsCertificateAllowed(Uri requestUri, SunshineEndpoint endpoint, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (endpoint == null || requestUri == null)
            {
                return false;
            }

            return endpoint.IsLoopbackHost && endpoint.MatchesRequestUri(requestUri);
        }

        /// <summary>兼容旧测试/调用的证书判定入口。</summary>
        internal static bool ValidateCertificate(Uri requestUri, SunshineEndpoint endpoint, SslPolicyErrors errors)
        {
            return IsCertificateAllowed(requestUri, endpoint, errors);
        }

        private static string ReadResponseBody(HttpWebResponse response)
        {
            try
            {
                if (response == null)
                {
                    return string.Empty;
                }

                using (var stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return string.Empty;
                    }

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static WindowFollowStatus ParseStatus(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            // 使用插件内置的 JsonLite 解析，不依赖 Playnite 宿主的序列化器初始化状态。
            return WindowFollowStatus.FromJson(body);
        }

        #endregion

        #region HTTP 细节

        private static AuthenticationHeaderValue CreateAuthorizationHeader(SunshineEndpoint endpoint)
        {
            if (endpoint == null || !endpoint.HasCredentials)
            {
                return null;
            }

            // 明文 HTTP 不允许携带凭据（SunshineEndpoint 已拒绝这种配置，这里再兜底一次）。
            if (endpoint.IsInsecureHttp)
            {
                return null;
            }

            var credentials = (endpoint.Username ?? string.Empty) + ":" + (endpoint.Password ?? string.Empty);
            return new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        /// <summary>
        /// 手工构造请求 JSON（字段顺序与端点契约一致），避免引入 JSON 依赖。
        /// session_id 为 0 时由 Sunshine 选择唯一活动会话。
        /// </summary>
        internal static string BuildWindowFollowJson(
            string action,
            string mode,
            string windowTitle,
            int processId,
            int sessionId)
        {
            var safeSessionId = Math.Max(0, sessionId);
            var builder = new StringBuilder(192);
            builder.Append('{');
            AppendJsonProperty(builder, "action", action, true);
            AppendJsonProperty(builder, "mode", mode, false);
            AppendJsonProperty(builder, "window_title", windowTitle, false);
            builder.Append(",\"process_id\":").Append(processId.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"session_id\":").Append(safeSessionId.ToString(CultureInfo.InvariantCulture));
            builder.Append('}');
            return builder.ToString();
        }

        internal static string BuildWindowFollowJson(
            string action,
            string mode,
            string windowTitle,
            int processId)
        {
            return BuildWindowFollowJson(action, mode, windowTitle, processId, 0);
        }

        private static void AppendJsonProperty(StringBuilder builder, string name, string value, bool first)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(name).Append("\":\"").Append(EscapeJson(value)).Append('"');
        }

        /// <summary>
        /// 转义 JSON 字符串值。游戏名/自定义标题可能包含引号、反斜杠、中文或控制字符。
        /// </summary>
        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length + 16);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < ' ')
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        private void WriteDebug(string message)
        {
            if (debugLogging && logger != null)
            {
                logger.Debug(message);
            }
        }

        #endregion
    }
}
