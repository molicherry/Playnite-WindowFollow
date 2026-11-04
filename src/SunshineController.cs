using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace SunshineWindowFollow
{
    /// <summary>
    /// 控制器依赖的最小接口。真实的 Playnite 适配器在插件文件中实现，
    /// 这样控制器的生命周期逻辑可以脱离 Playnite/WPF 单独测试。
    /// </summary>
    internal interface ISunshineSettingsProvider
    {
        /// <summary>返回一份设置快照（实现方必须 Clone，避免后台线程与设置界面竞争）。</summary>
        SunshineSettings GetSnapshot();
    }

    internal interface ISunshineLogger
    {
        void Debug(string message);

        void Info(string message);

        void Warn(string message);

        void Error(string message);

        void Error(Exception exception, string message);
    }

    /// <summary>
    /// 对 Sunshine 端点的同步调用。所有调用只能发生在控制器的工作线程上。
    /// sessionId 大于 0 时表示定向到该串流会话；0 表示“由服务端选择唯一会话”。
    /// </summary>
    internal interface ISunshineTransport
    {
        SunshineResponse EnableWindowFollow(
            SunshineEndpoint endpoint,
            string mode,
            string windowTitle,
            int processId,
            int sessionId,
            bool debugLogging);

        SunshineResponse DisableWindowFollow(
            SunshineEndpoint endpoint,
            int sessionId,
            bool debugLogging);

        SunshineResponse QueryStatus(
            SunshineEndpoint endpoint,
            bool debugLogging);
    }

    /// <summary>
    /// 负责把 Playnite 的游戏启动/退出事件转换成对 Sunshine 的窗口跟随请求。
    ///
    /// 所有网络请求都在唯一一个后台 worker 上串行执行：
    ///   - 按 Game.Id 跟踪同时运行的游戏，只有最后一个游戏退出才 disable；
    ///   - enable 携带 Game.Id/代次，disable 也携带所有者/代次，过期指令直接丢弃；
    ///   - enable 成功后保存 session_id，之后 disable 定向到该会话，避免第二个客户端
    ///     加入后“唯一会话选择”返回 409 时无法恢复；
    ///   - 404/连接失败/启动探测使用有界、低频的 reconciliation，不在无串流本地游玩时轮询；
    ///   - Dispose 先把最后的 disable 排入同一个 worker，取消队列中的旧命令，并等待
    ///     足够覆盖“一个在途请求 + 最后一个 disable”的时间；Dispose 自身绝不发网络请求。
    /// </summary>
    public sealed class SunshineController : IDisposable
    {
        private enum CaptureAction
        {
            Enable,
            Disable,
            Status,
            FinalDisable
        }

        private sealed class CaptureCommand
        {
            public CaptureAction Action { get; set; }

            public int Generation { get; set; }

            public Guid GameId { get; set; }

            public Guid OwnerGameId { get; set; }

            public string GameName { get; set; }

            public int StartedProcessId { get; set; }

            /// <summary>仅用于状态对账：定向到状态响应里报告的会话。</summary>
            public int SessionId { get; set; }

            public bool IsStartup { get; set; }
        }

        private sealed class GameEntry
        {
            public Guid Id { get; set; }

            public string Name { get; set; }

            public int ProcessId { get; set; }

            public int Count { get; set; }

            public long StartOrder { get; set; }
        }

        /// <summary>两次请求（enable/status + 最终 disable）的最坏等待时间，约 15 秒。</summary>
        internal const int DefaultDisposeWaitMs = 15200;

        // 有界低频对账：最多 6 次探测，间隔逐步拉长，约 6 分钟后彻底停止。
        private static readonly int[] DefaultReconcileDelaysMs =
        {
            10000,
            20000,
            30000,
            60000,
            120000,
            120000
        };

        private const int MaxStartDelayMs = 10000;
        private const int WaitSliceMs = 100;

        private readonly ISunshineSettingsProvider settingsProvider;
        private readonly ISunshineTransport transport;
        private readonly ISunshineLogger logger;
        private readonly int[] reconcileDelaysMs;
        private readonly int disposeWaitMs;

        private readonly BlockingCollection<CaptureCommand> queue = new BlockingCollection<CaptureCommand>();
        private readonly Thread worker;
        private readonly ManualResetEventSlim finalDisableDone = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim disposeSignal = new ManualResetEventSlim(false);

        private readonly object sync = new object();
        private readonly Dictionary<Guid, GameEntry> activeGames = new Dictionary<Guid, GameEntry>();

        private int disposed;
        private int workerThreadId;
        private int generation;
        private int desiredActive;
        private long gameSequence;
        private Guid lastStartedGameId;
        private Guid lastStoppedGameId;

        // 0 = 未知，1 = 已确认 override 开启，-1 = 已确认/最近一次操作后关闭。
        private int knownActive = -1;

        private int ownedSessionId;
        private string ownedClientUuid;
        private int observedSessionId;

        private int enableInFlight;
        private int disposeNeedsDisable;

        private int reconcileAttempt;
        private long reconcileDueUtcTicks = -1;
        private int reconcileGeneration;
        private bool reconcileAllowIdleProbe;

        /// <summary>在收到“存在活动串流”的状态响应后触发（可能在 worker 线程上触发）。</summary>
        public event Action ActiveStreamConfirmed;

        internal SunshineController(
            ISunshineSettingsProvider settingsProvider,
            ISunshineTransport transport,
            ISunshineLogger logger)
            : this(settingsProvider, transport, logger, null, 0)
        {
        }

        internal SunshineController(
            ISunshineSettingsProvider settingsProvider,
            ISunshineTransport transport,
            ISunshineLogger logger,
            int[] reconcileDelaysMs,
            int disposeWaitMs)
        {
            if (settingsProvider == null)
            {
                throw new ArgumentNullException("settingsProvider");
            }

            if (transport == null)
            {
                throw new ArgumentNullException("transport");
            }

            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }

            this.settingsProvider = settingsProvider;
            this.transport = transport;
            this.logger = logger;
            this.reconcileDelaysMs = NormalizeDelays(reconcileDelaysMs);
            this.disposeWaitMs = disposeWaitMs > 0 ? disposeWaitMs : DefaultDisposeWaitMs;

            worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "SunshineWindowFollow.Worker"
            };
            worker.Start();
        }

        #region 事件入口（由 Playnite 调用，必须快速返回）

        /// <summary>
        /// 游戏启动。按 Game.Id 跟踪；同一游戏重复启动时使用引用计数。
        /// </summary>
        public void NotifyGameStarted(Guid gameId, string gameName, int startedProcessId)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            CaptureCommand command;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                GameEntry entry;
                if (!activeGames.TryGetValue(gameId, out entry))
                {
                    entry = new GameEntry
                    {
                        Id = gameId,
                        Count = 0
                    };
                    activeGames.Add(gameId, entry);
                }

                entry.Count++;
                entry.Name = gameName ?? string.Empty;
                entry.ProcessId = startedProcessId;
                entry.StartOrder = ++gameSequence;

                lastStartedGameId = gameId;
                generation++;
                desiredActive = 1;
                ResetReconcileLocked(false);

                command = new CaptureCommand
                {
                    Action = CaptureAction.Enable,
                    Generation = generation,
                    GameId = gameId,
                    GameName = entry.Name,
                    StartedProcessId = entry.ProcessId
                };
            }

            Enqueue(command);
        }

        /// <summary>兼容旧调用：没有 Game.Id 时按同一个匿名游戏处理。</summary>
        public void NotifyGameStarted(string gameName, int startedProcessId)
        {
            NotifyGameStarted(Guid.Empty, gameName, startedProcessId);
        }

        /// <summary>
        /// 指定游戏退出。只有该游戏确实在跟踪表中时才生效；
        /// A start → B start → A stop 不会误 disable 仍在前台的 B。
        /// </summary>
        public void NotifyGameStopped(Guid gameId)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            CaptureCommand command = null;
            bool wasOwner;
            GameEntry newOwner = null;

            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                GameEntry entry;
                if (!activeGames.TryGetValue(gameId, out entry))
                {
                    WriteDebugLocked("忽略未跟踪游戏的退出事件：" + gameId);
                    return;
                }

                entry.Count--;
                if (entry.Count > 0)
                {
                    // 同一游戏重复启动/退出，仍有实例在运行，不改变当前状态。
                    return;
                }

                activeGames.Remove(gameId);
                wasOwner = lastStartedGameId == gameId;
                lastStoppedGameId = gameId;

                if (activeGames.Count > 0)
                {
                    if (!wasOwner)
                    {
                        // 停掉的不是当前生效的所有者：保留当前 enable 指令，不需要 disable。
                        return;
                    }

                    newOwner = FindMostRecentGameLocked();
                    lastStartedGameId = newOwner == null ? Guid.Empty : newOwner.Id;
                    generation++;
                    desiredActive = 1;
                    ResetReconcileLocked(false);

                    command = new CaptureCommand
                    {
                        Action = CaptureAction.Enable,
                        Generation = generation,
                        GameId = lastStartedGameId,
                        GameName = newOwner == null ? string.Empty : newOwner.Name,
                        StartedProcessId = newOwner == null ? 0 : newOwner.ProcessId
                    };
                }
                else
                {
                    generation++;
                    desiredActive = 0;
                    lastStartedGameId = Guid.Empty;
                    ResetReconcileLocked(false);

                    command = new CaptureCommand
                    {
                        Action = CaptureAction.Disable,
                        Generation = generation,
                        OwnerGameId = gameId
                    };
                }
            }

            if (command != null)
            {
                Enqueue(command);
            }
        }

        /// <summary>兼容旧调用：等价于停止 Guid.Empty 对应的游戏。</summary>
        public void NotifyGameStopped()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            CaptureCommand command = null;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                if (activeGames.Count == 0 && ownedSessionId <= 0 && knownActive != 1)
                {
                    return;
                }

                // Playnite 退出场景：停止所有仍在跟踪的游戏。
                activeGames.Clear();
                generation++;
                desiredActive = 0;
                lastStartedGameId = Guid.Empty;
                lastStoppedGameId = Guid.Empty;
                ResetReconcileLocked(false);

                command = new CaptureCommand
                {
                    Action = CaptureAction.Disable,
                    Generation = generation,
                    OwnerGameId = Guid.Empty
                };
            }

            Enqueue(command);
        }

        /// <summary>
        /// 启动/手动状态探测。HTTP 404、连接失败、多会话 409 会进入有界低频对账，
        /// 不会在当前没有串流会话时反复请求。
        /// </summary>
        public void QueryStatus()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            CaptureCommand command;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                ResetReconcileLocked(true);
                desiredActive = activeGames.Count > 0 ? 1 : 0;

                command = new CaptureCommand
                {
                    Action = CaptureAction.Status,
                    Generation = generation,
                    IsStartup = true
                };
            }

            Enqueue(command);
        }

        private void Enqueue(CaptureCommand command)
        {
            if (command == null || Volatile.Read(ref disposed) != 0)
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
            Volatile.Write(ref workerThreadId, Thread.CurrentThread.ManagedThreadId);

            try
            {
                while (true)
                {
                    if (queue.IsCompleted && queue.Count == 0)
                    {
                        break;
                    }

                    CaptureCommand command = null;
                    var taken = false;
                    try
                    {
                        taken = queue.TryTake(out command, GetQueueWaitMilliseconds());
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        taken = false;
                    }

                    if (taken)
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
                    else if (!TryExecuteScheduledReconcile())
                    {
                        if (queue.IsCompleted && queue.Count == 0)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Sunshine 窗口跟随工作线程异常退出");
            }
            finally
            {
                finalDisableDone.Set();
            }
        }

        private int GetQueueWaitMilliseconds()
        {
            lock (sync)
            {
                if (reconcileDueUtcTicks < 0)
                {
                    return Timeout.Infinite;
                }

                var remainingTicks = reconcileDueUtcTicks - DateTime.UtcNow.Ticks;
                if (remainingTicks <= 0)
                {
                    return 0;
                }

                var milliseconds = remainingTicks / TimeSpan.TicksPerMillisecond;
                if (milliseconds <= 0)
                {
                    return 1;
                }

                return milliseconds > int.MaxValue ? int.MaxValue : (int)milliseconds;
            }
        }

        private bool TryExecuteScheduledReconcile()
        {
            int expectedGeneration;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    reconcileDueUtcTicks < 0 ||
                    DateTime.UtcNow.Ticks < reconcileDueUtcTicks)
                {
                    return false;
                }

                reconcileDueUtcTicks = -1;
                if (reconcileGeneration != generation)
                {
                    return false;
                }

                expectedGeneration = generation;
            }

            ExecuteStatusQuery(expectedGeneration, false);
            return true;
        }

        private void ExecuteCommand(CaptureCommand command)
        {
            if (command == null)
            {
                return;
            }

            if (command.Action != CaptureAction.FinalDisable && Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            switch (command.Action)
            {
                case CaptureAction.Enable:
                    ExecuteEnable(command);
                    break;
                case CaptureAction.Disable:
                    ExecuteDisable(command);
                    break;
                case CaptureAction.Status:
                    ExecuteStatusQuery(command.Generation, command.IsStartup);
                    break;
                case CaptureAction.FinalDisable:
                    ExecuteFinalDisable(command);
                    break;
            }
        }

        #endregion

        #region enable

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

            // 等待游戏窗口出现。期间如果游戏退出、所有者变化或插件退出，尽快取消。
            var delay = Math.Max(0, Math.Min(MaxStartDelayMs, settings.StartDelayMs));
            if (delay > 0 && !WaitWithCancellation(delay, command.Generation))
            {
                WriteDebug("等待游戏窗口期间游戏已退出或指令已过期，取消本次 enable");
                return;
            }

            if (!IsCurrentEnable(command))
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
                    windowTitle = command.GameName ?? string.Empty;
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

            int targetSessionId;
            lock (sync)
            {
                if (!IsCurrentEnableLocked(command))
                {
                    return;
                }

                // 已经知道自己的会话时始终定向，避免第二个客户端加入后唯一选择 409。
                targetSessionId = ownedSessionId > 0 ? ownedSessionId : 0;
            }

            logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "游戏已启动，请求 Sunshine 启用窗口跟随（mode={0}, game=\"{1}\", process_id={2}, session_id={3}）",
                mode,
                command.GameName ?? string.Empty,
                processId,
                targetSessionId));

            Interlocked.Increment(ref enableInFlight);
            SunshineResponse response;
            try
            {
                response = transport.EnableWindowFollow(
                    endpoint,
                    mode,
                    windowTitle,
                    processId,
                    targetSessionId,
                    settings.DebugLogging);
            }
            catch (Exception exception)
            {
                Interlocked.Decrement(ref enableInFlight);
                logger.Error(exception, "请求 Sunshine 启用窗口跟随失败");
                ScheduleNextReconcile(command.Generation);
                return;
            }

            Interlocked.Decrement(ref enableInFlight);
            HandleEnableResponse(response, command, targetSessionId);
        }

        private bool IsCurrentEnable(CaptureCommand command)
        {
            lock (sync)
            {
                return IsCurrentEnableLocked(command);
            }
        }

        private bool IsCurrentEnableLocked(CaptureCommand command)
        {
            if (Volatile.Read(ref disposed) != 0 ||
                command.Generation != generation ||
                desiredActive != 1)
            {
                return false;
            }

            GameEntry entry;
            return activeGames.TryGetValue(command.GameId, out entry) && entry.Count > 0;
        }

        private void HandleEnableResponse(SunshineResponse response, CaptureCommand command, int requestedSessionId)
        {
            if (response == null)
            {
                logger.Error("Sunshine enable 请求没有返回结果");
                ScheduleNextReconcile(command.Generation);
                return;
            }

            if (!response.HasResponse)
            {
                logger.Error(string.Format(
                    CultureInfo.InvariantCulture,
                    "Sunshine enable 请求失败（端点 {0}，共 {1} 次）：{2}",
                    response.EndpointDisplay,
                    response.Attempts,
                    response.TransportError));
                ScheduleNextReconcile(command.Generation);
                return;
            }

            switch (response.StatusCode)
            {
                case 200:
                    HandleEnableSuccess(response, command, requestedSessionId);
                    break;

                case 404:
                    ClearOwnedSession("Sunshine 当前没有活动的串流会话（HTTP 404）");
                    logger.Debug("Sunshine 当前没有活动的串流会话（HTTP 404），跳过 enable");
                    ScheduleNextReconcile(command.Generation);
                    break;

                case 409:
                    logger.Warn("Sunshine 报告存在多个活动串流会话或该模式不受支持（HTTP 409），enable 未生效");
                    ScheduleNextReconcile(command.Generation);
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

                default:
                    logger.Error(string.Format(
                        CultureInfo.InvariantCulture,
                        "Sunshine 服务端错误（HTTP {0}）：{1}",
                        response.StatusCode,
                        SunshineText.Truncate(response.Body)));
                    if (response.StatusCode >= 500)
                    {
                        ScheduleNextReconcile(command.Generation);
                    }

                    break;
            }
        }

        private void HandleEnableSuccess(SunshineResponse response, CaptureCommand command, int requestedSessionId)
        {
            var status = response.Status;
            if (status == null)
            {
                logger.Warn("Sunshine 返回 HTTP 200，但响应不是可解析的 JSON：" +
                            SunshineText.Truncate(response.Body));
                ScheduleNextReconcile(command.Generation);
                return;
            }

            var current = false;
            string clearedReason = null;
            lock (sync)
            {
                if (status.Active)
                {
                    knownActive = 1;
                    if (status.SessionId > 0)
                    {
                        if (ownedSessionId > 0 && ownedSessionId != status.SessionId)
                        {
                            clearedReason = "Sunshine 会话已变化，清理旧 session_id=" + ownedSessionId;
                        }

                        ownedSessionId = status.SessionId;
                        ownedClientUuid = status.ClientUuid;
                    }
                    else if (requestedSessionId > 0)
                    {
                        ownedSessionId = requestedSessionId;
                    }
                }
                else
                {
                    knownActive = 0;
                    ownedSessionId = 0;
                    ownedClientUuid = null;
                }

                if (status.SessionId > 0)
                {
                    observedSessionId = status.SessionId;
                }

                current = command.Generation == generation && desiredActive == 1 &&
                          Volatile.Read(ref disposed) == 0;
            }

            if (clearedReason != null)
            {
                logger.Info(clearedReason);
            }

            if (status.SessionId > 0)
            {
                NotifyStreamConfirmed();
            }

            if (status.Active)
            {
                logger.Info("Sunshine enable 完成：" + status.Describe());
            }
            else
            {
                logger.Warn("Sunshine 返回 ok=true 但 override 未开启：" + status.Describe());
            }

            if (!current)
            {
                WriteDebug("enable 响应返回时指令已过期，状态仅用于清理/记录，不覆盖新状态");
                return;
            }

            if (status.Active)
            {
                ResetReconcileLocked(false);
            }
            else
            {
                ScheduleNextReconcile(command.Generation);
            }

            if (!status.Ok && !string.IsNullOrWhiteSpace(status.Error))
            {
                logger.Warn("Sunshine 返回 ok=false：" + status.Error);
            }
        }

        #endregion

        #region disable

        private void ExecuteDisable(CaptureCommand command)
        {
            var settings = GetSettingsSnapshot();
            if (settings == null)
            {
                logger.Error("插件设置尚未加载，无法恢复 Sunshine 整屏捕获");
                return;
            }

            int targetSessionId;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    command.Generation != generation ||
                    desiredActive != 0 ||
                    activeGames.Count != 0)
                {
                    WriteDebugLocked("disable 指令已过期或仍有游戏在运行，跳过（owner=" + command.OwnerGameId + "）");
                    return;
                }

                if (command.OwnerGameId != Guid.Empty && command.OwnerGameId != lastStoppedGameId)
                {
                    WriteDebugLocked("disable 指令的所有者已过期，跳过（owner=" + command.OwnerGameId + "）");
                    return;
                }

                if (!settings.Enabled && knownActive != 1 && ownedSessionId <= 0 && command.SessionId <= 0)
                {
                    WriteDebugLocked("插件未启用且当前未处于窗口跟随状态，跳过 disable");
                    return;
                }

                targetSessionId = command.SessionId > 0
                    ? command.SessionId
                    : (ownedSessionId > 0 ? ownedSessionId : observedSessionId);
            }

            SunshineEndpoint endpoint;
            string error;
            if (!SunshineEndpoint.TryCreate(settings, out endpoint, out error))
            {
                logger.Error("Sunshine 地址无效，无法恢复整屏捕获：" + error);
                return;
            }

            logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "游戏已退出，请求 Sunshine 恢复整屏捕获（session_id={0}）",
                targetSessionId));

            SunshineResponse response;
            int sentSessionId;
            try
            {
                response = SendDisableWithFallback(endpoint, targetSessionId, settings.DebugLogging, out sentSessionId);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "请求 Sunshine 恢复整屏捕获失败");
                ScheduleNextReconcile(command.Generation);
                return;
            }

            HandleDisableResponse(response, command, sentSessionId);
        }

        private SunshineResponse SendDisableWithFallback(
            SunshineEndpoint endpoint,
            int sessionId,
            bool debugLogging,
            out int sentSessionId)
        {
            sentSessionId = sessionId;
            var response = transport.DisableWindowFollow(endpoint, sentSessionId, debugLogging);

            // 第二个客户端加入后，session_id=0 的“唯一选择”会 409。
            // 如果响应里能拿到会话（或之前已保存会话），立刻定向重试一次。
            int fallbackSessionId = 0;
            if (ShouldRetryDisableTargeted(response, sentSessionId, out fallbackSessionId))
            {
                logger.Info("Sunshine disable 需要定向会话，使用保存的 session_id=" + fallbackSessionId + " 重试一次");
                sentSessionId = fallbackSessionId;
                response = transport.DisableWindowFollow(endpoint, sentSessionId, debugLogging);
            }

            return response;
        }

        private bool ShouldRetryDisableTargeted(SunshineResponse response, int sentSessionId, out int fallbackSessionId)
        {
            fallbackSessionId = 0;
            if (response == null || !response.HasResponse || sentSessionId > 0)
            {
                return false;
            }

            if (response.StatusCode != 409 && response.StatusCode != 200)
            {
                return false;
            }

            var status = response.Status;
            if (status != null && status.SessionId > 0)
            {
                fallbackSessionId = status.SessionId;
                return true;
            }

            lock (sync)
            {
                if (ownedSessionId > 0)
                {
                    fallbackSessionId = ownedSessionId;
                    return true;
                }

                if (observedSessionId > 0)
                {
                    fallbackSessionId = observedSessionId;
                    return true;
                }
            }

            return false;
        }

        private void HandleDisableResponse(SunshineResponse response, CaptureCommand command, int sentSessionId)
        {
            if (response == null)
            {
                logger.Error("Sunshine disable 请求没有返回结果");
                ScheduleNextReconcile(command.Generation);
                return;
            }

            if (!response.HasResponse)
            {
                logger.Error(string.Format(
                    CultureInfo.InvariantCulture,
                    "Sunshine disable 请求失败（端点 {0}，共 {1} 次）：{2}",
                    response.EndpointDisplay,
                    response.Attempts,
                    response.TransportError));
                ScheduleNextReconcile(command.Generation);
                return;
            }

            var status = response.Status;
            var current = false;
            var stillActive = false;

            switch (response.StatusCode)
            {
                case 200:
                    lock (sync)
                    {
                        if (status == null)
                        {
                            // HTTP 200 但正文不可解析：保守地认为服务端已执行，清掉旧身份。
                            knownActive = 0;
                            ownedSessionId = 0;
                            ownedClientUuid = null;
                        }
                        else if (status.Active)
                        {
                            knownActive = 1;
                            stillActive = true;
                            if (status.SessionId > 0)
                            {
                                observedSessionId = status.SessionId;
                            }
                        }
                        else
                        {
                            knownActive = 0;
                            ownedSessionId = 0;
                            ownedClientUuid = null;
                        }

                        current = command.Generation == generation && desiredActive == 0 &&
                                  Volatile.Read(ref disposed) == 0;
                    }

                    if (!stillActive)
                    {
                        if (status != null)
                        {
                            logger.Info("Sunshine disable 完成：" + status.Describe());
                        }
                        else
                        {
                            logger.Info("Sunshine disable 已返回 HTTP 200（正文不可解析）");
                        }

                        if (current)
                        {
                            ResetReconcileLocked(false);
                        }
                    }
                    else
                    {
                        logger.Warn("Sunshine 返回 HTTP 200 但 override 仍处于开启状态（session_id=" +
                                    sentSessionId + "）：" + status.Describe());
                        if (current)
                        {
                            ScheduleNextReconcile(command.Generation);
                        }
                    }

                    break;

                case 404:
                    lock (sync)
                    {
                        knownActive = 0;
                        ownedSessionId = 0;
                        ownedClientUuid = null;
                        current = command.Generation == generation && desiredActive == 0 &&
                                  Volatile.Read(ref disposed) == 0;
                    }

                    logger.Debug("Sunshine 当前没有活动的串流会话（HTTP 404），disable 无需执行");
                    if (current)
                    {
                        ResetReconcileLocked(false);
                    }

                    break;

                case 409:
                    logger.Warn("Sunshine 报告存在多个活动串流会话或该模式不受支持（HTTP 409），disable 仍未完成");
                    ScheduleNextReconcile(command.Generation);
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

                default:
                    logger.Error(string.Format(
                        CultureInfo.InvariantCulture,
                        "Sunshine 服务端错误（HTTP {0}）：{1}",
                        response.StatusCode,
                        SunshineText.Truncate(response.Body)));
                    if (response.StatusCode >= 500)
                    {
                        ScheduleNextReconcile(command.Generation);
                    }

                    break;
            }
        }

        #endregion

        #region status / reconciliation

        private void ExecuteStatusQuery(int expectedGeneration, bool isStartup)
        {
            var settings = GetSettingsSnapshot();
            if (settings == null)
            {
                WriteDebug("插件设置尚未加载，跳过状态查询");
                return;
            }

            if (!settings.Enabled)
            {
                WriteDebug("插件总开关已关闭，跳过状态查询");
                return;
            }

            SunshineEndpoint endpoint;
            string error;
            if (!SunshineEndpoint.TryCreate(settings, out endpoint, out error))
            {
                logger.Error("Sunshine 地址无效，跳过状态查询：" + error);
                return;
            }

            if (isStartup)
            {
                WriteDebug("插件启动，查询 Sunshine 窗口跟随状态: " + endpoint.DisplayUrl);
            }

            SunshineResponse response;
            try
            {
                response = transport.QueryStatus(endpoint, settings.DebugLogging);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "查询 Sunshine 窗口跟随状态失败");
                ScheduleNextReconcile(expectedGeneration);
                return;
            }

            HandleStatusResponse(response, expectedGeneration, isStartup);
        }

        private void HandleStatusResponse(SunshineResponse response, int expectedGeneration, bool isStartup)
        {
            if (response == null)
            {
                logger.Error("Sunshine status 请求没有返回结果");
                ScheduleNextReconcile(expectedGeneration);
                return;
            }

            if (!response.HasResponse)
            {
                logger.Error(string.Format(
                    CultureInfo.InvariantCulture,
                    "Sunshine status 请求失败（端点 {0}，共 {1} 次）：{2}",
                    response.EndpointDisplay,
                    response.Attempts,
                    response.TransportError));
                ScheduleNextReconcile(expectedGeneration);
                return;
            }

            switch (response.StatusCode)
            {
                case 200:
                    HandleStatusSuccess(response, expectedGeneration, isStartup);
                    break;

                case 404:
                    ClearOwnedSession("Sunshine 当前没有活动的串流会话（HTTP 404）");
                    logger.Debug("Sunshine 当前没有活动的串流会话（HTTP 404），状态对账等待下次探测");
                    ScheduleNextReconcile(expectedGeneration);
                    break;

                case 409:
                    logger.Warn("Sunshine 报告存在多个活动串流会话（HTTP 409），尝试用已保存的 session_id 恢复");
                    if (!RecoverFromSessionConflict(expectedGeneration))
                    {
                        ScheduleNextReconcile(expectedGeneration);
                    }

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

                default:
                    logger.Error(string.Format(
                        CultureInfo.InvariantCulture,
                        "Sunshine 服务端错误（HTTP {0}）：{1}",
                        response.StatusCode,
                        SunshineText.Truncate(response.Body)));
                    if (response.StatusCode >= 500)
                    {
                        ScheduleNextReconcile(expectedGeneration);
                    }

                    break;
            }
        }

        private void RestoreOwnedSessionFromList(WindowFollowStatus status)
        {
            if (status == null || status.SessionId > 0 || status.Sessions == null)
            {
                return;
            }

            int sessionId;
            lock (sync)
            {
                sessionId = ownedSessionId;
            }

            if (sessionId <= 0)
            {
                return;
            }

            foreach (var session in status.Sessions)
            {
                if (session == null || session.SessionId != sessionId)
                {
                    continue;
                }

                // GET has no selected_* fields. When another client joins, keep
                // ownership by finding our existing session in the full list.
                status.SelectedSession = session;
                status.SessionId = session.SessionId;
                status.ClientUuid = session.ClientUuid;
                if (session.Override != null)
                {
                    status.Active = session.Override.Enabled;
                    status.Mode = session.Override.Mode;
                }
                if (session.Observed != null)
                {
                    status.Fallback = session.Observed.FallbackReason;
                    status.FallbackToDisplay = session.Observed.FallbackToDisplay;
                    status.Window = new WindowFollowWindowInfo
                    {
                        Title = session.Observed.WindowTitle,
                        ProcessId = session.Observed.ProcessId,
                        Hwnd = session.Observed.Hwnd
                    };
                }
                return;
            }
        }

        private void HandleStatusSuccess(SunshineResponse response, int expectedGeneration, bool isStartup)
        {
            var status = response.Status;
            if (status == null)
            {
                logger.Warn("Sunshine 返回 HTTP 200，但状态不是可解析的 JSON：" +
                            SunshineText.Truncate(response.Body));
                ScheduleNextReconcile(expectedGeneration);
                return;
            }

            RestoreOwnedSessionFromList(status);

            int previousSessionId;
            int previousKnownActive;
            string clearedReason = null;
            bool current;

            lock (sync)
            {
                previousSessionId = ownedSessionId;
                previousKnownActive = knownActive;

                if (status.SessionId > 0)
                {
                    observedSessionId = status.SessionId;
                }
                else
                {
                    observedSessionId = 0;
                }

                if (status.SessionId > 0 &&
                    ownedSessionId > 0 &&
                    status.SessionId != ownedSessionId)
                {
                    clearedReason = "Sunshine 会话已变化（旧 session_id=" + ownedSessionId +
                                    "，新 session_id=" + status.SessionId + "），清理过期身份";
                    ownedSessionId = 0;
                    ownedClientUuid = null;
                    knownActive = 0;
                }
                else if (status.SessionId <= 0 && ownedSessionId > 0)
                {
                    clearedReason = "Sunshine 不再报告活动会话，清理过期 session_id=" + ownedSessionId;
                    ownedSessionId = 0;
                    ownedClientUuid = null;
                    knownActive = 0;
                }

                if (status.Active && status.SessionId > 0)
                {
                    ownedSessionId = status.SessionId;
                    ownedClientUuid = status.ClientUuid;
                    knownActive = 1;
                }
                else if (!status.Active)
                {
                    knownActive = 0;
                }

                current = expectedGeneration == generation &&
                          Volatile.Read(ref disposed) == 0;
            }

            if (clearedReason != null)
            {
                logger.Info(clearedReason);
            }

            if (status.SessionId > 0)
            {
                NotifyStreamConfirmed();
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                // 状态响应可能为正在排队的最终 disable 提供 session_id。
                return;
            }

            if (!current)
            {
                WriteDebug("status 响应返回时已有更新事件，忽略本次状态同步");
                return;
            }

            if (isStartup)
            {
                logger.Info("Sunshine 启动状态：" + status.Describe());
            }
            else if (previousKnownActive != (status.Active ? 1 : 0) ||
                     previousSessionId != status.SessionId)
            {
                logger.Info("Sunshine 状态对账：" + status.Describe());
            }

            var desired = Volatile.Read(ref desiredActive) == 1;
            if (desired)
            {
                if (!status.Active)
                {
                    EnqueueEnableForCurrentOwner(expectedGeneration);
                }
                else
                {
                    ResetReconcileLocked(false);
                }
            }
            else
            {
                if (status.Active)
                {
                    EnqueueDisableFromStatus(expectedGeneration);
                }
                else
                {
                    ResetReconcileLocked(false);
                }
            }
        }

        /// <summary>
        /// 多会话 409 时：已经保存了 session_id 就定向 enable/disable；否则只能等待低频对账。
        /// </summary>
        private bool RecoverFromSessionConflict(int expectedGeneration)
        {
            int sessionId;
            int desired;
            lock (sync)
            {
                if (expectedGeneration != generation || Volatile.Read(ref disposed) != 0)
                {
                    return false;
                }

                sessionId = ownedSessionId > 0 ? ownedSessionId : observedSessionId;
                desired = desiredActive;
            }

            if (sessionId <= 0)
            {
                return false;
            }

            if (desired == 1)
            {
                EnqueueEnableForCurrentOwner(expectedGeneration);
            }
            else
            {
                EnqueueDisableFromStatus(expectedGeneration);
            }

            return true;
        }

        private void EnqueueEnableForCurrentOwner(int expectedGeneration)
        {
            CaptureCommand command = null;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    expectedGeneration != generation ||
                    desiredActive != 1)
                {
                    return;
                }

                GameEntry owner;
                if (!activeGames.TryGetValue(lastStartedGameId, out owner))
                {
                    owner = FindMostRecentGameLocked();
                }

                if (owner == null)
                {
                    return;
                }

                command = new CaptureCommand
                {
                    Action = CaptureAction.Enable,
                    Generation = generation,
                    GameId = owner.Id,
                    GameName = owner.Name,
                    StartedProcessId = owner.ProcessId
                };
            }

            Enqueue(command);
        }

        private void EnqueueDisableFromStatus(int expectedGeneration)
        {
            CaptureCommand command = null;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    expectedGeneration != generation ||
                    desiredActive != 0 ||
                    activeGames.Count != 0)
                {
                    return;
                }

                var sessionId = ownedSessionId > 0 ? ownedSessionId : observedSessionId;
                command = new CaptureCommand
                {
                    Action = CaptureAction.Disable,
                    Generation = generation,
                    OwnerGameId = Guid.Empty,
                    SessionId = sessionId
                };
            }

            Enqueue(command);
        }

        private void ScheduleNextReconcile(int expectedGeneration)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            var delay = -1;
            var exhausted = false;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0 || expectedGeneration != generation)
                {
                    return;
                }

                if (!reconcileAllowIdleProbe &&
                    desiredActive == 0 &&
                    ownedSessionId <= 0 &&
                    knownActive != 1)
                {
                    return;
                }

                if (reconcileAttempt >= reconcileDelaysMs.Length)
                {
                    reconcileDueUtcTicks = -1;
                    exhausted = true;
                }
                else
                {
                    delay = reconcileDelaysMs[reconcileAttempt];
                    reconcileAttempt++;
                    reconcileGeneration = generation;
                    reconcileDueUtcTicks = DateTime.UtcNow.Ticks +
                                           (delay * TimeSpan.TicksPerMillisecond);
                }
            }

            if (exhausted)
            {
                logger.Debug("Sunshine 状态对账已达到次数上限，停止低频探测，等待下一次游戏事件");
            }
        }

        private void ResetReconcileLocked(bool allowIdleProbe)
        {
            reconcileAttempt = 0;
            reconcileDueUtcTicks = -1;
            reconcileGeneration = 0;
            reconcileAllowIdleProbe = allowIdleProbe;
        }

        #endregion

        #region 结果清理 / 辅助

        private void ClearOwnedSession(string reason)
        {
            lock (sync)
            {
                if (ownedSessionId > 0)
                {
                    logger.Info(reason + "（session_id=" + ownedSessionId + "）");
                }

                ownedSessionId = 0;
                ownedClientUuid = null;
                observedSessionId = 0;
                knownActive = 0;
            }
        }

        private void NotifyStreamConfirmed()
        {
            var handler = ActiveStreamConfirmed;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler();
            }
            catch (Exception exception)
            {
                logger.Error(exception, "确认活动串流后执行前台激活回调失败");
            }
        }

        private GameEntry FindMostRecentGameLocked()
        {
            GameEntry best = null;
            foreach (var entry in activeGames.Values)
            {
                if (best == null || entry.StartOrder > best.StartOrder)
                {
                    best = entry;
                }
            }

            return best;
        }

        private bool WaitWithCancellation(int totalMilliseconds, int expectedGeneration)
        {
            var deadline = DateTime.UtcNow.Ticks +
                           (totalMilliseconds * TimeSpan.TicksPerMillisecond);

            while (DateTime.UtcNow.Ticks < deadline)
            {
                if (Volatile.Read(ref disposed) != 0 ||
                    disposeSignal.IsSet ||
                    expectedGeneration != Volatile.Read(ref generation) ||
                    Volatile.Read(ref desiredActive) != 1)
                {
                    return false;
                }

                var remainingTicks = deadline - DateTime.UtcNow.Ticks;
                var remainingMs = (int)Math.Max(1, Math.Min(WaitSliceMs, remainingTicks / TimeSpan.TicksPerMillisecond));
                if (disposeSignal.Wait(remainingMs))
                {
                    return false;
                }
            }

            return Volatile.Read(ref disposed) == 0 &&
                   expectedGeneration == Volatile.Read(ref generation) &&
                   Volatile.Read(ref desiredActive) == 1;
        }

        private static int[] NormalizeDelays(int[] delays)
        {
            if (delays == null || delays.Length == 0)
            {
                return (int[])DefaultReconcileDelaysMs.Clone();
            }

            var normalized = new int[delays.Length];
            for (var index = 0; index < delays.Length; index++)
            {
                normalized[index] = Math.Max(1, delays[index]);
            }

            return normalized;
        }

        private SunshineSettings GetSettingsSnapshot()
        {
            return settingsProvider.GetSnapshot();
        }

        private void WriteDebug(string message)
        {
            var settings = GetSettingsSnapshot();
            if (settings != null && settings.DebugLogging)
            {
                logger.Debug(message);
            }
        }

        private void WriteDebugLocked(string message)
        {
            // 调用方可能已经持有 sync；这里只读取不可变快照，不会再次加锁。
            var settings = GetSettingsSnapshot();
            if (settings != null && settings.DebugLogging)
            {
                logger.Debug(message);
            }
        }

        #endregion

        #region Dispose

        private void ExecuteFinalDisable(CaptureCommand command)
        {
            try
            {
                if (Volatile.Read(ref disposeNeedsDisable) == 0)
                {
                    WriteDebug("Playnite 退出时没有需要恢复的窗口跟随状态，跳过最终 disable");
                    return;
                }

                var settings = GetSettingsSnapshot();
                if (settings == null)
                {
                    logger.Error("插件设置尚未加载，无法在退出前恢复 Sunshine 整屏捕获");
                    return;
                }

                SunshineEndpoint endpoint;
                string error;
                if (!SunshineEndpoint.TryCreate(settings, out endpoint, out error))
                {
                    logger.Error("Sunshine 地址无效，无法在退出前恢复整屏捕获：" + error);
                    return;
                }

                int targetSessionId;
                lock (sync)
                {
                    targetSessionId = ownedSessionId > 0
                        ? ownedSessionId
                        : observedSessionId;
                }

                logger.Info(string.Format(
                    CultureInfo.InvariantCulture,
                    "Playnite 即将退出，通过工作线程恢复 Sunshine 整屏捕获（session_id={0}）",
                    targetSessionId));

                int sentSessionId;
                var response = SendDisableWithFallback(
                    endpoint,
                    targetSessionId,
                    settings.DebugLogging,
                    out sentSessionId);

                if (response == null)
                {
                    logger.Error("Playnite 退出前的最终 disable 没有返回结果");
                    return;
                }

                if (!response.HasResponse)
                {
                    logger.Error("Playnite 退出前的最终 disable 失败：" + response.TransportError);
                    return;
                }

                if (response.StatusCode == 200 || response.StatusCode == 404)
                {
                    lock (sync)
                    {
                        knownActive = 0;
                        ownedSessionId = 0;
                        ownedClientUuid = null;
                    }

                    logger.Info("Playnite 退出前已恢复 Sunshine 整屏捕获" +
                                (sentSessionId > 0 ? "（session_id=" + sentSessionId + "）" : string.Empty));
                }
                else if (response.StatusCode == 409)
                {
                    logger.Warn("Playnite 退出前最终 disable 仍返回 HTTP 409（多会话），无法唯一选择会话");
                }
                else
                {
                    logger.Error(string.Format(
                        CultureInfo.InvariantCulture,
                        "Playnite 退出前最终 disable 返回 HTTP {0}：{1}",
                        response.StatusCode,
                        SunshineText.Truncate(response.Body)));
                }
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Playnite 退出前的最终 disable 发生异常");
            }
            finally
            {
                finalDisableDone.Set();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }

            var finalGeneration = 0;
            int needDisable;
            lock (sync)
            {
                needDisable = activeGames.Count > 0 ||
                              ownedSessionId > 0 ||
                              knownActive == 1 ||
                              Volatile.Read(ref enableInFlight) > 0
                    ? 1
                    : 0;

                Volatile.Write(ref disposeNeedsDisable, needDisable);
                generation++;
                finalGeneration = generation;
                desiredActive = 0;
                activeGames.Clear();
                lastStartedGameId = Guid.Empty;
                ResetReconcileLocked(false);
            }

            // 唤醒可能在启动延迟里等待的 worker，并取消队列中所有旧命令。
            disposeSignal.Set();
            try
            {
                CaptureCommand ignored;
                while (queue.TryTake(out ignored))
                {
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            var finalQueued = true;
            try
            {
                // 最终 disable 只进入同一个 worker，Dispose 自身绝不发网络请求。
                queue.Add(new CaptureCommand
                {
                    Action = CaptureAction.FinalDisable,
                    Generation = finalGeneration
                });
            }
            catch (ObjectDisposedException)
            {
                finalQueued = false;
            }
            catch (InvalidOperationException)
            {
                finalQueued = false;
            }

            try
            {
                queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }

            if (!finalQueued)
            {
                finalDisableDone.Set();
            }
            else if (Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref workerThreadId))
            {
                // 不允许在 worker 自己内部等待自己；队列中的 FinalDisable 会在回调返回后执行。
                logger.Warn("Sunshine 控制器在工作线程上被 Dispose，最终 disable 将继续由该线程串行完成");
                return;
            }
            else if (!finalDisableDone.Wait(disposeWaitMs))
            {
                logger.Warn("等待最终 disable 超时（" + disposeWaitMs + " 毫秒）；不再从调用线程补发网络请求");
            }

            try
            {
                worker.Join(1000);
            }
            catch (Exception)
            {
                // 退出路径上吞掉一切异常。
            }
        }

        #endregion
    }
}
