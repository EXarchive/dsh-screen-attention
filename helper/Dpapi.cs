// DPAPI（CryptUnprotectData）—— 只读解开 Chromium v10 cookie 的主密钥封装。
// 为什么放 helper：node 没有 FFI，插件侧每解一条就走 pwsh 太慢；
// 这里一次调用批量解，插件只负责 SQLite 与 AES-GCM。
// 与 Program.cs 一致：全局命名空间（Program 里没有 namespace 声明）
using System.Runtime.InteropServices;

static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>当前用户上下文解封一条 DPAPI blob；失败返回 null，lastError 带 Win32 码。</summary>
    public static byte[] Unprotect(byte[] blob, out int lastError)
    {
        lastError = 0;
        if (blob == null || blob.Length == 0) { lastError = -1; return null; }
        IntPtr inPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, inPtr, blob.Length);
            var inBlob = new DATA_BLOB { cbData = blob.Length, pbData = inPtr };
            var outBlob = new DATA_BLOB();
            // CRYPTPROTECT_UI_FORBIDDEN：后台进程弹不出 UI，必须禁掉提示
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, ref outBlob))
            {
                lastError = Marshal.GetLastWin32Error();
                return null;
            }
            try
            {
                var outp = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outp, 0, outBlob.cbData);
                return outp;
            }
            finally { LocalFree(outBlob.pbData); }
        }
        finally { Marshal.FreeHGlobal(inPtr); }
    }
}
