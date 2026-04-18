using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace DefectScout.Core.Services;

/// <summary>
/// Ensures child processes spawned by this application (Copilot CLI, node.exe, Playwright, etc.)
/// are cleaned up when the application exits or when a run completes.
///
/// Two mechanisms work together:
/// 1. Windows Job Object (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) — assigns the current process to
///    a job so that ALL descended child processes are killed by the OS when the job handle is
///    closed (i.e. when this process exits, even abnormally).
/// 2. Explicit kill — walks the Win32 Toolhelp32 process snapshot to find and kill any node.exe
///    descendants of this process.  Called after a run completes so the data folder is released
///    immediately without waiting for the app to close.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessGuard
{
    private static readonly ILogger _log = Log.ForContext(typeof(ProcessGuard));
    private static IntPtr _jobHandle = IntPtr.Zero;

    // ── Win32 P/Invoke ───────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int infoType,
        ref JobObjectExtendedLimitInformation lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    // ── Constants ────────────────────────────────────────────────────────────

    private const int  JobObjectExtendedLimitInformationType = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE    = 0x2000;
    private const uint TH32CS_SNAPPROCESS                     = 0x00000002;

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // ── Structs ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount,  WriteTransferCount,  OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long    PerProcessUserTimeLimit;
        public long    PerJobUserTimeLimit;
        public uint    LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint    ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint    PriorityClass;
        public uint    SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint    dwSize;
        public uint    cntUsage;
        public uint    th32ProcessID;
        public IntPtr  th32DefaultHeapID;
        public uint    th32ModuleID;
        public uint    cntThreads;
        public uint    th32ParentProcessID;
        public int     pcPriClassBase;
        public uint    dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string  szExeFile;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Windows Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> and assigns
    /// the current process to it.  All subsequently spawned child processes inherit the job and
    /// are killed automatically by the OS when this process exits (even abnormally).
    /// Safe to call multiple times — only initialises once.
    /// </summary>
    public static void Initialize()
    {
        if (_jobHandle != IntPtr.Zero) return;

        var job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (job == IntPtr.Zero)
        {
            _log.Warning("ProcessGuard: CreateJobObject failed (Win32 error {Code}). " +
                         "Falling back to explicit kill only.", Marshal.GetLastWin32Error());
            return;
        }

        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        uint size = (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>();

        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationType, ref info, size))
        {
            _log.Warning("ProcessGuard: SetInformationJobObject failed (Win32 error {Code}).",
                Marshal.GetLastWin32Error());
            CloseHandle(job);
            return;
        }

        if (!AssignProcessToJobObject(job, GetCurrentProcess()))
        {
            // ERROR_ACCESS_DENIED (5) is expected when running under a debugger or a CI
            // environment that has already assigned the process to its own job object.
            // On Windows 8+ nested jobs are supported, but the host job's flags may still
            // prevent re-assignment.  Log and fall back to the explicit kill path.
            _log.Warning("ProcessGuard: AssignProcessToJobObject failed (Win32 error {Code}). " +
                         "Expected under Visual Studio / CI runners. " +
                         "Falling back to explicit child-kill on shutdown.",
                Marshal.GetLastWin32Error());
            CloseHandle(job);
            return;
        }

        _jobHandle = job;
        _log.Information("ProcessGuard: Job Object active — all child processes will be killed on app exit.");
    }

    /// <summary>
    /// Immediately kills all <c>node.exe</c> processes that are descendants of this process.
    /// Call after a run finishes to release any file-system locks on the data folder, and also
    /// during application shutdown as a belt-and-suspenders measure alongside the Job Object.
    /// </summary>
    public static void KillDescendantNodeProcesses()
    {
        var ownPid    = Environment.ProcessId;
        var parentMap = BuildParentMap();
        if (parentMap.Count == 0) return;

        int killed = 0;
        foreach (var (pid, (_, name)) in parentMap)
        {
            if (!name.Equals("node", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsDescendantOf(pid, ownPid, parentMap))                  continue;

            try
            {
                var p = Process.GetProcessById(pid);
                _log.Information("ProcessGuard: Killing descendant node.exe PID {Pid}", pid);
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Exception ex)
            {
                // Process may have already exited between snapshot and kill — expected, not an error.
                _log.Debug(ex, "ProcessGuard: Could not kill node.exe PID {Pid} (may have already exited)", pid);
            }
        }

        if (killed > 0)
            _log.Information("ProcessGuard: Killed {Count} descendant node.exe process(es)", killed);
        else
            _log.Debug("ProcessGuard: No descendant node.exe processes found");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a snapshot of all running processes as: pid → (parentPid, processNameWithoutExtension).
    /// Uses CreateToolhelp32Snapshot so no process handles are required (works for all processes).
    /// </summary>
    private static Dictionary<int, (int parentPid, string name)> BuildParentMap()
    {
        var map  = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return map;

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref entry)) return map;

            do
            {
                var exeName = Path.GetFileNameWithoutExtension(entry.szExeFile ?? string.Empty);
                map[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, exeName);
            }
            while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }

        return map;
    }

    private static bool IsDescendantOf(
        int pid,
        int ancestorPid,
        Dictionary<int, (int parentPid, string)> parentMap)
    {
        var visited = new HashSet<int>();
        var current = pid;

        while (parentMap.TryGetValue(current, out var info))
        {
            if (!visited.Add(current))    break; // cycle guard
            if (info.parentPid == ancestorPid) return true;
            current = info.parentPid;
        }

        return false;
    }
}
