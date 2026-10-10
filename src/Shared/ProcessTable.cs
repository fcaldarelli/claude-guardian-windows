using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGuardian
{
    /// <summary>A process in the snapshot: PID, parent PID and executable name.</summary>
    public sealed class ProcessEntry
    {
        public int Pid;
        public int ParentPid;
        public string ExeName;
    }

    /// <summary>
    /// Snapshot of running processes (Toolhelp32) and a few supporting Win32 functions.
    ///
    /// On Windows PIDs are reused quickly and the recorded "parent" of a process
    /// may have died and been replaced by another process with the same PID:
    /// that is why start times are compared too.
    /// </summary>
    public static class ProcessTable
    {
        public static Dictionary<int, ProcessEntry> Snapshot()
        {
            var result = new Dictionary<int, ProcessEntry>();
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE) return result;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snapshot, ref entry)) return result;
                do
                {
                    result[(int)entry.th32ProcessID] = new ProcessEntry
                    {
                        Pid = (int)entry.th32ProcessID,
                        ParentPid = (int)entry.th32ParentProcessID,
                        ExeName = entry.szExeFile,
                    };
                } while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return result;
        }

        /// <summary>Full path of the executable, or null if the process is not accessible.</summary>
        public static string ImagePath(int pid)
        {
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                var size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>Process start time (FILETIME UTC), 0 if unavailable.</summary>
        public static long StartTime(int pid)
        {
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return 0;
            try
            {
                return GetProcessTimes(handle, out var created, out _, out _, out _) ? created : 0;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>
        /// Does the process still exist? If startTime is known, also checks that it is the same
        /// process and not another one that was given the same PID.
        /// </summary>
        public static bool IsAlive(int pid, long startTime)
        {
            if (pid <= 4) return false;
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
            {
                // Access denied: the process exists but belongs to another user or is elevated.
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            }
            try
            {
                if (!GetExitCodeProcess(handle, out var code) || code != STILL_ACTIVE) return false;
                if (startTime == 0) return true;
                return !GetProcessTimes(handle, out var created, out _, out _, out _) || created == startTime;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>
        /// Is the recorded parent really the parent? It must have started before the child,
        /// otherwise the original parent died and the PID was reassigned.
        /// </summary>
        public static bool IsGenuineParent(int parentPid, int childPid)
        {
            var parent = StartTime(parentPid);
            var child = StartTime(childPid);
            return parent == 0 || child == 0 || parent <= child;
        }

        public static string LastErrorMessage() => new Win32Exception(Marshal.GetLastWin32Error()).Message;

        // MARK: - Win32

        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint STILL_ACTIVE = 259;
        private const int ERROR_ACCESS_DENIED = 5;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);
    }
}
