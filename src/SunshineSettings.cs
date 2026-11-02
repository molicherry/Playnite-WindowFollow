using System;
using System.Collections.Generic;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 插件设置。Playnite 会把此对象序列化成
    /// %AppData%\Playnite\ExtensionsData\{插件Id}\config.json
    /// （便携版为 {Playnite目录}\ExtensionsData\{插件Id}\config.json）。
    /// 注意：当前 Playnite SDK 没有提供加密存储能力，密码以明文保存，README 中有安全说明。
    /// </summary>
    public class SunshineSettings : ObservableObject
    {
        public const string DefaultServerUrl = "https://127.0.0.1:47990";
        public const int DefaultStartDelayMs = 1500;

        private bool enabled = true;
        private string serverUrl = DefaultServerUrl;
        private string username = string.Empty;
        private string password = string.Empty;
        private bool debugLogging = false;
        private string captureMode = CaptureModes.Foreground;
        private string windowTitle = string.Empty;
        private int processId = 0;
        private int startDelayMs = DefaultStartDelayMs;

        /// <summary>总开关：关闭后插件不会发起任何请求。</summary>
        public bool Enabled
        {
            get { return enabled; }
            set { SetValue(ref enabled, value); }
        }

        /// <summary>Sunshine WebUI 地址，默认 https://127.0.0.1:47990。</summary>
        public string ServerUrl
        {
            get { return serverUrl; }
            set { SetValue(ref serverUrl, value); }
        }

        /// <summary>Sunshine WebUI 用户名（HTTP Basic 认证）。</summary>
        public string Username
        {
            get { return username; }
            set { SetValue(ref username, value); }
        }

        /// <summary>Sunshine WebUI 密码（HTTP Basic 认证，以明文保存在 config.json）。</summary>
        public string Password
        {
            get { return password; }
            set { SetValue(ref password, value); }
        }

        /// <summary>是否输出调试日志（请求/响应详情）。</summary>
        public bool DebugLogging
        {
            get { return debugLogging; }
            set { SetValue(ref debugLogging, value); }
        }

        /// <summary>捕获模式：foreground / title / pid。</summary>
        public string CaptureMode
        {
            get { return captureMode; }
            set { SetValue(ref captureMode, value); }
        }

        /// <summary>mode=title 时使用的窗口标题；留空则使用 Playnite 启动的游戏名称。</summary>
        public string WindowTitle
        {
            get { return windowTitle; }
            set { SetValue(ref windowTitle, value); }
        }

        /// <summary>mode=pid 时使用的进程 ID；小于等于 0 时使用 Playnite 报告的 StartedProcessId。</summary>
        public int ProcessId
        {
            get { return processId; }
            set { SetValue(ref processId, value); }
        }

        /// <summary>游戏启动后等待窗口出现的毫秒数，默认 1500ms。</summary>
        public int StartDelayMs
        {
            get { return startDelayMs; }
            set { SetValue(ref startDelayMs, value); }
        }

        /// <summary>
        /// 复制一份设置，供后台线程安全读取（避免与设置界面同时读写同一个对象）。
        /// </summary>
        public SunshineSettings Clone()
        {
            return new SunshineSettings
            {
                Enabled = Enabled,
                ServerUrl = ServerUrl,
                Username = Username,
                Password = Password,
                DebugLogging = DebugLogging,
                CaptureMode = CaptureMode,
                WindowTitle = WindowTitle,
                ProcessId = ProcessId,
                StartDelayMs = StartDelayMs
            };
        }
    }
}
