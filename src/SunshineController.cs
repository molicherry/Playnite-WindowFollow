using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using Playnite.SDK;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 负责把 Playnite 的游戏启动/退出事件转换成对 Sunshine 的窗口跟随请求。
    ///
    /// 所有网络请求都在单独的后台线程上串行执行：
    ///   - 绝不会阻塞 Playnite 的主线程/游戏启动流程；
    ///   - 任何异常都在工作线程内捕获并写日志，不会抛回 Playnite；
    ///   - 游戏启动后延迟 StartDelayMs（默认 1.5 秒）再启用窗口跟随，等待游戏窗口出现；
    ///   - 如果延迟期间游戏已经退出（比如启动即崩溃），则取消这次 enable，避免“跟随”到一个不存在的窗口；
    ///   - 404（当前没有活动串流会话）视为正常情况，静默跳过。
    /// </summary>
    public sealed class SunshineController : IDisposable
    {
        private enum CaptureAction
        {
            Enable,
            Disable,
            Status
        }

        private sealed class CaptureCommand
        {
            public CaptureAction Action { get; set; }

            public int Generation { get; set; }

            public string GameName { get; set; }

            public int StartedProcessId { get; set; }
        }

        private readonly SunshineWindowFollowPlugin plugin;
        private readonly ILogger logger;
        private readonly BlockingCollection<CaptureCommand> queue = new BlockingCollection<CaptureCommand>();
        private readonly Thread worker;

        private int disposed;
        private int generation;
        private int desiredActive;

        // -1 = 未知（还没查询/未成功同步），0 = 整屏捕获，1 = 窗口跟随。
        private int knownActive = -1;

        public SunshineController(SunshineWindowFollowPlugin plugin, ILogger logger)
        {
            this.plugin = plugin;
            this.logger = logger;
            worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "SunshineWindowFollow.Worker"
            };
            worker.Start();
        }

        #region 事件入口（由 Playnite 调用，必须快速返回）

        /// <summary>游戏启动：延迟后请求 Sunshine 切换为窗口跟随。</summary>
        public void NotifyGameStarted(string gameName, int startedProcessId)
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            Volatile.Write(ref desiredActive, 1);
            Enqueue(new CaptureCommand
            {
                Action = CaptureAction.Enable,
                Generation = currentGeneration,
                GameName = gameName ?? string.Empty,
                StartedProcessId = startedProcessId
            });
        }

        /// <summary>游戏退出/Playnite 退出：请求 Sunshine 恢复整屏捕获。</summary>
        public void NotifyGameStopped()
        {
            Interlocked.Increment(ref generation);
            Volatile.Write(ref desiredActive, 0);
            Enqueue(new CaptureCommand { Action = CaptureAction.Disable });
        }

        /// <summary>插件启动时查询一次当前状态（仅记录日志）。</summary>
        public void QueryStatus()
        {
            Enqueue(new CaptureCommand { Action = CaptureAction.Status });
        }

        private void Enqueue(CaptureCommand command)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            try
            {
                queue.Add(command);
            }
            catch (InvalidOperationException)
            {
                // 队列已 CompleteAdding（正在退出），忽略即可。
            }
        }

        #endregion

        #region 工作线程

        private void WorkerLoop()
        {
            try
            {
                foreach (var command in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        ExecuteCommand(command);
                    }
                    catch (Exception exception)
                    {
                        // 工作线程里吞掉一切异常，保证队列继续工作。
                        logger.Error(exception, "处理 Sunshine 窗口跟随指令时发生未预期的错误");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ExecuteCommand(CaptureCommand command)
        {
            switch (command.Action)
            {
                case CaptureAction.Enable:
                    ExecuteEnable(command);
                    break;
                case CaptureAction.Disable:
                    ExecuteDisable();
                    break;
                case CaptureAction.Status:
                    ExecuteStatusQuery();
                    break;
            }
        }

        private void ExecuteEnable(CaptureCommand command)
        {
            var settings = GetSettingsSnapshot();
            if (settings == null)
            {
                logger.Error("插件设置尚未加载，跳过启用 Sunshine 窗口跟随");
                return;
            }

            if (!settings.Enabled)
            {
                WriteDebug("插件总开关已关闭，跳过启用窗口跟随");
                return;
            }

            // 等待游戏窗口出现。期间如果游戏退出或收到新指令，尽快取消。
            var delay = Math.Max(0, Math.Min(10000, settings.StartDelayMs));
            if (delay > 0 && !WaitWithCancellation(delay, command.Generation))
            {
                WriteDebug("等待游戏窗口期间游戏已退出或指令已过期，取消本次 enable");
                return;
            }

            if (command.Generation != Volatile.Read(ref generation) || Volatile.Read(ref desiredActive) != 1)
            {
                WriteDebug("enable 指令已过期（已有更新的启动/退出事件），取消");
                return;
            }

            SunshineEndpoint endpoint;
            string error;
            if (!SunshineEndpoint.TryCreate(settings, out endpoint, out error))
            {
                logger.Error("Sunshine 地址无效，无法启用窗口跟随：" + error);
                return;
            }

            var mode = CaptureModes.Normalize(settings.CaptureMode);
            var windowTitle = string.Empty;
            var processId = 0;

            if (mode == CaptureModes.Title)
            {
                windowTitle = (settings.WindowTitle ?? string.Empty).Trim();
                if (windowTitle.Length == 0)
                {
                    windowTitle = command.GameName;
                }

                if (windowTitle.Length == 0)
                {
                    logger.Warn("捕获模式为 title，但没有可用的窗口标题，回退为 foreground 模式");
                    mode = CaptureModes.Foreground;
                }
            }
            else if (mode == CaptureModes.Pid)
            {
                processId = settings.ProcessId > 0 ? settings.ProcessId : command.StartedProcessId;
                if (processId <= 0)
                {
                    logger.Warn("捕获模式为 pid，但没有可用的进程 ID，回退为 foreground 模式");
                    mode = CaptureModes.Foreground;
                }
            }

            logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "游戏已启动，请求 Sunshine 启用窗口跟随（mode={0}, game=\"{1}\", process_id={2}）",
                mode,
                command.GameName,
                processId));

            var client = new SunshineClient(logger, settings.DebugLogging);
            var response = client.EnableWindowFollow(endpoint, mode, windowTitle, processId);
            HandleResponse(response, "enable");
        }

        private void ExecuteDisable()
        {
            var settings = GetSettingsSnapshot();
            if (settings == null)
            {
                logger.Error("插件设置尚未加载，无法恢复 Sunshine 整屏捕获");
                return;
            }

            if (!settings.Enabled && Volatile.Read(ref knownActive) != 1)
            {
                WriteDebug("插件未启用且当前未处于窗口跟随状态，跳过 disable");
                return;
            }

            if (Volatile.Read(ref knownActive) == 0)
            {
                WriteDebug("Sunshine 当前已是整屏捕获（已知状态），跳过重复 disable");
                return;
            }

            SunshineEndpoint endpoint;
            string error;
            if (!SunshineEndpoint.TryCreate(settings, out endpoint, out error))
            {
                logger.Error("Sunshine 地址无效，无法恢复整屏捕获：" + error);
                return;
            }

            logger.Info("游戏已退出，请求 Sunshine 恢复整屏捕获");
            var client = new SunshineClient(logger, settings.DebugLogging);
            var response = client.DisableWindowFollow(endpoint);
            HandleResponse(response, "disable");
        }

        private void ExecuteStatusQuery()
        {
            var settings = GetSettingsSnapshot();
            if (settings == null)
            {
                WriteDebug("插件设置尚未加载，跳过状态查询");
                return;
            }

            if (!settings.Enabled)
            {
                WriteDebug("插件总开关已关闭，跳过启动状态查询");
                return;
            }

            SunshineEndpoint endpoint;
            string error;
            if (!SunshineEndpoint.TryCreate(settings, out endpoint, out error))
            {
                logger.Error("Sunshine 地址无效，跳过启动状态查询：" + error);
                return;
            }

            WriteDebug("插件启动，查询 Sunshine 窗口跟随状态: " + endpoint.DisplayUrl);
            var client = new SunshineClient(logger, settings.DebugLogging);
            var response = client.QueryStatus(endpoint);
            HandleResponse(response, "status");
        }

        /// <summary>
        /// 可被“游戏退出/插件关闭”提前打断的等待。
        /// </summary>
        private bool WaitWithCancellation(int totalMilliseconds, int expectedGeneration)
        {
            var remaining = totalMilliseconds;
            while (remaining > 0)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    Volatile.Read(ref desiredActive) != 1 ||
                    expectedGeneration != Volatile.Read(ref generation))
                {
                    return false;
                }

                var slice = Math.Min(remaining, 100);
                Thread.Sleep(slice);
                remaining -= slice;
            }

            return Volatile.Read(ref disposed) == 0 &&
                   Volatile.Read(ref desiredActive) == 1 &&
                   expectedGeneration == Volatile.Read(ref generation);
        }

        #endregion

        #region 结果处理

        private void HandleResponse(SunshineResponse response, string operation)
        {
            if (response == null)
            {
                logger.Error("Sunshine " + operation + " 请求没有返回结果");
                return;
            }

            if (!response.HasResponse)
            {
                logger.Error(string.Format(
                    "Sunshine {0} 请求失败（端点 {1}，共 {2} 次）：{3}",
                    operation,
                    response.EndpointDisplay,
                    response.Attempts,
                    response.TransportError));
                return;
            }

            switch (response.StatusCode)
            {
                case 200:
                    if (response.Status == null)
                    {
                        logger.Warn("Sunshine 返回 HTTP 200，但响应不是可解析的 JSON：" +
                                    SunshineText.Truncate(response.Body));
                        return;
                    }

                    Volatile.Write(ref knownActive, response.Status.Active ? 1 : 0);

                    if (string.Equals(operation, "status", StringComparison.Ordinal) && !response.Status.Active)
                    {
                        logger.Info("Sunshine 当前状态：" + response.Status.Describe());
                    }
                    else
                    {
                        logger.Info("Sunshine " + operation + " 完成：" + response.Status.Describe());
                    }

                    if (!response.Status.Ok && !string.IsNullOrWhiteSpace(response.Status.Error))
                    {
                        logger.Warn("Sunshine 返回 ok=false：" + response.Status.Error);
                    }

                    break;

                case 404:
                    // 当前没有活动的串流会话：属于正常情况，静默跳过。
                    Volatile.Write(ref knownActive, 0);
                    WriteDebug("Sunshine 当前没有活动的串流会话（HTTP 404），跳过 " + operation);
                    break;

                case 401:
                    logger.Error("Sunshine 认证失败（HTTP 401），请在插件设置中检查 WebUI 用户名和密码");
                    break;

                case 403:
                    logger.Error("Sunshine 拒绝了请求（HTTP 403）：该端点只允许本机访问，请确认 Playnite 与 Sunshine 在同一台机器上");
                    break;

                case 400:
                    logger.Error("Sunshine 参数错误（HTTP 400）：" + SunshineText.Truncate(response.Body));
                    break;

                case 409:
                    logger.Warn("Sunshine 报告存在多个活动串流会话或该模式不受支持（HTTP 409），已跳过 " + operation);
                    break;

                default:
                    logger.Error(string.Format(
                        "Sunshine 服务端错误（HTTP {0}）：{1}",
                        response.StatusCode,
                        SunshineText.Truncate(response.Body)));
                    break;
            }
        }

        #endregion

        #region 辅助

        private SunshineSettings GetSettingsSnapshot()
        {
            var viewModel = plugin.SettingsViewModel;
            var settings = viewModel == null ? null : viewModel.Settings;
            return settings == null ? null : settings.Clone();
        }

        private void WriteDebug(string message)
        {
            var viewModel = plugin.SettingsViewModel;
            var settings = viewModel == null ? null : viewModel.Settings;
            if (settings != null && settings.DebugLogging)
            {
                logger.Debug(message);
            }
        }

        #endregion

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }

            try
            {
                queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                // 等待工作线程收尾（正在进行的 HTTP 请求最多约 2 次 x 3 秒超时）。
                worker.Join(1000);
            }
            catch (Exception)
            {
            }

            // Playnite 退出时如果仍处于窗口跟随状态，尽力恢复整屏捕获。
            if (Volatile.Read(ref knownActive) == 1)
            {
                try
                {
                    var settings = GetSettingsSnapshot();
                    SunshineEndpoint endpoint;
                    string error;
                    if (settings != null && SunshineEndpoint.TryCreate(settings, out endpoint, out error))
                    {
                        var client = new SunshineClient(logger, settings.DebugLogging);
                        var response = client.DisableWindowFollow(endpoint);
                        if (response != null && response.HasResponse && response.StatusCode == 200)
                        {
                            Volatile.Write(ref knownActive, 0);
                            logger.Info("Playnite 退出前已恢复 Sunshine 整屏捕获");
                        }
                    }
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Playnite 退出前恢复 Sunshine 整屏捕获失败");
                }
            }
        }
    }
}
