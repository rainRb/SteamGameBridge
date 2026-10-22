using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SteamGameBridge.App;

internal sealed record GameProcessIdentity(int Pid, DateTime CreatedUtc, int SessionId, string FullPath);

internal static class GameProcessTracker
{
    // QueryLimitedInformation + Synchronize only: never request VM_READ, VM_WRITE,
    // CreateThread or all-access permissions to inspect a protected game.
    internal static SafeProcessHandle OpenLifetimeHandle(int pid)
    {
        var handle = OpenProcess(0x00100000 | 0x1000, false, pid);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }
    internal static GameProcessIdentity Identity(Process process)
    {
        using var handle = OpenLifetimeHandle(process.Id);
        var name = new StringBuilder(32768); int size = name.Capacity;
        if (!QueryFullProcessImageName(handle, 0, name, ref size) || !GetProcessTimes(handle, out long created, out _, out _, out _) || !ProcessIdToSessionId((uint)process.Id, out uint session))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(process.Id, DateTime.FromFileTimeUtc(created), checked((int)session), Path.GetFullPath(name.ToString()));
    }
    internal static int WaitForExit(SafeProcessHandle handle)
    {
        if (WaitForSingleObject(handle, uint.MaxValue) != 0 || !GetExitCodeProcess(handle, out uint code)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((int)code);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    internal static bool IsRunning(string path)
    {
        using var current = Process.GetCurrentProcess();
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (candidate)
            {
                try
                {
                    if (candidate.SessionId != current.SessionId) continue;
                    if (Identity(candidate).FullPath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception ex) { throw new IOException("无法核对同名游戏进程的完整路径。请统一 Steam 与游戏的权限后重试。", ex); }
            }
        }
        return false;
    }
    internal static bool Matches(GameProcessIdentity process, string expectedPath, DateTime earliestUtc, int sessionId) => process.SessionId == sessionId
        && process.CreatedUtc >= earliestUtc && process.FullPath.Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);

    internal static Process ConfirmDirect(int pid, string path, DateTime since)
    {
        var process = Process.GetProcessById(pid);
        try
        {
            using var current = Process.GetCurrentProcess();
            if (!Matches(Identity(process), path, since, current.SessionId)) throw new IOException("直接启动的进程与本次实际游戏不一致。");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    internal static Process WaitForGame(string path, DateTime since, TimeSpan timeout)
    {
        using var current = Process.GetCurrentProcess();
        var watch = Stopwatch.StartNew(); string? lastError = null;
        while (watch.Elapsed < timeout)
        {
            var matches = new List<Process>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                bool keep = false;
                try { if (Matches(Identity(process), path, since, current.SessionId)) { matches.Add(process); keep = true; } }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) { lastError = ex.Message; }
                finally { if (!keep) process.Dispose(); }
            }
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) { foreach (var process in matches) process.Dispose(); throw new IOException("同一路径出现多个新游戏进程，无法确认本次会话。"); }
            Thread.Sleep(100);
        }
        throw new IOException("等待实际游戏进程超时；未使用启动器或同名进程维持 Steam 状态。请检查路径、权限及启动器中的开始按钮。" + (lastError is null ? "" : " 原因：" + lastError));
    }
}
