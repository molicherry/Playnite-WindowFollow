using System;
using System.Collections.Generic;
using Playnite.SDK;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 设置视图模型。Playnite 在插件设置窗口打开时调用 BeginEdit，
    /// 确认时调用 VerifySettings + EndEdit，取消时调用 CancelEdit。
    /// 设置通过 Plugin.LoadPluginSettings / SavePluginSettings 持久化，
    /// Playnite 会保存到 ExtensionsData\{插件Id}\config.json。
    /// </summary>
    public class SunshineSettingsViewModel : ObservableObject, ISettings
    {
        private readonly SunshineWindowFollowPlugin plugin;
        private readonly ILogger logger;

        private SunshineSettings editingClone;
        private SunshineSettings settings;

        public SunshineSettings Settings
        {
            get { return settings; }
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public SunshineSettingsViewModel(SunshineWindowFollowPlugin plugin, ILogger logger)
        {
            this.plugin = plugin;
            this.logger = logger;

            SunshineSettings saved = null;
            try
            {
                saved = plugin.LoadPluginSettings<SunshineSettings>();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "读取插件设置失败，将使用默认设置");
            }

            Settings = saved ?? new SunshineSettings();
        }

        public void BeginEdit()
        {
            editingClone = Settings == null ? null : Settings.Clone();
        }

        public void CancelEdit()
        {
            if (editingClone != null)
            {
                Settings = editingClone;
            }
        }

        public void EndEdit()
        {
            try
            {
                plugin.SavePluginSettings(Settings);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "保存插件设置失败");
            }
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (Settings == null)
            {
                errors.Add("设置对象为空");
                return false;
            }

            if (Settings.Enabled)
            {
                SunshineEndpoint endpoint;
                string error;
                if (!SunshineEndpoint.TryCreate(Settings, out endpoint, out error))
                {
                    errors.Add("Sunshine 地址无效：" + error);
                }
            }

            if (!CaptureModes.IsValid(Settings.CaptureMode))
            {
                errors.Add("捕获模式无效：仅支持 foreground / title / pid");
            }

            if (Settings.ProcessId < 0)
            {
                errors.Add("进程 ID 不能为负数");
            }

            if (Settings.StartDelayMs < 0 || Settings.StartDelayMs > 10000)
            {
                errors.Add("启动延迟必须在 0 ~ 10000 毫秒之间");
            }

            return errors.Count == 0;
        }
    }
}
