using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Playnite.SDK;

namespace SunshineWindowFollow
{
    /// <summary>
    /// Brings the Playnite window to the foreground after an active Sunshine
    /// stream session has been confirmed. The caller must not invoke this for
    /// ordinary local launches: doing so would steal focus from whatever the
    /// user is doing every time Playnite starts.
    /// </summary>
    internal static class WindowActivation
    {
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private static readonly IntPtr HWND_TOP = IntPtr.Zero;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        /// <summary>
        /// Raises a window to the top of the Z-order. Locked or disconnected RDP
        /// sessions reject SetForegroundWindow(), but the Z-order can still be
        /// changed, which is enough for the streamed desktop to show the window.
        /// </summary>
        private static void RaiseWindow(IntPtr hwnd)
        {
            const uint noMoveNoSize = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
            BringWindowToTop(hwnd);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, noMoveNoSize | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, noMoveNoSize);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, noMoveNoSize);
        }

        /// <summary>
        /// Starts a background thread that waits for Playnite's main window and
        /// activates it. Never blocks Playnite startup. Call this only after an
        /// active stream session has been confirmed.
        /// </summary>
        public static void ActivateOwnMainWindowAsync(ILogger logger)
        {
            var thread = new Thread(new ThreadStart(delegate { ActivateOwnMainWindow(logger); }))
            {
                IsBackground = true,
                Name = "SunshineWindowFollow.ActivateWindow"
            };
            thread.Start();
        }

        /// <summary>兼容旧调用；没有日志时仍执行同样的验证，只是不记录结果。</summary>
        public static void ActivateOwnMainWindowAsync()
        {
            ActivateOwnMainWindowAsync(null);
        }

        private static void ActivateOwnMainWindow(ILogger logger)
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    process.Refresh();
                    var hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                    {
                        if (GetForegroundWindow() == hwnd)
                        {
                            Info(logger, "Playnite 窗口已经在活动串流的前台，无需切换");
                            return;
                        }

                        var result = TryActivate(hwnd);
                        if (result)
                        {
                            Info(logger, "已将 Playnite 窗口切到活动串流的前台");
                        }
                        else
                        {
                            Warn(logger, "已请求激活 Playnite 窗口，但前台窗口验证未通过（可能被系统前台锁拒绝）");
                        }

                        return;
                    }

                    Thread.Sleep(250);
                }

                Warn(logger, "等待 Playnite 主窗口超时（20 秒），跳过前台激活");
            }
            catch (Exception exception)
            {
                if (logger != null)
                {
                    logger.Error(exception, "激活 Playnite 窗口时发生异常");
                }
            }
        }

        private static bool TryActivate(IntPtr hwnd)
        {
            ShowWindow(hwnd, SW_RESTORE);
            RaiseWindow(hwnd);

            var foreground = SetForegroundWindow(hwnd);
            if (!foreground || GetForegroundWindow() != hwnd)
            {
                AttachAndActivate(hwnd);
            }

            // SetForegroundWindow 是异步的，稍等再验证最终前台窗口。
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (GetForegroundWindow() == hwnd)
                {
                    return true;
                }

                Thread.Sleep(50);
            }

            return false;
        }

        private static void AttachAndActivate(IntPtr hwnd)
        {
            var foreground = GetForegroundWindow();
            var foregroundThread = foreground != IntPtr.Zero
                ? GetWindowThreadProcessId(foreground, IntPtr.Zero)
                : 0;
            var currentThread = GetCurrentThreadId();
            var attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            }

            try
            {
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }

        private static void Info(ILogger logger, string message)
        {
            if (logger != null)
            {
                logger.Info(message);
            }
        }

        private static void Warn(ILogger logger, string message)
        {
            if (logger != null)
            {
                logger.Warn(message);
            }
        }
    }
}
