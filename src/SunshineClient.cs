using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Playnite.SDK;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 与 Sunshine 补丁端点 /api/capture/window-follow 通信的 HTTP 客户端。
    ///
    /// 约定（由 Sunshine 侧补丁实现）：
    ///   POST {"action":"enable"|"disable"|"status","mode":"foreground|title|pid",
    ///         "window_title":"...","process_id":0,"session_id":0}
    ///   GET  返回同样的状态结构。
    /// 状态码：200 成功；400 参数错误；401 认证失败；403 非本机；404 无活动串流会话（静默跳过）；
    ///        409 多会话/不支持；500 服务端错误。
    ///
    /// 证书安全说明：Sunshine WebUI 默认使用自签名证书，这里只对“配置的 Sunshine 端点”
    /// （scheme + host + port 完全一致）放行证书错误，不会全局接受任意证书。
    ///   - 运行在 .NET Framework 4.7.1+ 时：通过 HttpClientHandler.ServerCertificateCustomValidationCallback
    ///     （反射设置）只作用于本次请求的 handler，不修改进程级 ServicePointManager。
    ///   - 更老的运行时：短暂替换进程级 ServicePointManager.ServerCertificateValidationCallback，
    ///     仅对本端点放行，其余请求回退到原有回调/默认校验，并在请求结束后恢复。
    /// </summary>
    public sealed class SunshineClient
    {
        public const int TimeoutMs = 3000;
        public const int MaxAttempts = 2;
        private const int RetryDelayMs = 300;

        private static readonly object CertificateCallbackSync = new object();

        /// <summary>
        /// .NET Framework 4.7.1+ 的 HttpClientHandler 才有的属性；反射获取，避免把插件绑定到 4.7.1+。
        /// </summary>
        private static readonly PropertyInfo HandlerCertificateCallbackProperty =
            typeof(HttpClientHandler).GetProperty(
                "ServerCertificateCustomValidationCallback",
                BindingFlags.Public | BindingFlags.Instance);

        private static readonly bool SupportsPerHandlerCertificateCallback =
            HandlerCertificateCallbackProperty != null &&
            HandlerCertificateCallbackProperty.PropertyType == typeof(Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>);

        private readonly ILogger logger;
        private readonly bool debugLogging;

        public SunshineClient(ILogger logger, bool debugLogging)
        {
            this.logger = logger;
            this.debugLogging = debugLogging;
        }

        /// <summary>请求 Sunshine 把当前串流会话的捕获目标切换为游戏窗口。</summary>
        public SunshineResponse EnableWindowFollow(SunshineEndpoint endpoint, string mode, string windowTitle, int processId)
        {
            var body = BuildWindowFollowJson("enable", mode, windowTitle, processId);
            return SendWithRetry(endpoint, "POST", body, "enable");
        }

        /// <summary>请求 Sunshine 恢复整屏（显示器）捕获。</summary>
        public SunshineResponse DisableWindowFollow(SunshineEndpoint endpoint)
        {
            var body = BuildWindowFollowJson("disable", CaptureModes.Foreground, string.Empty, 0);
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
                lastResponse.EndpointDisplay = endpoint.DisplayUrl;

                WriteDebug(string.Format(
                    CultureInfo.InvariantCulture,
                    "Sunshine {0} 请求（第 {1} 次）：{2} {3} -> {4}",
                    operation,
                    attempt,
                    method,
                    endpoint.ApiUri,
                    lastResponse.HasResponse ? "HTTP " + lastResponse.StatusCode : "传输失败: " + lastResponse.TransportError));

                if (lastResponse.HasResponse)
                {
                    WriteDebug("Sunshine 响应正文：" + SunshineText.Truncate(lastResponse.Body));
                }

                if (attempt >= MaxAttempts || !ShouldRetry(lastResponse))
                {
                    break;
                }

                logger.Warn(string.Format(
                    "Sunshine {0} 请求失败（{1}），{2} 毫秒后重试一次。",
                    operation,
                    lastResponse.HasResponse ? "HTTP " + lastResponse.StatusCode : lastResponse.TransportError,
                    RetryDelayMs));
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
            if (SupportsPerHandlerCertificateCallback)
            {
                return ExecuteWithHttpClient(endpoint, method, body);
            }

            return ExecuteWithHttpWebRequest(endpoint, method, body);
        }

        /// <summary>
        /// 首选实现：HttpClient + 每 handler 证书回调（.NET Framework 4.7.1+）。
        /// </summary>
        private SunshineResponse ExecuteWithHttpClient(SunshineEndpoint endpoint, string method, string body)
        {
            using (var handler = new HttpClientHandler())
            {
                var callback = new Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>(
                    delegate (HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
                    {
                        return ValidateCertificate(request == null ? null : request.RequestUri, endpoint, errors);
                    });
                HandlerCertificateCallbackProperty.SetValue(handler, callback);

                using (var client = new HttpClient(handler, false))
                {
                    client.Timeout = TimeSpan.FromMilliseconds(TimeoutMs);

                    using (var request = new HttpRequestMessage(new HttpMethod(method), endpoint.ApiUri))
                    {
                        request.Headers.Authorization = CreateAuthorizationHeader(endpoint);
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
        /// 兼容实现（.NET Framework 4.6.2 ~ 4.7.0）：HttpWebRequest + 请求期间限定范围的全局证书回调。
        /// </summary>
        private SunshineResponse ExecuteWithHttpWebRequest(SunshineEndpoint endpoint, string method, string body)
        {
            lock (CertificateCallbackSync)
            {
                var previousCallback = ServicePointManager.ServerCertificateValidationCallback;
                ServicePointManager.ServerCertificateValidationCallback =
                    delegate (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
                    {
                        var webRequest = sender as HttpWebRequest;
                        var requestUri = webRequest == null ? null : webRequest.RequestUri;
                        if (endpoint.MatchesRequestUri(requestUri))
                        {
                            // 只对配置的 Sunshine 端点放行自签名/名称不匹配证书。
                            return true;
                        }

                        if (previousCallback != null)
                        {
                            return previousCallback(sender, certificate, chain, errors);
                        }

                        return errors == SslPolicyErrors.None;
                    };

                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(endpoint.ApiUri);
                    request.Method = method;
                    request.Timeout = TimeoutMs;
                    request.ReadWriteTimeout = TimeoutMs;
                    request.AllowAutoRedirect = false;
                    request.Headers[HttpRequestHeader.Authorization] = CreateAuthorizationHeader(endpoint).ToString();

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
                finally
                {
                    ServicePointManager.ServerCertificateValidationCallback = previousCallback;
                }
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

        private static SunshineResponse CreateTransportFailure(Exception exception)
        {
            var message = DescribeException(exception);

            if (exception is OperationCanceledException)
            {
                message = "请求超时（" + TimeoutMs + " 毫秒）";
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
        /// 仅接受“常规校验通过”或“请求目标就是配置的 Sunshine 端点”两种情况。
        /// 其它证书错误一律拒绝。
        /// </summary>
        private static bool ValidateCertificate(Uri requestUri, SunshineEndpoint endpoint, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            return endpoint != null && endpoint.MatchesRequestUri(requestUri);
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
            var credentials = (endpoint.Username ?? string.Empty) + ":" + (endpoint.Password ?? string.Empty);
            return new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        /// <summary>
        /// 手工构造请求 JSON（字段顺序与端点契约一致），避免引入 JSON 依赖。
        /// </summary>
        private static string BuildWindowFollowJson(string action, string mode, string windowTitle, int processId)
        {
            var builder = new StringBuilder(160);
            builder.Append('{');
            AppendJsonProperty(builder, "action", action, true);
            AppendJsonProperty(builder, "mode", mode, false);
            AppendJsonProperty(builder, "window_title", windowTitle, false);
            builder.Append(",\"process_id\":").Append(processId.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"session_id\":0");
            builder.Append('}');
            return builder.ToString();
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
            if (debugLogging)
            {
                logger.Debug(message);
            }
        }

        #endregion
    }
}
