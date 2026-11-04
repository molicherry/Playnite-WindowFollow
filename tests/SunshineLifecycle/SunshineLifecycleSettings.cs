#if SUNSHINE_LIFECYCLE_TESTS
namespace SunshineWindowFollow
{
    /// <summary>
    /// 测试替身：只保留控制器需要的设置字段，避免把 Playnite SDK/WPF 依赖带进生命周期测试。
    /// </summary>
    public class SunshineSettings
    {
        public SunshineSettings()
        {
            Enabled = true;
            ServerUrl = "https://127.0.0.1:47990";
            Username = string.Empty;
            Password = string.Empty;
            CaptureMode = "foreground";
            WindowTitle = string.Empty;
            StartDelayMs = 0;
        }

        public bool Enabled { get; set; }

        public string ServerUrl { get; set; }

        public string Username { get; set; }

        public string Password { get; set; }

        public bool DebugLogging { get; set; }

        public string CaptureMode { get; set; }

        public string WindowTitle { get; set; }

        public int ProcessId { get; set; }

        public int StartDelayMs { get; set; }

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
#endif
