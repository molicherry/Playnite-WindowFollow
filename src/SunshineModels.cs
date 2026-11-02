using System;
using System.Text;

namespace SunshineWindowFollow
{
    /// <summary>
    /// Sunshine 窗口跟随端点的捕获模式常量，必须与端点契约保持一致：
    /// foreground = 跟随前台窗口（推荐），title = 按窗口标题匹配，pid = 按进程 ID 匹配。
    /// </summary>
    public static class CaptureModes
    {
        public const string Foreground = "foreground";
        public const string Title = "title";
        public const string Pid = "pid";

        public static readonly string[] All = { Foreground, Title, Pid };

        public static bool IsValid(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                return false;
            }

            return string.Equals(mode, Foreground, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, Title, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, Pid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把设置里保存的模式规范化为契约中的小写值，无效值回退到 foreground。
        /// </summary>
        public static string Normalize(string mode)
        {
            if (string.Equals(mode, Title, StringComparison.OrdinalIgnoreCase))
            {
                return Title;
            }

            if (string.Equals(mode, Pid, StringComparison.OrdinalIgnoreCase))
            {
                return Pid;
            }

            return Foreground;
        }

        public static string GetDisplayName(string mode)
        {
            switch (Normalize(mode))
            {
                case Title:
                    return "title（按窗口标题匹配）";
                case Pid:
                    return "pid（按进程 ID 匹配）";
                default:
                    return "foreground（跟随前台窗口，推荐）";
            }
        }
    }

    /// <summary>
    /// 状态响应里的 window 对象。
    /// </summary>
    public class WindowFollowWindowInfo
    {
        public string Title { get; set; }

        public int ProcessId { get; set; }

        public string Hwnd { get; set; }

        public string Describe()
        {
            if (string.IsNullOrWhiteSpace(Title) && ProcessId <= 0)
            {
                return "未报告窗口信息";
            }

            return string.Format(
                "title=\"{0}\", process_id={1}, hwnd={2}",
                Title ?? string.Empty,
                ProcessId,
                string.IsNullOrWhiteSpace(Hwnd) ? "-" : Hwnd);
        }
    }

    /// <summary>
    /// POST/GET /api/capture/window-follow 返回的状态结构。
    /// 解析使用插件内置的 JsonLite（见 JsonLite.cs），不依赖宿主或第三方 JSON 库。
    /// </summary>
    public class WindowFollowStatus
    {
        public bool Ok { get; set; }

        public bool Active { get; set; }

        public string Mode { get; set; }

        public int SessionId { get; set; }

        public string ClientUuid { get; set; }

        public WindowFollowWindowInfo Window { get; set; }

        /// <summary>endpoint 提供的回退原因（通常是字符串或 null）。</summary>
        public string Fallback { get; set; }

        public string Error { get; set; }

        /// <summary>
        /// 解析端点返回的 JSON。任何格式问题都返回 null，由调用方按“无法解析”处理。
        /// </summary>
        public static WindowFollowStatus FromJson(string json)
        {
            JsonValue root;
            if (!JsonLite.TryParse(json, out root) || root.Kind != JsonKind.Object)
            {
                return null;
            }

            var status = new WindowFollowStatus
            {
                Ok = root.GetBool("ok"),
                Active = root.GetBool("active"),
                Mode = root.GetString("mode"),
                SessionId = root.GetInt("session_id"),
                ClientUuid = root.GetString("client_uuid"),
                Error = root.GetString("error")
            };

            var window = root.GetObject("window");
            if (window != null)
            {
                status.Window = new WindowFollowWindowInfo
                {
                    Title = window.GetString("title"),
                    ProcessId = window.GetInt("process_id"),
                    Hwnd = window.GetString("hwnd")
                };
            }

            var fallback = root.Get("fallback");
            if (fallback != null && fallback.Kind != JsonKind.Null)
            {
                status.Fallback = fallback.ToDisplayString();
            }

            return status;
        }

        public string Describe()
        {
            var builder = new StringBuilder();
            builder.Append("ok=").Append(Ok);
            builder.Append(", active=").Append(Active);
            builder.Append(", mode=").Append(string.IsNullOrWhiteSpace(Mode) ? "-" : Mode);
            builder.Append(", session_id=").Append(SessionId);

            if (!string.IsNullOrWhiteSpace(ClientUuid))
            {
                builder.Append(", client_uuid=").Append(ClientUuid);
            }

            if (Window != null)
            {
                builder.Append(", window[").Append(Window.Describe()).Append(']');
            }

            if (!string.IsNullOrWhiteSpace(Error))
            {
                builder.Append(", error=").Append(Error);
            }

            if (Fallback != null)
            {
                builder.Append(", fallback=").Append(Convert.ToString(Fallback));
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// 一次 HTTP 调用的结果，包含传输层失败（没有收到 HTTP 响应）与 HTTP 状态码。
    /// </summary>
    public sealed class SunshineResponse
    {
        /// <summary>是否收到了 HTTP 响应（false 表示连接失败/超时/证书被拒绝）。</summary>
        public bool HasResponse { get; set; }

        /// <summary>HTTP 状态码，HasResponse 为 false 时无意义。</summary>
        public int StatusCode { get; set; }

        /// <summary>响应正文（可能为空）。</summary>
        public string Body { get; set; }

        /// <summary>传输层错误描述，HasResponse 为 true 时为 null。</summary>
        public string TransportError { get; set; }

        /// <summary>解析后的状态结构，解析失败时为 null。</summary>
        public WindowFollowStatus Status { get; set; }

        /// <summary>实际发起的请求次数（含重试）。</summary>
        public int Attempts { get; set; }

        /// <summary>请求的端点地址，仅用于日志。</summary>
        public string EndpointDisplay { get; set; }
    }

    /// <summary>
    /// “测试连接”结果。
    /// </summary>
    public sealed class ConnectionTestResult
    {
        public bool Success { get; set; }

        public string Message { get; set; }

        public static ConnectionTestResult Ok(string message)
        {
            return new ConnectionTestResult { Success = true, Message = message };
        }

        public static ConnectionTestResult Fail(string message)
        {
            return new ConnectionTestResult { Success = false, Message = message };
        }
    }

    /// <summary>
    /// 日志/提示文本的小工具。
    /// </summary>
    public static class SunshineText
    {
        public const int DefaultMaxLength = 800;

        /// <summary>
        /// 截断过长的文本（例如异常 HTML 页面），避免把日志写爆。
        /// </summary>
        public static string Truncate(string value, int maxLength = DefaultMaxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (maxLength <= 0)
            {
                maxLength = DefaultMaxLength;
            }

            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...(已截断)";
        }
    }
}
