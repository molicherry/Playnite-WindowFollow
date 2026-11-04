#if SUNSHINE_LIFECYCLE_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SunshineWindowFollow.LifecycleTests
{
    internal sealed class LifecycleTestCase
    {
        public LifecycleTestCase(string name, Action body)
        {
            Name = name;
            Body = body;
        }

        public string Name { get; private set; }

        public Action Body { get; private set; }
    }

    internal static class LifecycleAssert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new LifecycleAssertionException(message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new LifecycleAssertionException(
                    message + "（期望：" + expected + "，实际：" + actual + "）");
            }
        }

        public static void NotNull(object value, string message)
        {
            if (value == null)
            {
                throw new LifecycleAssertionException(message);
            }
        }
    }

    internal sealed class LifecycleAssertionException : Exception
    {
        public LifecycleAssertionException(string message) : base(message)
        {
        }
    }

    public static class LifecycleProgram
    {
        public static int Main()
        {
            var tests = new[]
            {
                new LifecycleTestCase("A start -> B start -> A stop 不 disable B", AStartBStartAStopDoesNotDisableB),
                new LifecycleTestCase("新游戏启动后过期的 disable 被丢弃", StaleDisableAfterNewStartIsDropped),
                new LifecycleTestCase("enable 成功的 SessionId 用于定向 disable", EnableSessionIsUsedForDisable),
                new LifecycleTestCase("第二客户端 409 后使用定向会话恢复", SecondClientConflictRecoversWithTargetedDisable),
                new LifecycleTestCase("404 清理过期会话身份", NotFoundClearsStaleSessionBeforeDisable),
                new LifecycleTestCase("404 对账有界且低频", NotFoundReconcileIsBounded),
                new LifecycleTestCase("状态驱动 enable 失败时对账仍有界", StatusDrivenEnableReconcileIsBounded),
                new LifecycleTestCase("会话变化后不沿用旧 SessionId", SessionChangeReplacesStaleIdentity),
                new LifecycleTestCase("多会话 GET 保留已保存的所属会话", MultiSessionStatusKeepsOwnedSession),
                new LifecycleTestCase("Dispose 在同一个 worker 上串行最终 disable", DisposeSerializesFinalDisableOnWorker),
                new LifecycleTestCase("Dispose 取消排队中的启动延迟", DisposeCancelsQueuedEnable),
                new LifecycleTestCase("启动状态发现旧 override 时定向清理", StartupActiveOverrideIsDisabled),
                new LifecycleTestCase("只有状态确认活动串流才触发前台回调", ActiveStreamIsOnlyConfirmedByStatus)
            };

            var failures = 0;
            foreach (var test in tests)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    test.Body();
                    stopwatch.Stop();
                    Console.WriteLine("PASS  " + test.Name + " (" + stopwatch.ElapsedMilliseconds + " ms)");
                }
                catch (Exception exception)
                {
                    failures++;
                    stopwatch.Stop();
                    Console.WriteLine("FAIL  " + test.Name + " (" + stopwatch.ElapsedMilliseconds + " ms)");
                    Console.WriteLine("      " + exception.GetType().Name + ": " + exception.Message);
                    if (!(exception is LifecycleAssertionException))
                    {
                        Console.WriteLine(exception.StackTrace);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "全部生命周期测试通过。"
                : failures + " 个生命周期测试失败。");

            return failures == 0 ? 0 : 1;
        }

        private static SunshineController CreateController(
            LifecycleSettingsProvider settings,
            LifecycleFakeTransport transport,
            int[] reconcileDelays,
            int disposeWaitMs)
        {
            return new SunshineController(
                settings,
                transport,
                new LifecycleLogger(),
                reconcileDelays ?? new[] { 10000, 20000 },
                disposeWaitMs);
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return predicate();
        }

        private static void AStartBStartAStopDoesNotDisableB()
        {
            var settings = LifecycleSettings.Create();
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Enable)
                    {
                        return LifecycleResponses.Active(7);
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var gameA = Guid.NewGuid();
            var gameB = Guid.NewGuid();

            using (var controller = CreateController(settings, transport, new[] { 10000 }, 3000))
            {
                controller.NotifyGameStarted(gameA, "A", 100);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 1, 2000),
                    "A 启动后应发出 enable");

                controller.NotifyGameStarted(gameB, "B", 200);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 2, 2000),
                    "B 启动后应发出 enable");

                controller.NotifyGameStopped(gameA);
                Thread.Sleep(150);

                LifecycleAssert.Equal(0, transport.Count(LifecycleCallKind.Disable),
                    "A 退出时 B 仍在运行，不能 disable");

                controller.NotifyGameStopped(gameB);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Disable) == 1, 2000),
                    "最后一个游戏退出后应发出 disable");

                var disable = transport.Last(LifecycleCallKind.Disable);
                LifecycleAssert.NotNull(disable, "应记录 disable 调用");
                LifecycleAssert.Equal(7, disable.SessionId, "disable 应定向到 enable 保存的会话");
            }
        }

        private static void StaleDisableAfterNewStartIsDropped()
        {
            var settings = LifecycleSettings.Create();
            settings.Settings.StartDelayMs = 300;

            var transport = new LifecycleFakeTransport
            {
                Handler = call => call.Kind == LifecycleCallKind.Enable
                    ? LifecycleResponses.Active(11)
                    : LifecycleResponses.Inactive()
            };

            var gameA = Guid.NewGuid();
            var gameB = Guid.NewGuid();

            using (var controller = CreateController(settings, transport, new[] { 10000 }, 3000))
            {
                controller.NotifyGameStarted(gameA, "A", 100);
                Thread.Sleep(30);

                // A 退出时队列里会有 disable；紧接着 B 启动，代次/所有者都必须让旧 disable 失效。
                controller.NotifyGameStopped(gameA);
                controller.NotifyGameStarted(gameB, "B", 200);

                LifecycleAssert.True(
                    transport.WaitFor(
                        calls => CountKind(calls, LifecycleCallKind.Enable) >= 1 &&
                                 CountKind(calls, LifecycleCallKind.Disable) == 0,
                        3000),
                    "B 的 enable 应执行，且旧 disable 不能执行");

                Thread.Sleep(150);
                LifecycleAssert.Equal(0, transport.Count(LifecycleCallKind.Disable),
                    "过期 disable 不能在新游戏启动后发出");
            }
        }

        private static void EnableSessionIsUsedForDisable()
        {
            var settings = LifecycleSettings.Create();
            var transport = new LifecycleFakeTransport
            {
                Handler = call => call.Kind == LifecycleCallKind.Enable
                    ? LifecycleResponses.Active(42)
                    : LifecycleResponses.Inactive()
            };

            var game = Guid.NewGuid();
            using (var controller = CreateController(settings, transport, new[] { 10000 }, 3000))
            {
                controller.NotifyGameStarted(game, "Game", 10);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 1, 2000),
                    "应发出 enable");

                controller.NotifyGameStopped(game);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Disable) == 1, 2000),
                    "应发出 disable");

                LifecycleAssert.Equal(42, transport.Last(LifecycleCallKind.Disable).SessionId,
                    "disable 必须携带保存的 session_id");
            }
        }

        private static void SecondClientConflictRecoversWithTargetedDisable()
        {
            var settings = LifecycleSettings.Create();
            var disableCalls = 0;
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Enable)
                    {
                        // 模拟服务端没有在 enable 响应里报告 session_id。
                        return LifecycleResponses.Active(0);
                    }

                    if (call.Kind == LifecycleCallKind.Disable)
                    {
                        disableCalls++;
                        if (call.SessionId == 0)
                        {
                            // 第二个客户端加入后，唯一选择返回 409，但正文带出了原会话。
                            return LifecycleResponses.Http(409, LifecycleResponses.MakeStatus(true, 9, "test-client"));
                        }

                        if (call.SessionId == 9)
                        {
                            return LifecycleResponses.Inactive();
                        }
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var game = Guid.NewGuid();
            using (var controller = CreateController(settings, transport, new[] { 10000 }, 3000))
            {
                controller.NotifyGameStarted(game, "Game", 10);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 1, 2000),
                    "应发出 enable");

                controller.NotifyGameStopped(game);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Disable) >= 2, 2000),
                    "409 后应使用定向会话重试 disable");

                var snapshot = transport.Snapshot();
                var disables = snapshot.FindAll(call => call.Kind == LifecycleCallKind.Disable);
                LifecycleAssert.Equal(0, disables[0].SessionId, "第一次 disable 使用唯一选择");
                LifecycleAssert.Equal(9, disables[1].SessionId, "409 后应定向到响应中的会话");
                LifecycleAssert.Equal(2, disableCalls, "只应重试一次");
            }
        }

        private static void NotFoundClearsStaleSessionBeforeDisable()
        {
            var settings = LifecycleSettings.Create();
            var statusCalls = 0;
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Enable)
                    {
                        return LifecycleResponses.Active(7);
                    }

                    if (call.Kind == LifecycleCallKind.Status)
                    {
                        statusCalls++;
                        return LifecycleResponses.Http(404, null);
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var game = Guid.NewGuid();
            using (var controller = CreateController(settings, transport, new[] { 10000 }, 3000))
            {
                controller.NotifyGameStarted(game, "Game", 10);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 1, 2000),
                    "应发出 enable");

                controller.QueryStatus();
                LifecycleAssert.True(WaitUntil(() => statusCalls >= 1, 2000), "应执行一次状态查询");

                controller.NotifyGameStopped(game);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Disable) == 1, 2000),
                    "应发出 disable");

                LifecycleAssert.Equal(0, transport.Last(LifecycleCallKind.Disable).SessionId,
                    "404 后不能再用过期 session_id 定向 disable");
            }
        }

        private static void NotFoundReconcileIsBounded()
        {
            var settings = LifecycleSettings.Create();
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Enable)
                    {
                        return LifecycleResponses.Http(404, null);
                    }

                    if (call.Kind == LifecycleCallKind.Status)
                    {
                        return LifecycleResponses.Http(404, null);
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var game = Guid.NewGuid();
            using (var controller = CreateController(settings, transport, new[] { 40, 40, 40 }, 3000))
            {
                controller.NotifyGameStarted(game, "Game", 10);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Status) == 3, 3000),
                    "应执行完 3 次有界状态对账");

                Thread.Sleep(250);
                LifecycleAssert.Equal(3, transport.Count(LifecycleCallKind.Status),
                    "达到对账上限后不能继续请求");
                LifecycleAssert.Equal(1, transport.Count(LifecycleCallKind.Enable),
                    "不能高频重复 enable");
            }
        }

        private static void StatusDrivenEnableReconcileIsBounded()
        {
            var settings = LifecycleSettings.Create();
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Enable)
                    {
                        // 模拟服务端接受请求但 override 未真正开启（例如模式不受支持）。
                        return LifecycleResponses.Http(200,
                            LifecycleResponses.MakeStatus(false, 6, "test-client"));
                    }

                    if (call.Kind == LifecycleCallKind.Status)
                    {
                        return LifecycleResponses.Http(200,
                            LifecycleResponses.MakeStatus(false, 6, "test-client"));
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var game = Guid.NewGuid();
            using (var controller = CreateController(settings, transport, new[] { 30, 30 }, 3000))
            {
                controller.NotifyGameStarted(game, "Game", 10);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 3, 3000),
                    "状态驱动的 enable 应按对账上限停止（3 次 enable / 2 次 status）");

                Thread.Sleep(200);
                LifecycleAssert.Equal(3, transport.Count(LifecycleCallKind.Enable),
                    "对账上限用尽后不能无限重发 enable");
                LifecycleAssert.Equal(2, transport.Count(LifecycleCallKind.Status),
                    "status 对账次数必须有界");
            }
        }

        private static void SessionChangeReplacesStaleIdentity()
        {
            var settings = LifecycleSettings.Create();
            var statusServed = 0;
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Enable)
                    {
                        return LifecycleResponses.Active(7);
                    }

                    if (call.Kind == LifecycleCallKind.Status)
                    {
                        if (Interlocked.Exchange(ref statusServed, 1) == 0)
                        {
                            return LifecycleResponses.Http(200,
                                LifecycleResponses.MakeStatus(true, 8, "new-client"));
                        }

                        return LifecycleResponses.Inactive();
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var game = Guid.NewGuid();
            using (var controller = CreateController(settings, transport, new[] { 10000 }, 3000))
            {
                controller.NotifyGameStarted(game, "Game", 10);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 1, 2000),
                    "应发出 enable");

                controller.QueryStatus();
                LifecycleAssert.True(WaitUntil(() => statusServed == 1, 2000), "应执行状态查询");
                Thread.Sleep(50);

                controller.NotifyGameStopped(game);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Disable) == 1, 2000),
                    "应发出 disable");

                LifecycleAssert.Equal(8, transport.Last(LifecycleCallKind.Disable).SessionId,
                    "会话变化后应使用新会话，不能沿用旧 session_id=7");
            }
        }

        private static void MultiSessionStatusKeepsOwnedSession()
        {
            var settings = LifecycleSettings.Create();
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Enable)
                    {
                        return LifecycleResponses.Active(7);
                    }

                    if (call.Kind == LifecycleCallKind.Status)
                    {
                        var status = LifecycleResponses.MakeStatus(false, 0, null);
                        status.Sessions.Add(new WindowFollowSession
                        {
                            SessionId = 7,
                            ClientUuid = "client-a",
                            Running = true,
                            Override = new WindowFollowOverride { Enabled = true, Mode = CaptureModes.Foreground }
                        });
                        status.Sessions.Add(new WindowFollowSession
                        {
                            SessionId = 8,
                            ClientUuid = "client-b",
                            Running = true,
                            Override = new WindowFollowOverride { Enabled = false, Mode = CaptureModes.Foreground }
                        });
                        return LifecycleResponses.Http(200, status);
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var game = Guid.NewGuid();
            using (var controller = CreateController(settings, transport, new[] { 10000 }, 3000))
            {
                controller.NotifyGameStarted(game, "Game", 10);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 1, 2000),
                    "应发出初始 enable");

                controller.QueryStatus();
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Status) == 1, 2000),
                    "应执行多会话状态查询");
                Thread.Sleep(75);

                controller.NotifyGameStopped(game);
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Disable) == 1, 2000),
                    "应发出定向 disable");

                LifecycleAssert.Equal(7, transport.Last(LifecycleCallKind.Disable).SessionId,
                    "多会话 GET 不得清除已保存的 session_id");
                LifecycleAssert.Equal(1, CountKind(transport.Snapshot(), LifecycleCallKind.Enable),
                    "已启用的所属会话不应被重复 enable");
            }
        }

        private static void DisposeSerializesFinalDisableOnWorker()
        {
            var settings = LifecycleSettings.Create();
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Enable)
                    {
                        Thread.Sleep(150);
                        return LifecycleResponses.Active(7);
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var game = Guid.NewGuid();
            var controller = CreateController(settings, transport, new[] { 10000 }, 5000);
            var callerThreadId = Thread.CurrentThread.ManagedThreadId;

            controller.NotifyGameStarted(game, "Game", 10);
            LifecycleAssert.True(
                transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Enable) == 1, 2000),
                "worker 应开始处理 enable");

            var stopwatch = Stopwatch.StartNew();
            controller.Dispose();
            stopwatch.Stop();

            LifecycleAssert.True(stopwatch.ElapsedMilliseconds >= 100,
                "Dispose 应等待在途 enable 结束并完成最终 disable");

            var enable = transport.Last(LifecycleCallKind.Enable);
            var disable = transport.Last(LifecycleCallKind.Disable);
            LifecycleAssert.NotNull(disable, "Dispose 应通过 worker 发出最终 disable");
            LifecycleAssert.Equal(7, disable.SessionId, "最终 disable 应定向到 enable 返回的会话");
            LifecycleAssert.Equal(enable.ThreadId, disable.ThreadId, "enable/disable 必须在同一个 worker 线程上");
            LifecycleAssert.True(disable.ThreadId != callerThreadId,
                "Dispose 不能从调用线程另发网络请求");
        }

        private static void DisposeCancelsQueuedEnable()
        {
            var settings = LifecycleSettings.Create();
            settings.Settings.StartDelayMs = 2000;

            var transport = new LifecycleFakeTransport
            {
                Handler = call => call.Kind == LifecycleCallKind.Enable
                    ? LifecycleResponses.Active(3)
                    : LifecycleResponses.Inactive()
            };

            var game = Guid.NewGuid();
            var controller = CreateController(settings, transport, new[] { 10000 }, 3000);

            controller.NotifyGameStarted(game, "Game", 10);
            Thread.Sleep(30);

            var stopwatch = Stopwatch.StartNew();
            controller.Dispose();
            stopwatch.Stop();

            LifecycleAssert.True(stopwatch.ElapsedMilliseconds < 1500,
                "Dispose 应取消启动延迟，而不是等满 StartDelayMs");
            LifecycleAssert.Equal(0, transport.Count(LifecycleCallKind.Enable),
                "被取消的 enable 不能发出");
            LifecycleAssert.Equal(1, transport.Count(LifecycleCallKind.Disable),
                "Dispose 仍应通过 worker 发出最终 disable");
        }

        private static void StartupActiveOverrideIsDisabled()
        {
            var settings = LifecycleSettings.Create();
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Status)
                    {
                        return LifecycleResponses.Active(77);
                    }

                    if (call.Kind == LifecycleCallKind.Disable)
                    {
                        return LifecycleResponses.Inactive();
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            using (var controller = CreateController(settings, transport, new[] { 10000 }, 3000))
            {
                controller.QueryStatus();
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Disable) == 1, 2000),
                    "启动时发现旧 override 应发出定向 disable");

                LifecycleAssert.Equal(77, transport.Last(LifecycleCallKind.Disable).SessionId,
                    "启动清理必须使用状态响应中的 selected_session_id");
                LifecycleAssert.Equal(0, transport.Count(LifecycleCallKind.Enable),
                    "启动清理不应触发 enable");
            }
        }

        private static void ActiveStreamIsOnlyConfirmedByStatus()
        {
            var settings = LifecycleSettings.Create();
            var statusMode = 0;
            var transport = new LifecycleFakeTransport
            {
                Handler = call =>
                {
                    if (call.Kind == LifecycleCallKind.Status)
                    {
                        return statusMode == 0
                            ? LifecycleResponses.Http(404, null)
                            : LifecycleResponses.Active(5);
                    }

                    return LifecycleResponses.Inactive();
                }
            };

            var confirmations = 0;
            using (var controller = CreateController(settings, transport, new[] { 40, 40 }, 3000))
            {
                controller.ActiveStreamConfirmed += delegate
                {
                    Interlocked.Increment(ref confirmations);
                };

                controller.QueryStatus();
                LifecycleAssert.True(
                    transport.WaitFor(calls => CountKind(calls, LifecycleCallKind.Status) >= 1, 2000),
                    "应执行启动状态查询");
                Thread.Sleep(250);

                LifecycleAssert.Equal(0, Volatile.Read(ref confirmations),
                    "没有活动串流（404）时不能触发前台激活");

                statusMode = 1;
                controller.QueryStatus();
                LifecycleAssert.True(
                    WaitUntil(() => Volatile.Read(ref confirmations) == 1, 2000),
                    "确认活动串流后应触发一次前台激活回调");
            }
        }

        private static int CountKind(List<LifecycleCall> calls, LifecycleCallKind kind)
        {
            var count = 0;
            for (var index = 0; index < calls.Count; index++)
            {
                if (calls[index].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
#endif
