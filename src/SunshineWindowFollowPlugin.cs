using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 方案4 配套 Playnite 插件：游戏启动/退出时联动 Sunshine，把当前串流会话的捕获目标
    /// 在“整块显示器”和“游戏窗口”之间切换。
    ///
    /// 依赖 Sunshine 侧已实现本地控制端点：
    ///   POST/GET https://127.0.0.1:47990/api/capture/window-follow
    /// 端点契约见 README 与 SunshineClient 注释。
    /// </summary>
    public class SunshineWindowFollowPlugin : GenericPlugin
    {
        /// <summary>
        /// 插件 GUID，必须与 extension.yaml 中 Id 的后半段（_ 后面的 GUID）一致。
        /// </summary>
        public static readonly Guid PluginId = Guid.Parse("c40d275b-5dad-4f8e-9640-137ec5267ef1");

        private const string NotificationId = "SunshineWindowFollow.ConnectionTest";

        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly SunshineSettingsViewModel settingsViewModel;
        private readonly SunshineController controller;
        private int streamActivationRequested;

        /// <summary>插件设置视图模型（设置界面与后台线程都从这里读取当前设置）。</summary>
        public SunshineSettingsViewModel SettingsViewModel
        {
            get { return settingsViewModel; }
        }

        public override Guid Id
        {
            get { return PluginId; }
        }

        public SunshineWindowFollowPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            settingsViewModel = new SunshineSettingsViewModel(this, logger);
            controller = new SunshineController(
                new PluginSettingsProvider(settingsViewModel),
                new SunshineClientTransport(logger),
                new PlayniteLoggerAdapter(logger));
            controller.ActiveStreamConfirmed += OnActiveStreamConfirmed;

            logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Sunshine Window Follow 插件已加载（插件Id={0}，运行模式={1}）",
                PluginId,
                PlayniteApi.ApplicationInfo.Mode));
        }

        #region Playnite 事件

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            try
            {
                // 启动时先做一次状态对账；只有确认存在活动串流会话后，
                // ActiveStreamConfirmed 回调才会尝试把 Playnite 切到前台。
                controller.QueryStatus();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "插件启动时查询 Sunshine 状态失败");
            }
        }

        private void OnActiveStreamConfirmed()
        {
            // 前台激活只做一次：状态对账可能多次确认同一个串流会话，
            // 但每次本地启动都无条件 SetForegroundWindow 会打断用户。
            if (Interlocked.Exchange(ref streamActivationRequested, 1) != 0)
            {
                return;
            }

            try
            {
                logger.Info("已确认存在活动串流会话，尝试将 Playnite 窗口切到前台");
                WindowActivation.ActivateOwnMainWindowAsync(logger);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "确认活动串流后激活 Playnite 窗口失败");
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            try
            {
                var gameName = args == null || args.Game == null ? string.Empty : args.Game.Name;
                var gameId = args == null || args.Game == null ? Guid.Empty : args.Game.Id;
                var processId = args == null ? 0 : args.StartedProcessId;
                logger.Info(string.Format(
                    CultureInfo.InvariantCulture,
                    "收到游戏启动事件：game=\"{0}\", game_id={1}, StartedProcessId={2}",
                    gameName,
                    gameId,
                    processId));

                controller.NotifyGameStarted(gameId, gameName, processId);
            }
            catch (Exception exception)
            {
                // 事件回调绝不能把异常抛回 Playnite；请求本身已经在后台线程完成。
                logger.Error(exception, "处理游戏启动事件失败（不影响游戏运行）");
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            try
            {
                var gameName = args == null || args.Game == null ? string.Empty : args.Game.Name;
                var gameId = args == null || args.Game == null ? Guid.Empty : args.Game.Id;
                logger.Info("收到游戏退出事件：game=\"" + gameName + "\", game_id=" + gameId);
                controller.NotifyGameStopped(gameId);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "处理游戏退出事件失败（不影响 Playnite）");
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            try
            {
                // Playnite 退出时如果游戏仍在运行，尽力恢复整屏捕获。
                controller.NotifyGameStopped();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Playnite 退出时恢复整屏捕获失败");
            }
        }

        #endregion

        #region 设置

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SunshineSettingsView(this);
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@",
                Description = "测试 Sunshine 连接",
                Action = RunConnectionTestFromMenu
            };
        }

        #endregion

        #region 测试连接

        /// <summary>
        /// 测试连接：先 GET 一次状态，并把结果格式化成中文说明。
        /// 供设置界面按钮与主菜单项共用；HTTP 请求是同步的，调用方必须放在后台线程。
        /// </summary>
        public ConnectionTestResult TestConnection(SunshineSettings settings)
        {
            if (settings == null)
            {
                return ConnectionTestResult.Fail("插件设置尚未加载");
            }

            SunshineEndpoint endpoint;
            string error;
            if (!SunshineEndpoint.TryCreate(settings, out endpoint, out error))
            {
                return ConnectionTestResult.Fail("Sunshine 地址无效：" + error);
            }

            var client = new SunshineClient(logger, settings.DebugLogging);
            var response = client.QueryStatus(endpoint);
            if (response == null)
            {
                return ConnectionTestResult.Fail("未收到 Sunshine 响应");
            }

            if (!response.HasResponse)
            {
                return ConnectionTestResult.Fail(string.Format(
                    "无法连接 {0}：{1}",
                    endpoint.DisplayUrl,
                    response.TransportError));
            }

            switch (response.StatusCode)
            {
                case 200:
                    if (response.Status == null)
                    {
                        return ConnectionTestResult.Fail(
                            "连接成功（HTTP 200），但响应不是可解析的 JSON：" +
                            SunshineText.Truncate(response.Body, 300));
                    }

                    return ConnectionTestResult.Ok(
                        "连接成功（HTTP 200）。" + response.Status.Describe());

                case 401:
                    return ConnectionTestResult.Fail("认证失败（HTTP 401），请检查 Sunshine WebUI 用户名和密码");

                case 403:
                    return ConnectionTestResult.Fail("请求被拒绝（HTTP 403）：该端点只允许本机访问，请确认 Playnite 与 Sunshine 在同一台机器上");

                case 404:
                    return ConnectionTestResult.Ok(
                        "已连通 Sunshine，但当前没有活动的串流会话（HTTP 404）。开始串流后再测试可看到完整状态。");

                case 409:
                    return ConnectionTestResult.Fail("Sunshine 报告存在多个活动串流会话或该模式不受支持（HTTP 409）");

                default:
                    return ConnectionTestResult.Fail(string.Format(
                        "Sunshine 返回 HTTP {0}：{1}",
                        response.StatusCode,
                        SunshineText.Truncate(response.Body, 300)));
            }
        }

        private void RunConnectionTestFromMenu(MainMenuItemActionArgs args)
        {
            var viewModel = settingsViewModel;
            var snapshot = viewModel == null || viewModel.Settings == null ? null : viewModel.Settings.Clone();

            Task.Run(() =>
            {
                try
                {
                    var result = TestConnection(snapshot);
                    ShowNotification(
                        "Sunshine 连接测试：" + result.Message,
                        result.Success ? NotificationType.Info : NotificationType.Error);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "主菜单“测试 Sunshine 连接”执行失败");
                    ShowNotification("Sunshine 连接测试异常：" + exception.Message, NotificationType.Error);
                }
            });
        }

        private void ShowNotification(string text, NotificationType type)
        {
            try
            {
                // 同一个 Id 的通知不会重复添加，先移除再添加以刷新内容。
                PlayniteApi.Notifications.Remove(NotificationId);
                PlayniteApi.Notifications.Add(NotificationId, text, type);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "显示 Playnite 通知失败");
            }
        }

        #endregion

        public override void Dispose()
        {
            try
            {
                controller.ActiveStreamConfirmed -= OnActiveStreamConfirmed;
            }
            catch (Exception exception)
            {
                logger.Error(exception, "取消活动串流确认回调失败");
            }

            try
            {
                controller.Dispose();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "释放 Sunshine 控制器失败");
            }

            base.Dispose();
        }

        #region 控制器适配器

        private sealed class PluginSettingsProvider : ISunshineSettingsProvider
        {
            private readonly SunshineSettingsViewModel viewModel;

            public PluginSettingsProvider(SunshineSettingsViewModel viewModel)
            {
                this.viewModel = viewModel;
            }

            public SunshineSettings GetSnapshot()
            {
                var settings = viewModel == null ? null : viewModel.Settings;
                return settings == null ? null : settings.Clone();
            }
        }

        private sealed class PlayniteLoggerAdapter : ISunshineLogger
        {
            private readonly ILogger logger;

            public PlayniteLoggerAdapter(ILogger logger)
            {
                this.logger = logger;
            }

            public void Debug(string message)
            {
                logger.Debug(message);
            }

            public void Info(string message)
            {
                logger.Info(message);
            }

            public void Warn(string message)
            {
                logger.Warn(message);
            }

            public void Error(string message)
            {
                logger.Error(message);
            }

            public void Error(Exception exception, string message)
            {
                logger.Error(exception, message);
            }
        }

        private sealed class SunshineClientTransport : ISunshineTransport
        {
            private readonly ILogger logger;

            public SunshineClientTransport(ILogger logger)
            {
                this.logger = logger;
            }

            public SunshineResponse EnableWindowFollow(
                SunshineEndpoint endpoint,
                string mode,
                string windowTitle,
                int processId,
                int sessionId,
                bool debugLogging)
            {
                return new SunshineClient(logger, debugLogging)
                    .EnableWindowFollow(endpoint, mode, windowTitle, processId, sessionId);
            }

            public SunshineResponse DisableWindowFollow(
                SunshineEndpoint endpoint,
                int sessionId,
                bool debugLogging)
            {
                return new SunshineClient(logger, debugLogging)
                    .DisableWindowFollow(endpoint, sessionId);
            }

            public SunshineResponse QueryStatus(SunshineEndpoint endpoint, bool debugLogging)
            {
                return new SunshineClient(logger, debugLogging).QueryStatus(endpoint);
            }
        }

        #endregion
    }
}
