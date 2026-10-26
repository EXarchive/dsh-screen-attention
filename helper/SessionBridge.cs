using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// ============ Session 0 -> 活动控制台会话 (Session 1+) 透明桥接器 ============
// 当宿主以 NT AUTHORITY\SYSTEM 运行在 Session 0（且位于绑定了 Session 0 的 Job Object 内）时，
// 直接 CreateProcessAsUserW 跨会话会因 Job Object 单会话约束返回 ERROR_ACCESS_DENIED (5)。
// 通过 PROC_THREAD_ATTRIBUTE_PARENT_PROCESS 指定 Session 1 的 winlogon/explorer 为父进程，
// 即可合法在 Session 1 (winsta0\default) 启动工作进程，并通过命名管道透明桥接 stdio。
static class SessionBridge
{
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
    [DllImport("kernel32.dll", SetLastError = true)] static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUserW(
        IntPtr hToken, string lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    const uint PROCESS_CREATE_PROCESS = 0x0080, PROCESS_DUP_HANDLE = 0x0040, PROCESS_QUERY_INFORMATION = 0x0400;
    const uint TOKEN_ASSIGN_PRIMARY = 0x0001, TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008, TOKEN_ADJUST_PRIVILEGES = 0x0020;
    const uint MAXIMUM_ALLOWED = 0x02000000;
    const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    const int SecurityImpersonation = 2, TokenPrimary = 1;
    const int STARTF_USESHOWWINDOW = 0x00000001;
    const uint CREATE_NO_WINDOW = 0x08000000, EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = new IntPtr(0x00020000);

    static void EnablePrivilege(string privName)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr hTok)) return;
        try
        {
            if (LookupPrivilegeValue(null, privName, out LUID luid))
            {
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(hTok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally { CloseHandle(hTok); }
    }

    static (IntPtr hParentProc, IntPtr hPrimaryToken) AcquireSessionParentAndToken(uint targetSession)
    {
        EnablePrivilege("SeTcbPrivilege");
        EnablePrivilege("SeAssignPrimaryTokenPrivilege");
        EnablePrivilege("SeIncreaseQuotaPrivilege");
        EnablePrivilege("SeDebugPrivilege");

        foreach (var procName in new[] { "winlogon", "explorer" })
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try
                {
                    if ((uint)p.SessionId != targetSession) continue;
                    IntPtr hProc = OpenProcess(PROCESS_CREATE_PROCESS | PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION, false, p.Id);
                    if (hProc == IntPtr.Zero) continue;
                    if (OpenProcessToken(hProc, TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY, out IntPtr hTok))
                    {
                        try
                        {
                            if (DuplicateTokenEx(hTok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr hPrimary))
                                return (hProc, hPrimary);
                        }
                        finally { CloseHandle(hTok); }
                    }
                    CloseHandle(hProc);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        return (IntPtr.Zero, IntPtr.Zero);
    }

    // Worker 端：在 Session 1 中运行，连接父进程传入的命名管道或 TCP 回环端口重定向 Console.In / Out
    public static bool AttachWorkerTransport(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--bridge-port" && int.TryParse(args[i + 1], out int port))
            {
                var client = new System.Net.Sockets.TcpClient();
                client.NoDelay = true;
                client.Connect(System.Net.IPAddress.Loopback, port);
                var stream = client.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
                var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                Console.SetIn(reader);
                Console.SetOut(TextWriter.Synchronized(writer));
                // 当父进程断开 TCP 连接时，自动退出 Session 1 子进程，杜绝孤儿进程
                var watchThread = new Thread(() =>
                {
                    try
                    {
                        while (client.Connected) Thread.Sleep(500);
                    }
                    catch { }
                    Environment.Exit(0);
                }) { IsBackground = true };
                watchThread.Start();
                return true;
            }
        }
        return false;
    }

    public static bool TryBridgeIfSession0(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (AttachWorkerTransport(args)) return false;

        int mySession = Process.GetCurrentProcess().SessionId;
        if (mySession != 0) return false;

        uint activeSession = WTSGetActiveConsoleSessionId();
        if (activeSession == 0 || activeSession == 0xFFFFFFFF) return false;

        var (hParent, hToken) = AcquireSessionParentAndToken(activeSession);
        if (hParent == IntPtr.Zero || hToken == IntPtr.Zero)
        {
            if (hParent != IntPtr.Zero) CloseHandle(hParent);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            return false;
        }

        // 使用本地回环 TCP (127.0.0.1:0) 作为跨 Session 0 <-> Session 1 零权限障碍通道
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        IntPtr attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        IntPtr attrList = Marshal.AllocHGlobal(attrSize);
        IntPtr parentVal = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(parentVal, hParent);

        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule.FileName;
        var cmdLine = new StringBuilder("\"" + exePath + "\" --bridge-port " + port);

        bool ok = false;
        PROCESS_INFORMATION pi = default;
        int cpErr = 0;
        try
        {
            if (InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize) &&
                UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, parentVal, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                var siEx = new STARTUPINFOEX();
                siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                siEx.StartupInfo.lpDesktop = "winsta0\\default";
                siEx.StartupInfo.dwFlags = STARTF_USESHOWWINDOW;
                siEx.StartupInfo.wShowWindow = 0;
                siEx.lpAttributeList = attrList;

                ok = CreateProcessAsUserW(
                    hToken, exePath, cmdLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                    IntPtr.Zero, AppContext.BaseDirectory,
                    ref siEx, out pi);
                cpErr = Marshal.GetLastWin32Error();
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            Marshal.FreeHGlobal(parentVal);
            CloseHandle(hParent);
            CloseHandle(hToken);
        }

        if (!ok)
        {
            Console.Error.WriteLine("[bridge] CreateProcessAsUserW failed err=" + cpErr);
            try { listener.Stop(); } catch { }
            return false;
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { TerminateProcess(pi.hProcess, 0); } catch { } };

        System.Net.Sockets.TcpClient workerConn = null;
        try
        {
            var acceptTask = listener.AcceptTcpClientAsync();
            if (!acceptTask.Wait(4000))
            {
                try { TerminateProcess(pi.hProcess, 1); } catch { }
                return false;
            }
            workerConn = acceptTask.Result;
            workerConn.NoDelay = true;
        }
        finally { try { listener.Stop(); } catch { } }

        var netStream = workerConn.GetStream();

        var inThread = new Thread(() =>
        {
            try
            {
                using var src = Console.OpenStandardInput();
                var buf = new byte[4096];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    netStream.Write(buf, 0, n);
                    netStream.Flush();
                }
            }
            catch { }
            try { workerConn.Close(); } catch { }
            try { TerminateProcess(pi.hProcess, 0); } catch { }
        }) { IsBackground = true };

        var outThread = new Thread(() =>
        {
            try
            {
                using var dst = Console.OpenStandardOutput();
                var buf = new byte[4096];
                int n;
                while ((n = netStream.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    dst.Flush();
                }
            }
            catch { }
        }) { IsBackground = true };

        inThread.Start();
        outThread.Start();

        WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
        outThread.Join(500);
        GetExitCodeProcess(pi.hProcess, out uint code);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        exitCode = (int)code;
        return true;
    }
}
