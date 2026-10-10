using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGuardian
{
    /// <summary>A top-level window with its title and the process that owns it.</summary>
    public sealed class TopWindow
    {
        public IntPtr Handle;
        public string Title;
        public int Pid;
    }

    /// <summary>Window enumeration and bringing to the foreground (Win32).</summary>
    public static class WindowTools
    {
        /// <summary>
        /// Visible main windows (minimized too) with a title: no windows
        /// "owned" by others (dialogs, popups) and no tool windows.
        /// </summary>
        public static List<TopWindow> TopLevelWindows()
        {
            var result = new List<TopWindow>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                var title = TitleOf(hwnd);
                if (string.IsNullOrEmpty(title)) return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                result.Add(new TopWindow { Handle = hwnd, Title = title, Pid = (int)pid });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static string TitleOf(IntPtr hwnd)
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return "";
            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool IsValid(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

        public static bool IsVisible(IntPtr hwnd) => IsValid(hwnd) && IsWindowVisible(hwnd);

        /// <summary>
        /// The window at the top of the "owner" chain. For the Windows Terminal console
        /// (a hidden window) it is the terminal window hosting it.
        /// </summary>
        public static IntPtr RootOwner(IntPtr hwnd) => IsValid(hwnd) ? GetAncestor(hwnd, GA_ROOTOWNER) : IntPtr.Zero;

        public static int ProcessOf(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            return (int)pid;
        }

        /// <summary>
        /// Brings the window to the foreground, restoring it if minimized.
        ///
        /// Windows allows this only to the foreground process: Claude Guardian is one right after
        /// the click in its menu, but if the permission has already expired it temporarily attaches
        /// its input to the active window's thread, as task switchers do.
        /// </summary>
        public static void BringToFront(IntPtr hwnd)
        {
            if (!IsValid(hwnd)) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;

            var foreground = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        /// <summary>Allows a just-launched process (e.g. the editor) to take the foreground.</summary>
        public static void AllowAnyForeground() => AllowSetForegroundWindow(ASFW_ANY);

        // MARK: - Win32

        private const uint GW_OWNER = 4;
        private const uint GA_ROOTOWNER = 3;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x00000080;
        private const int SW_RESTORE = 9;
        private const int ASFW_ANY = -1;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern IntPtr GetWindowLong32(IntPtr hwnd, int index);

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
