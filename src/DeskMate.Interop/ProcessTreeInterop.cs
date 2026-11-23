using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 进程树查询：给「点击切回窗口」兜底用 —— 有些应用的 Dock 图标对应的是启动器壳进程，
/// 绿点亮的是壳（进程名匹配上了），真窗口却开在它派生的子进程里，光按壳的进程名找窗口永远找不到。
/// 用 <c>NtQueryInformationProcess</c> 读父进程号，比 WMI 的 <c>Win32_Process</c> 快两个数量级。
/// 只需要 <c>PROCESS_QUERY_LIMITED_INFORMATION</c> 权限：对绝大多数进程都读得到，
/// 提权进程读不到就让那条分支自然断掉 —— 断了只是切不回，不会误切到别家的窗口。
/// </summary>
public static class ProcessTreeInterop
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NtProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2;
        public IntPtr Reserved3;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref NtProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>读进程的父进程号；读不到（进程已退出、权限不够）返回 false。</summary>
    public static bool TryGetParentProcessId(int processId, out int parentProcessId)
    {
        parentProcessId = 0;

        var handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var info = new NtProcessBasicInformation();
            var status = NtQueryInformationProcess(
                handle, ProcessBasicInformation, ref info, Marshal.SizeOf<NtProcessBasicInformation>(), out _);
            if (status != 0)
            {
                return false;
            }

            parentProcessId = (int)info.InheritedFromUniqueProcessId;
            return parentProcessId != 0;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }
}
