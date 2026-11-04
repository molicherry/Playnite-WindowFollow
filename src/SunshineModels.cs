using System;
using System.Collections.Generic;
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
    /// 状态响应里 observed.window_* 字段对应的窗口对象。
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
    /// 某个串流会话当前的窗口跟随请求（Sunshine 响应里的 override 对象）。
    /// </summary>
    public sealed class WindowFollowOverride
    {
        public bool Enabled { get; set; }

        public string Mode { get; set; }

        public string WindowTitle { get; set; }

        public int ProcessId { get; set; }

        public static WindowFollowOverride FromJson(JsonValue node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            return new WindowFollowOverride
            {
                Enabled = node.GetBool("enabled"),
                Mode = node.GetString("mode"),
                WindowTitle = node.GetString("window_title"),
                ProcessId = node.GetInt("process_id")
            };
        }

        public string Describe()
        {
            return string.Format(
                "enabled={0}, mode={1}, window_title=\"{2}\", process_id={3}",
                Enabled,
                string.IsNullOrWhiteSpace(Mode) ? "-" : Mode,
                WindowTitle ?? string.Empty,
                ProcessId);
        }
    }

    /// <summary>
    /// 捕获后端实际观察到的窗口状态（Sunshine 响应里的 observed 对象）。
    /// 注意：observed.active 表示“当前确实在捕获窗口”，并不代表用户请求的
    /// override.enabled；插件的启用状态以 override.enabled 为准。
    /// </summary>
    public sealed class WindowFollowObserved
    {
        public bool Active { get; set; }

        public bool FallbackToDisplay { get; set; }

        public string WindowTitle { get; set; }

        public int ProcessId { get; set; }

        public string Hwnd { get; set; }

        public string FallbackReason { get; set; }

        public static WindowFollowObserved FromJson(JsonValue node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            return new WindowFollowObserved
            {
                Active = node.GetBool("active"),
                FallbackToDisplay = node.GetBool("fallback_to_display"),
                WindowTitle = node.GetString("window_title"),
                ProcessId = node.GetInt("process_id"),
                Hwnd = ReadScalarString(node.Get("hwnd")),
                FallbackReason = node.GetString("fallback_reason")
            };
        }

        internal static string ReadScalarString(JsonValue value)
        {
            if (value == null || value.Kind == JsonKind.Null)
            {
                return null;
            }

            if (value.Kind == JsonKind.String)
            {
                return value.StringValue;
            }

            return value.ToDisplayString();
        }

        public string Describe()
        {
            return string.Format(
                "active={0}, fallback_to_display={1}, window_title=\"{2}\", process_id={3}, hwnd={4}, fallback_reason={5}",
                Active,
                FallbackToDisplay,
                WindowTitle ?? string.Empty,
                ProcessId,
                string.IsNullOrWhiteSpace(Hwnd) ? "-" : Hwnd,
                string.IsNullOrWhiteSpace(FallbackReason) ? "-" : FallbackReason);
        }
    }

    /// <summary>
    /// 响应 sessions 数组中的一个串流会话。保留完整列表是为了后续按
    /// selected_session_id 重新同步（例如 enable 后查询/关闭同一个会话）。
    /// </summary>
    public sealed class WindowFollowSession
    {
        public int SessionId { get; set; }

        public string ClientUuid { get; set; }

        public string ClientName { get; set; }

        public string AppName { get; set; }

        public bool Running { get; set; }

        public bool ControlOnly { get; set; }

        /// <summary>某些响应会用 selected 标记当前选中的会话（兼容字段）。</summary>
        public bool Selected { get; set; }

        public WindowFollowOverride Override { get; set; }

        public WindowFollowObserved Observed { get; set; }

        public static WindowFollowSession FromJson(JsonValue node)
        {
            if (node == null || node.Kind != JsonKind.Object)
            {
                return null;
            }

            return new WindowFollowSession
            {
                SessionId = node.GetInt("session_id"),
                ClientUuid = node.GetString("client_uuid"),
                ClientName = node.GetString("client_name"),
                AppName = node.GetString("app_name"),
                Running = node.GetBool("running"),
                ControlOnly = node.GetBool("control_only"),
                Selected = node.GetBool("selected"),
                Override = WindowFollowOverride.FromJson(node.GetObject("override")),
                Observed = WindowFollowObserved.FromJson(node.GetObject("observed"))
            };
        }

        public string Describe()
        {
            return string.Format(
                "session_id={0}, client_uuid={1}, app_name={2}, running={3}, override[{4}], observed[{5}]",
                SessionId,
                string.IsNullOrWhiteSpace(ClientUuid) ? "-" : ClientUuid,
                string.IsNullOrWhiteSpace(AppName) ? "-" : AppName,
                Running,
                Override == null ? "-" : Override.Describe(),
                Observed == null ? "-" : Observed.Describe());
        }
    }

    /// <summary>
    /// POST/GET /api/capture/window-follow 返回的状态结构。
    ///
    /// 真实响应形如：
    /// {
    ///   "ok": true,
    ///   "sessions": [
    ///     { "session_id": 1, "client_uuid": "...",
    ///       "override": { "enabled": true, "mode": "foreground", ... },
    ///       "observed": { "active": true, "window_title": "...", ... } }
    ///   ],
    ///   "selected_session_id": 1,
    ///   "selected_client_uuid": "..."
    /// }
    ///
    /// 顶层没有 active/mode/session_id/window/fallback 字段；这些值必须从
    /// selected_session_id 选中的会话里读取。解析使用插件内置的 JsonLite。
    /// </summary>
    public class WindowFollowStatus
    {
        public bool Ok { get; set; }

        /// <summary>
        /// 用户请求的启用状态，映射自选中会话的 override.enabled。
        /// 不是 observed.active（后者只表示捕获后端当前是否成功跟随窗口）。
        /// </summary>
        public bool Active { get; set; }

        /// <summary>选中会话的 override.mode。</summary>
        public string Mode { get; set; }

        /// <summary>selected_session_id；缺失时回退到选中/唯一会话的 session_id。</summary>
        public int SessionId { get; set; }

        /// <summary>selected_client_uuid；缺失时回退到选中/唯一会话的 client_uuid。</summary>
        public string ClientUuid { get; set; }

        /// <summary>选中会话 observed.window_* 对应的窗口信息。</summary>
        public WindowFollowWindowInfo Window { get; set; }

        /// <summary>选中会话 observed.fallback_reason。</summary>
        public string Fallback { get; set; }

        /// <summary>选中会话 observed.fallback_to_display。</summary>
        public bool FallbackToDisplay { get; set; }

        public string Error { get; set; }

        public bool Supported { get; set; }

        public string Reason { get; set; }

        public string Feature { get; set; }

        /// <summary>完整会话列表，供调用方按 session_id 重新同步。</summary>
        public List<WindowFollowSession> Sessions { get; set; }

        /// <summary>本次选中会话的对象（可能为 null）。</summary>
        public WindowFollowSession SelectedSession { get; set; }

        public WindowFollowStatus()
        {
            Sessions = new List<WindowFollowSession>();
            Supported = true;
        }

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
                Error = root.GetString("error"),
                Feature = root.GetString("feature"),
                Reason = root.GetString("reason")
            };

            var supported = root.Get("supported");
            if (supported != null && supported.Kind == JsonKind.Boolean)
            {
                status.Supported = supported.BooleanValue;
            }

            var sessionsNode = root.Get("sessions");
            if (sessionsNode != null && sessionsNode.Kind == JsonKind.Array)
            {
                for (var index = 0; index < sessionsNode.Count; index++)
                {
                    var session = WindowFollowSession.FromJson(sessionsNode.GetAt(index));
                    if (session != null)
                    {
                        status.Sessions.Add(session);
                    }
                }
            }

            var selectedSessionId = root.GetInt("selected_session_id");
            var selectedClientUuid = root.GetString("selected_client_uuid");

            var selected = FindSessionById(status.Sessions, selectedSessionId);
            if (selected == null && !string.IsNullOrEmpty(selectedClientUuid))
            {
                selected = FindSessionByClientUuid(status.Sessions, selectedClientUuid);
            }

            if (selected == null)
            {
                selected = FindMarkedSession(status.Sessions);
            }

            // GET 响应可能没有任何 selected_* 字段；只有一个会话时它显然就是
            // 当前会话。多个会话且没有 selected 信息时不会猜测，避免串会话。
            if (selected == null && selectedSessionId <= 0 && status.Sessions.Count == 1)
            {
                selected = status.Sessions[0];
            }

            if (selected == null)
            {
                selected = WindowFollowSession.FromJson(
                    root.GetObject("selected_session") ??
                    root.GetObject("selected") ??
                    root.GetObject("session"));
                if (selected != null && FindSessionById(status.Sessions, selected.SessionId) == null)
                {
                    status.Sessions.Add(selected);
                }
            }

            status.SelectedSession = selected;
            status.SessionId = selectedSessionId > 0
                ? selectedSessionId
                : (selected == null ? 0 : selected.SessionId);
            status.ClientUuid = !string.IsNullOrEmpty(selectedClientUuid)
                ? selectedClientUuid
                : (selected == null ? null : selected.ClientUuid);

            if (selected != null)
            {
                if (selected.Override != null)
                {
                    status.Active = selected.Override.Enabled;
                    status.Mode = selected.Override.Mode;
                }

                if (selected.Observed != null)
                {
                    status.Fallback = selected.Observed.FallbackReason;
                    status.FallbackToDisplay = selected.Observed.FallbackToDisplay;
                    status.Window = new WindowFollowWindowInfo
                    {
                        Title = selected.Observed.WindowTitle,
                        ProcessId = selected.Observed.ProcessId,
                        Hwnd = selected.Observed.Hwnd
                    };
                }
            }

            return status;
        }

        private static WindowFollowSession FindSessionById(List<WindowFollowSession> sessions, int sessionId)
        {
            if (sessions == null || sessionId <= 0)
            {
                return null;
            }

            foreach (var session in sessions)
            {
                if (session != null && session.SessionId == sessionId)
                {
                    return session;
                }
            }

            return null;
        }

        private static WindowFollowSession FindSessionByClientUuid(List<WindowFollowSession> sessions, string clientUuid)
        {
            if (sessions == null || string.IsNullOrEmpty(clientUuid))
            {
                return null;
            }

            foreach (var session in sessions)
            {
                if (session != null &&
                    string.Equals(session.ClientUuid, clientUuid, StringComparison.OrdinalIgnoreCase))
                {
                    return session;
                }
            }

            return null;
        }

        private static WindowFollowSession FindMarkedSession(List<WindowFollowSession> sessions)
        {
            if (sessions == null)
            {
                return null;
            }

            foreach (var session in sessions)
            {
                if (session != null && session.Selected)
                {
                    return session;
                }
            }

            return null;
        }

        public string Describe()
        {
            var builder = new StringBuilder();
            builder.Append("ok=").Append(Ok);

            if (!Supported)
            {
                builder.Append(", supported=false");
                if (!string.IsNullOrWhiteSpace(Reason))
                {
                    builder.Append(", reason=").Append(Reason);
                }
            }

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
