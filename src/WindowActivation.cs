using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SunshineWindowFollow
{
    /// <summary>
    /// Brings the Playnite window to the foreground after it was launched by
    /// Sunshine. Sunshine runs in the background, so Windows normally rejects
    /// focus changes from its child processes; without this the previous
    /// foreground window (remote agent/browser) stays on top and Playnite stays
    /// behind it on the streamed desktop.
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
        /// activates it. Never blocks Playnite startup.
        /// </summary>
        public static void ActivateOwnMainWindowAsync()
        {
            var thread = new Thread(ActivateOwnMainWindow)
            {
                IsBackground = true,
                Name = "SunshineWindowFollow.ActivateWindow"
            };
            thread.Start();
        }

        private static void ActivateOwnMainWindow()
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
                        ShowWindow(hwnd, SW_RESTORE);
                        RaiseWindow(hwnd);
                        if (!SetForegroundWindow(hwnd))
                        {
                            AttachAndActivate(hwnd);
                        }
                        return;
                    }
                    Thread.Sleep(250);
                }
            }
            catch (Exception exception)
            {
            }
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

            SetForegroundWindow(hwnd);

            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }
}
