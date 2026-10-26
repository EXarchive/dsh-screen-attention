using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Text.Json;

// ============ P/Invoke: GDI capture ============
static class Gdi
{
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    public static void EnablePerMonitorDpi()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
        try { SetProcessDPIAware(); } catch { }
    }

    public static int ScreenW()
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        int pw = dc != IntPtr.Zero ? GetDeviceCaps(dc, 118) : 0;
        if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
        int sm = GetSystemMetrics(0);
        return Math.Max(pw, sm);
    }

    public static int ScreenH()
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        int ph = dc != IntPtr.Zero ? GetDeviceCaps(dc, 117) : 0;
        if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc);
        int sm = GetSystemMetrics(1);
        return Math.Max(ph, sm);
    }
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] data, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    public static volatile bool LastSecureDesktop = false;
    public static long PrivacyRedactedCount = 0;
    static readonly object PrivLock = new object();
    static List<string> _privacyKeywords = InitPrivacyKeywords();
    static List<Rectangle> _cachedPrivRects = new List<Rectangle>();
    static long _privCacheTick = 0;

    static List<string> InitPrivacyKeywords()
    {
        var list = new List<string> { "1Password", "KeePass", "Bitwarden", "凭据管理器", "Windows 安全中心", "__SA_PRIVACY_TEST__" };
        string env = Environment.GetEnvironmentVariable("SA_PRIVACY_TITLES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var p in env.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = p.Trim();
                if (s.Length > 0 && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
            }
        }
        return list;
    }

    public static string[] GetPrivacyKeywords() { lock (PrivLock) return _privacyKeywords.ToArray(); }

    static List<Rectangle> GetPrivacyRects()
    {
        long now = Environment.TickCount64;
        lock (PrivLock)
        {
            if (now - _privCacheTick < 200) return _cachedPrivRects;
            _privCacheTick = now;
            var rects = new List<Rectangle>();
            var kws = _privacyKeywords.ToArray();
            if (kws.Length == 0) { _cachedPrivRects = rects; return rects; }
            Win.EnumWindows((h, p) =>
            {
                if (!Win.IsWindowVisible(h) || Win.IsIconic(h)) return true;
                var sb = new StringBuilder(256);
                Win.GetWindowText(h, sb, 256);
                string title = sb.ToString();
                if (string.IsNullOrEmpty(title)) return true;
                for (int i = 0; i < kws.Length; i++)
                {
                    if (title.IndexOf(kws[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (Win.GetWindowRect(h, out var r) && r.R > r.L && r.B > r.T)
                            rects.Add(new Rectangle(r.L, r.T, r.R - r.L, r.B - r.T));
                        break;
                    }
                }
                return true;
            }, IntPtr.Zero);
            _cachedPrivRects = rects;
            return rects;
        }
    }

    public static void RedactRect(byte[] buf, int bw, int bh, int capX, int capY, Rectangle winRect)
    {
        int x0 = Math.Max(0, winRect.X - capX), y0 = Math.Max(0, winRect.Y - capY);
        int x1 = Math.Min(bw, winRect.Right - capX), y1 = Math.Min(bh, winRect.Bottom - capY);
        if (x1 <= x0 || y1 <= y0) return;
        for (int py = y0; py < y1; py++)
        {
            int rowOff = py * bw * 4;
            for (int px = x0; px < x1; px++)
            {
                int off = rowOff + px * 4;
                buf[off] = 24; buf[off + 1] = 24; buf[off + 2] = 24; buf[off + 3] = 255;
            }
        }
        System.Threading.Interlocked.Increment(ref PrivacyRedactedCount);
    }

    // capture virtual screen region into top-down BGRA buffer (with C10 SecureDesktop tolerance & B14 Privacy Mask)
    public static (byte[] buf, int w, int h) Capture(int x, int y, int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var buf = new byte[w * h * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) { LastSecureDesktop = true; return (buf, w, h); }
        IntPtr mem = CreateCompatibleDC(dc);
        IntPtr bmp = CreateCompatibleBitmap(dc, w, h);
        IntPtr old = SelectObject(mem, bmp);
        bool ok = BitBlt(mem, 0, 0, w, h, dc, x, y, 0x00CC0020); // SRCCOPY
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
        int lines = ok ? GetDIBits(mem, bmp, 0, (uint)h, buf, ref bi, 0) : 0;
        SelectObject(mem, old);
        DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, dc);
        LastSecureDesktop = !ok || lines <= 0;
        if (!LastSecureDesktop)
        {
            var privRects = GetPrivacyRects();
            for (int i = 0; i < privRects.Count; i++) RedactRect(buf, w, h, x, y, privRects[i]);
        }
        return (buf, w, h);
    }

    // capture + optional scale (StretchBlt HALFTONE), returns top-down BGRA
    public static (byte[] buf, int w, int h) CaptureScaled(int x, int y, int w, int h, double scale)
    {
        var (src, sw, sh) = Capture(x, y, w, h);
        if (Math.Abs(scale - 1.0) < 0.01) return (src, sw, sh);
        int dw = Math.Max(1, (int)Math.Round(sw * scale));
        int dh = Math.Max(1, (int)Math.Round(sh * scale));
        IntPtr dc = GetDC(IntPtr.Zero);
        IntPtr smem = CreateCompatibleDC(dc), dmem = CreateCompatibleDC(dc);
        IntPtr sbmp = CreateCompatibleBitmap(dc, sw, sh), dbmp = CreateCompatibleBitmap(dc, dw, dh);
        IntPtr sOld = SelectObject(smem, sbmp), dOld = SelectObject(dmem, dbmp);
        // source bitmap from buffer: build via SetDIBits
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = sw, biHeight = -sh, biPlanes = 1, biBitCount = 32, biCompression = 0 };
        SetDIBits(smem, sbmp, 0, (uint)sh, src, ref bi, 0);
        SetStretchBltMode(dmem, 4); // HALFTONE
        StretchBlt(dmem, 0, 0, dw, dh, smem, 0, 0, sw, sh, 0x00CC0020);
        var outBuf = new byte[dw * dh * 4];
        var bo = new BITMAPINFOHEADER { biSize = 40, biWidth = dw, biHeight = -dh, biPlanes = 1, biBitCount = 32, biCompression = 0 };
        GetDIBits(dmem, dbmp, 0, (uint)dh, outBuf, ref bo, 0);
        SelectObject(smem, sOld); SelectObject(dmem, dOld);
        DeleteObject(sbmp); DeleteObject(dbmp);
        DeleteDC(smem); DeleteDC(dmem); ReleaseDC(IntPtr.Zero, dc);
        return (outBuf, dw, dh);
    }

    [DllImport("gdi32.dll")] public static extern int SetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] data, ref BITMAPINFOHEADER bi, uint usage);
}

// ============ P/Invoke: GDI+ JPEG encode (system gdiplus.dll, no NuGet) ============
static class GdiPlus
{
    [StructLayout(LayoutKind.Sequential)] public struct StartupInput { public uint Version; public IntPtr Callback; public bool NoThread; public bool NoCodecs; }
    [DllImport("gdiplus.dll")] public static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, out IntPtr output);
    [DllImport("gdiplus.dll")] public static extern void GdiplusShutdown(IntPtr token);
    [DllImport("gdiplus.dll")] public static extern int GdipCreateBitmapFromHBITMAP(IntPtr hbm, IntPtr hpal, out IntPtr bitmap);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] public static extern int GdipSaveImageToFile(IntPtr image, string filename, ref Guid clsid, IntPtr encParams);
    [DllImport("gdiplus.dll")] public static extern int GdipDisposeImage(IntPtr image);

    static readonly Guid JpegClsid = new Guid("{557CF401-1A04-11D3-9A73-0000F81EF32E}");

    public static byte[] EncodeJpeg(byte[] bgra, int w, int h, string tmpFile)
    {
        IntPtr dc = Gdi.GetDC(IntPtr.Zero);
        IntPtr mem = Gdi.CreateCompatibleDC(dc);
        IntPtr bmp = Gdi.CreateCompatibleBitmap(dc, w, h);
        IntPtr old = Gdi.SelectObject(mem, bmp);
        var bi = new Gdi.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
        Gdi.SetDIBits(mem, bmp, 0, (uint)h, bgra, ref bi, 0);

        var input = new StartupInput { Version = 1 };
        IntPtr token, output;
        int st = GdiplusStartup(out token, ref input, out output);
        IntPtr image;
        int hr = GdipCreateBitmapFromHBITMAP(bmp, IntPtr.Zero, out image);
        int saveHr = -1;
        if (hr == 0) { var clsid = JpegClsid; saveHr = GdipSaveImageToFile(image, tmpFile, ref clsid, IntPtr.Zero); GdipDisposeImage(image); }
        if (st == 0) GdiplusShutdown(token);

        Gdi.SelectObject(mem, old);
        Gdi.DeleteObject(bmp); Gdi.DeleteDC(mem); Gdi.ReleaseDC(IntPtr.Zero, dc);
        bool dirWritable = false;
        try { var p = tmpFile + ".probe"; File.WriteAllText(p, "x"); File.Delete(p); dirWritable = true; } catch { }
        if (hr != 0 || saveHr != 0 || !File.Exists(tmpFile)) throw new Exception("jpeg encode failed st=" + st + " hr=" + hr + " saveHr=" + saveHr + " dirWritable=" + dirWritable + " path=" + tmpFile);
        var bytes = File.ReadAllBytes(tmpFile);
        File.Delete(tmpFile);
        return bytes;
    }
}

// ============ P/Invoke: input / windows / idle / hotkey ============
static class Win
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    // Windows INPUT 是 40 字节 union（x64）：type + padding + max(MOUSEINPUT 32B, KEYBDINPUT 24B)
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize, dwTime; }
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    [DllImport("kernel32.dll")] public static extern uint GetTickCount();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    // 把窗口置顶到最高层（防其他置顶窗口抢占）；HWND_TOPMOST + NOACTIVATE + 不改尺寸位置
    public static void RaiseTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW); } catch { }
    }
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    // 窗口激活
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    // 剪贴板
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint fmt, IntPtr mem);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    public static readonly int INPUT_SIZE = Marshal.SizeOf(typeof(INPUT));
    public static uint Now() => GetTickCount();
    public static uint IdleMs()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        GetLastInputInfo(ref li);
        return GetTickCount() - li.dwTime;
    }

    static void Send(INPUT[] a) { if (SendInput((uint)a.Length, a, INPUT_SIZE) != a.Length) throw new Exception("SendInput failed, err=" + Marshal.GetLastWin32Error()); }

    public static void MouseMove(int x, int y)
    {
        // MOUSEEVENTF_ABSOLUTE 要求 dx/dy 归一化到 0..65535
        int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
        int dx = sw > 0 ? (int)(x * 65536.0 / sw) : 0;
        int dy = sh > 0 ? (int)(y * 65536.0 / sh) : 0;
        var a = new INPUT[1];
        a[0].type = 0; a[0].mi.dx = dx; a[0].mi.dy = dy; a[0].mi.flags = 0x8001; // ABSOLUTE|MOVE
        Send(a);
    }
    public static void MouseButton(uint down, uint up, int clicks = 1)
    {
        var a = new INPUT[clicks * 2];
        for (int i = 0; i < clicks; i++)
        {
            a[i * 2].type = 0; a[i * 2].mi.flags = down;
            a[i * 2 + 1].type = 0; a[i * 2 + 1].mi.flags = up;
        }
        Send(a);
    }
    // 单次按下/释放：拖拽用，避免 MouseButton 成对 down+up 造成双击
    public static void MouseDown(uint flags)
    {
        var a = new INPUT[1];
        a[0].type = 0; a[0].mi.flags = flags;
        Send(a);
    }
    public static void MouseUp(uint flags)
    {
        var a = new INPUT[1];
        a[0].type = 0; a[0].mi.flags = flags;
        Send(a);
    }
    public static void MouseWheel(int amount)
    {
        var a = new INPUT[1];
        a[0].type = 0; a[0].mi.data = (uint)amount; a[0].mi.flags = 0x0800; // WHEEL
        Send(a);
    }
    public static void TypeText(string text)
    {
        var a = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            a[i * 2].type = 1; a[i * 2].ki.scan = text[i]; a[i * 2].ki.flags = 0x0004; // UNICODE
            a[i * 2 + 1].type = 1; a[i * 2 + 1].ki.scan = text[i]; a[i * 2 + 1].ki.flags = 0x0004 | 0x0002; // UNICODE|KEYUP
        }
        Send(a);
    }
    public static void KeyPress(ushort vk)
    {
        var a = new INPUT[2];
        a[0].type = 1; a[0].ki.vk = vk;
        a[1].type = 1; a[1].ki.vk = vk; a[1].ki.flags = 0x0002;
        Send(a);
    }

    public static string ForegroundTitle()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                GetWindowText(fg, sb, 256);
                string t = sb.ToString().Trim();
                if (t.Length > 0) return t.Length > 36 ? t.Substring(0, 36) + "…" : t;
            }
            var wins = ListWindows();
            if (wins.Count > 0 && wins[0].TryGetValue("title", out var tv))
            {
                string wt = Convert.ToString(tv).Trim();
                if (wt.Length > 0) return wt.Length > 36 ? wt.Substring(0, 36) + "…" : wt;
            }
            return "桌面";
        }
        catch { return "桌面"; }
    }

    public static List<Dictionary<string, object>> ListWindows()
    {
        var list = new List<Dictionary<string, object>>();
        IntPtr fg = GetForegroundWindow();
        EnumWindows((h, p) =>
        {
            if (!IsWindowVisible(h)) return true;
            int cloaked = 0;
            DwmGetWindowAttribute(h, 14, out cloaked, sizeof(int));
            if (cloaked != 0) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            string t = sb.ToString();
            if (t.Length == 0) return true;
            RECT r; if (!GetWindowRect(h, out r)) return true;
            if (r.R - r.L < 80 || r.B - r.T < 60) return true;
            var cb = new StringBuilder(256); GetClassName(h, cb, 256);
            uint pid; GetWindowThreadProcessId(h, out pid);
            list.Add(new Dictionary<string, object> {
                { "hwnd", (long)h.ToInt64() }, { "title", t }, { "cls", cb.ToString() },
                { "x", r.L }, { "y", r.T }, { "w", r.R - r.L }, { "h", r.B - r.T },
                { "pid", pid }, { "fg", h == fg },
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static ushort VkOf(string name)
    {
        string n = name.ToUpperInvariant();
        switch (n)
        {
            case "ENTER": return 0x0D; case "ESC": return 0x1B; case "TAB": return 0x09;
            case "BACKSPACE": return 0x08; case "DELETE": return 0x2E; case "INSERT": return 0x2D;
            case "UP": return 0x26; case "DOWN": return 0x28; case "LEFT": return 0x25; case "RIGHT": return 0x27;
            case "HOME": return 0x24; case "END": return 0x23; case "PAGEUP": return 0x21; case "PAGEDOWN": return 0x22;
            case "SPACE": return 0x20; case "ALT": return 0x12; case "CTRL": return 0x11; case "SHIFT": return 0x10;
            case "WIN": case "LWIN": return 0x5B; case "APPS": return 0x5D;
            case "CAPSLOCK": return 0x14; case "NUMLOCK": return 0x90; case "SCROLLLOCK": return 0x91;
            case "PRINTSCREEN": return 0x2C; case "PAUSE": return 0x13; case "NUMPAD0": return 0x60;
        }
        // F1-F12 → 0x70..0x7B
        if (n.Length >= 2 && n[0] == 'F' && int.TryParse(n.Substring(1), out int fn) && fn >= 1 && fn <= 12)
            return (ushort)(0x70 + fn - 1);
        // 单字符字母/数字 → 虚拟键码即 ASCII 大写
        if (n.Length == 1 && ((n[0] >= 'A' && n[0] <= 'Z') || (n[0] >= '0' && n[0] <= '9')))
            return (ushort)n[0];
        throw new Exception("unsupported key: " + name);
    }

    public static INPUT KeyInput(ushort vk, bool up)
    {
        var a = new INPUT(); a.type = 1; a.ki.vk = vk;
        if (up) a.ki.flags = 0x0002;
        return a;
    }

    // 组合键：先按下所有修饰键，再按主键，然后反向释放
    public static void Hotkey(string[] keys)
    {
        var mods = new List<ushort>(); ushort main = 0;
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            string n = k.Trim().ToUpperInvariant();
            if (n == "CTRL" || n == "SHIFT" || n == "ALT" || n == "WIN" || n == "LWIN") mods.Add(VkOf(n));
            else main = VkOf(n);
        }
        var list = new List<INPUT>();
        foreach (var m in mods) list.Add(KeyInput(m, false));
        if (main != 0) { list.Add(KeyInput(main, false)); list.Add(KeyInput(main, true)); }
        for (int i = mods.Count - 1; i >= 0; i--) list.Add(KeyInput(mods[i], true));
        if (list.Count > 0) Send(list.ToArray());
    }

    // 激活窗口（按 hwnd；含还原最小化、AttachThreadInput 绕过前台锁）
    public static bool FocusWindow(long hwnd)
    {
        var h = new IntPtr(hwnd);
        if (h == IntPtr.Zero) return false;
        if (IsIconic(h)) ShowWindow(h, 9);
        uint fgPid; IntPtr fg = GetForegroundWindow();
        uint fgTid = GetWindowThreadProcessId(fg, out fgPid);
        uint curTid = GetCurrentThreadId();
        bool attached = false;
        if (fgTid != 0 && fgTid != curTid) attached = AttachThreadInput(curTid, fgTid, true);
        bool ok = SetForegroundWindow(h);
        if (!ok)
        {
            // 标准解锁技巧：模拟一次 Alt 空按后重试（前台锁允许）
            try { keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 0x0002, UIntPtr.Zero); } catch { }
            ok = SetForegroundWindow(h);
        }
        if (attached) AttachThreadInput(curTid, fgTid, false);
        if (!ok) { BringWindowToTop(h); ok = SetForegroundWindow(h); }
        if (!ok) { try { SwitchToThisWindow(h, false); } catch { } }
        return ok;
    }

    public static long FindWindowByTitle(string titlePart)
    {
        if (string.IsNullOrEmpty(titlePart)) return 0;
        long found = 0;
        EnumWindows((h, p) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            if (sb.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0) { found = h.ToInt64(); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ===== 剪贴板（CF_UNICODETEXT） =====
    public static string ClipboardGet()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr h = GetClipboardData(13);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static bool ClipboardSet(string text)
    {
        if (text == null) text = "";
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            IntPtr h = GlobalAlloc(0x0042, (UIntPtr)bytes); // GMEM_MOVEABLE | GMEM_ZEROINIT
            if (h == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return false; }
            try
            {
                for (int i = 0; i < text.Length; i++) Marshal.WriteInt16(p, i * 2, (short)text[i]);
                Marshal.WriteInt16(p, text.Length * 2, 0);
            }
            finally { GlobalUnlock(h); }
            return SetClipboardData(13, h) != IntPtr.Zero;
        }
        finally { CloseClipboard(); }
    }
}

// ============ OCR（PaddleOCR PP-OCRv4 det+rec 委托） ============
static class Ocr
{
    public static readonly string Lang = "zh-Hans-CN";
    public static int StripeCount(int h) => 1;   // 兼容旧接口；PaddleOCR 不再条带化

    public static Dictionary<string, object> Run(byte[] buf, int w, int h, double scale, int maxBoxes = 0)
        => PaddleOcr.Run(buf, w, h, scale, maxBoxes);
}

// ============ 图片文件 OCR（从文件夹数据图片提取文本数据） ============
static class ImageFile
{
    public static (byte[] buf, int w, int h) Load(string path)
    {
        if (!System.IO.File.Exists(path)) throw new Exception("file not found: " + path);
        using var bmp = new Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        var buf = new byte[w * h * 4];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            if (stride == w * 4) Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            else
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), buf, y * w * 4, w * 4);
            }
        }
        finally { bmp.UnlockBits(data); }
        return (buf, w, h);
    }

    public static Dictionary<string, object> Ocr(string path, double scale)
    {
        var (buf, w, h) = Load(path);
        var res = PaddleOcr.Run(buf, w, h, scale);
        res["file"] = path;
        res["imageW"] = w;
        res["imageH"] = h;
        return res;
    }
}

// ============ UIA（FlaUI.UIA3，STA 线程） ============
static class Uia
{
    const int MAX_NODES = 800;
    const int MAX_DEPTH = 16;

    public static List<Dictionary<string, object>> Run(long hwnd)
    {
        List<Dictionary<string, object>> result = null;
        Exception err = null;
        var t = new Thread(() =>
        {
            try { result = RunCore(new IntPtr(hwnd)); }
            catch (Exception e) { err = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(30000)) throw new Exception("UIA_TIMEOUT");
        if (err != null) throw err;
        return result;
    }

    static List<Dictionary<string, object>> RunCore(IntPtr hwnd)
    {
        var items = new List<Dictionary<string, object>>();
        int visited = 0;
        var errors = new List<string>();
        using var auto = new FlaUI.UIA3.UIA3Automation();
        var root = hwnd != IntPtr.Zero
            ? auto.FromHandle(hwnd)
            : auto.FromHandle(Win.GetForegroundWindow());
        if (root == null) { items.Add(new Dictionary<string, object> { { "source", "uia" }, { "text", "NO_ROOT" }, { "role", "debug" }, { "rect", new Dictionary<string, object> { { "x", 0 }, { "y", 0 }, { "w", 0 }, { "h", 0 } } } }); return items; }
        var walker = auto.TreeWalkerFactory.GetControlViewWalker();

        void Dfs(FlaUI.Core.AutomationElements.AutomationElement el, int depth)
        {
            if (items.Count >= MAX_NODES || depth > MAX_DEPTH) return;
            visited++;
            string name = null, type = null;
            int x = 0, y = 0, w = 0, h = 0;
            bool offscreen = false;
            try
            {
                name = el.Properties.Name.ValueOrDefault?.ToString();
                type = el.Properties.ControlType.ValueOrDefault.ToString();
                if (string.IsNullOrEmpty(type)) type = "Unknown";
                var r = el.Properties.BoundingRectangle.ValueOrDefault;
                if (r.Width > 0 && r.Height > 0) { x = r.Left; y = r.Top; w = r.Width; h = r.Height; }
                offscreen = el.Properties.IsOffscreen.ValueOrDefault;
            }
            catch (Exception ex)
            {
                if (errors.Count < 3) errors.Add(ex.GetType().Name + ": " + ex.Message);
                return;
            }
            if (offscreen) return;

            bool interesting = !string.IsNullOrWhiteSpace(name)
                || type is "Button" or "Edit" or "Hyperlink" or "CheckBox" or "RadioButton"
                or "ComboBox" or "MenuItem" or "TabItem" or "ListItem" or "TreeItem";
            if (interesting && w > 0 && h > 0)
            {
                items.Add(new Dictionary<string, object> {
                    { "source", "uia" },
                    { "text", name ?? "" },
                    { "role", type },
                    { "rect", new Dictionary<string, object> { { "x", x }, { "y", y }, { "w", w }, { "h", h } } },
                    { "hwnd", hwnd.ToInt64() },
                });
            }
            var child = walker.GetFirstChild(el);
            while (child != null && items.Count < MAX_NODES)
            {
                Dfs(child, depth + 1);
                child = walker.GetNextSibling(child);
            }
        }

        Dfs(root, 0);
        items.Insert(0, new Dictionary<string, object> {
            { "source", "uia" }, { "text", "visited=" + visited + " errors=" + string.Join(" | ", errors) },
            { "role", "debug" }, { "rect", new Dictionary<string, object> { { "x", 0 }, { "y", 0 }, { "w", 0 }, { "h", 0 } } },
        });
        return items;
    }
}

// ============ 注意力可视化小窗（WinForms overlay，STA 线程） ============
static class Overlay
{
    public static Form form;
    public static Label bar;
    public static readonly object Sync = new object();
    public static bool hasRect;
    public static Rectangle attRect;
    public static bool hasGaze;
    public static Point gazePt;
    public static string note = "";
    public static byte[] frame;      // 最新采集帧 BGRA（watch 线程写）
    public static int fw, fh;
    public static double fps;        // 采集帧率
    public static int originX = 0, originY = 0;  // 虚拟屏幕原点（多显示器，帧左上角虚拟坐标）
    public static bool IsOpen => form != null;
    static volatile System.Threading.Tasks.TaskCompletionSource<bool> ReadySignal = new System.Threading.Tasks.TaskCompletionSource<bool>();

    // 等待窗口真正显示出来（<3s 超时）；失败返回 false
    public static bool WaitReady(int ms)
    {
        try { return ReadySignal.Task.Wait(ms) && ReadySignal.Task.Result; } catch { return false; }
    }

    public static void Open(int x, int y, int w, int h)
    {
        if (form != null) return;
        ReadySignal = new System.Threading.Tasks.TaskCompletionSource<bool>();
        var shownSignal = new System.Threading.Tasks.TaskCompletionSource<bool>();
        var t = new Thread(() =>
        {
            try
            {
                form = new Form
                {
                    Text = "SA 注意力",
                    TopMost = true,
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(x, y, w, h),
                    BackColor = Color.Black,
                };
                bar = new Label
                {
                    Dock = DockStyle.Bottom,
                    Height = 46,
                    BackColor = Color.FromArgb(210, 0, 0, 0),
                    ForeColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 9f),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 0, 8, 0),
                    Text = "等待画面…",
                };
                form.Controls.Add(bar);
                form.DoubleClick += (s, e) => { try { form.Close(); } catch { } };
                Point dragOff = Point.Empty; bool dragging = false;
                form.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragOff = e.Location; } };
                form.MouseMove += (s, e) => { if (dragging) form.Location = new Point(form.Left + e.X - dragOff.X, form.Top + e.Y - dragOff.Y); };
                form.MouseUp += (s, e) => dragging = false;
                form.Paint += (s, e) => PaintFrame(e.Graphics, form.ClientSize);
                // 每 100ms 重绘 + 每 tick 强化置顶到最高层
                var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (s, e) =>
                {
                    try { lock (Sync) { form.Text = note.Length > 0 ? "SA 注意力 - " + note : "SA 注意力"; } Win.RaiseTopmost(form.Handle); form.Invalidate(); } catch { }
                };
                timer.Start();
                form.FormClosed += (s, e) => { try { timer.Stop(); } catch { } };
                form.Shown += (s, e) =>
                {
                    try
                    {
                        Win.RaiseTopmost(form.Handle);
                        bool ok = form != null && form.IsHandleCreated && !form.IsDisposed;
                        shownSignal.TrySetResult(ok);
                    }
                    catch { shownSignal.TrySetResult(false); }
                };
                Application.Run(form);
            }
            catch { shownSignal.TrySetResult(false); }
            form = null; bar = null;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        // 等待窗口显示信号（最多 3 秒），把结果记入 ReadySignal
        try
        {
            bool ok = shownSignal.Task.Wait(3000);
            ReadySignal.TrySetResult(ok && shownSignal.Task.Result);
        }
        catch { ReadySignal.TrySetResult(false); }
        // 小窗画面由 watch 引擎供帧（单一采集引擎，插件编排保证 watch 已启动）
    }

    public static void Close()
    {
        var f = form;
        if (f == null) return;
        try { f.Invoke(new Action(() => { try { f.Close(); } catch { } })); } catch { }
        form = null; bar = null;
    }

    // UI 线程：绘制最新帧 + 注意力标注 + 底部信息条
    static void PaintFrame(Graphics g, Size client)
    {
        lock (Sync)
        {
            if (frame != null && fw > 0 && fh > 0)
            {
                using var bmp = BgraToBitmap(frame, fw, fh);
                int barH = bar != null ? bar.Height : 0;
                int vw = client.Width, vh = Math.Max(1, client.Height - barH);
                double scale = Math.Min((double)vw / fw, (double)vh / fh);
                int dw = (int)(fw * scale), dh = (int)(fh * scale);
                int dx = (vw - dw) / 2, dy = (vh - dh) / 2;
                g.DrawImage(bmp, dx, dy, dw, dh);
                if (hasRect)
                {
                    using var pen = new Pen(Color.Red, 2);
                    int rx = dx + (int)((attRect.X - originX) * scale), ry = dy + (int)((attRect.Y - originY) * scale);
                    int rw = Math.Max(2, (int)(attRect.Width * scale)), rh = Math.Max(2, (int)(attRect.Height * scale));
                    g.DrawRectangle(pen, rx, ry, rw, rh);
                }
                if (hasGaze)
                {
                    using var pen = new Pen(Color.Lime, 2);
                    int gx = dx + (int)((gazePt.X - originX) * scale), gy = dy + (int)((gazePt.Y - originY) * scale);
                    g.DrawLine(pen, gx - 8, gy, gx + 8, gy);
                    g.DrawLine(pen, gx, gy - 8, gx, gy + 8);
                }
            }
            if (bar != null)
            {
                var sb = new StringBuilder();
                sb.Append(note.Length > 0 ? "注意: " + note : "空闲");
                if (hasRect) sb.Append("  |  区域 " + attRect.X + "," + attRect.Y + " " + attRect.Width + "x" + attRect.Height);
                if (hasGaze) sb.Append("  |  注视 " + gazePt.X + "," + gazePt.Y);
                sb.Append("  |  观察 " + fps.ToString("0.0") + "fps");
                bar.Text = sb.ToString();
            }
        }
    }

    static Bitmap BgraToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        bmp.UnlockBits(data);
        return bmp;
    }
}

// ============ 持续采集线程（AI 控频 + 变化检测 + 自动调速） ============
// ============ 单一采集流（FrameHub）：Watch/Interpret 共享一次采集 ============
static class FrameHub
{
    static Thread thread;
    static volatile bool running;
    static int intervalMs = 100;
    static readonly object L = new object();
    static byte[] buf; static int bw, bh;
    static long seq, tsTicks;
    static long frames, totalCaptureMs;
    static int lastCaptureMs;
    static double fps;
    static int refs;

    public static bool Running => running;
    public static int Width { get { lock (L) return bw; } }
    public static int Height { get { lock (L) return bh; } }
    public static long Seq { get { lock (L) return seq; } }
    public static int LastCaptureMs { get { lock (L) return lastCaptureMs; } }
    public static double Fps { get { lock (L) return fps; } }
    public static long Frames { get { lock (L) return frames; } }
    public static double AvgCaptureMs { get { lock (L) return frames > 0 ? (double)totalCaptureMs / frames : 0; } }

    // 引用计数：无人订阅时停止采集，避免空转
    public static void Acquire()
    {
        lock (L)
        {
            refs++;
            if (refs == 1 && !running)
            {
                running = true;
                thread = new Thread(Loop) { IsBackground = true };
                thread.Start();
            }
        }
    }
    public static void Release()
    {
        lock (L)
        {
            refs = Math.Max(0, refs - 1);
            if (refs == 0) running = false;
        }
    }

    // 等待比 lastSeen 更新的帧；超时返回 null
    public static byte[] WaitNext(long lastSeen, int timeoutMs, out int w, out int h, out long s, out long ts)
    {
        lock (L)
        {
            long deadline = Environment.TickCount64 + Math.Max(1, timeoutMs);
            while (running && seq == lastSeen)
            {
                long remain = deadline - Environment.TickCount64;
                if (remain <= 0) break;
                System.Threading.Monitor.Wait(L, (int)Math.Min(remain, 100));
            }
            w = bw; h = bh; s = seq; ts = tsTicks;
            return seq == lastSeen ? null : buf;
        }
    }

    static void Loop()
    {
        int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
        long fpsTs = Environment.TickCount64, fpsFrames = 0;
        while (running)
        {
            var t0 = Stopwatch.StartNew();
            try
            {
                var (b, w, h) = Gdi.Capture(0, 0, sw, sh);
                int ms = (int)t0.ElapsedMilliseconds;
                lock (L)
                {
                    buf = b; bw = w; bh = h; seq++; tsTicks = DateTime.UtcNow.Ticks;
                    frames++; totalCaptureMs += ms; lastCaptureMs = ms;
                    System.Threading.Monitor.PulseAll(L);
                    long now = Environment.TickCount64;
                    if (now - fpsTs >= 1000)
                    {
                        fps = (frames - fpsFrames) * 1000.0 / Math.Max(1, now - fpsTs);
                        fpsTs = now; fpsFrames = frames;
                    }
                }
            }
            catch { }
            int sleep = intervalMs - (int)t0.ElapsedMilliseconds;
            if (sleep > 0) Thread.Sleep(sleep);
        }
    }
}

static class Watch
{
    static Thread thread;
    static volatile bool watching;
    static readonly object SyncLock = new object();
    public static bool IsWatching => watching;
    public static int IntervalMs = 1000;
    static readonly int FastMs = 100;
    static volatile bool motion;
    static double motionRatio;
    static Rectangle motionRect;
    static long motionSince;
    static bool everDetected;
    static long lastChangeEventAt;
    static Rectangle focusRect = Rectangle.Empty;   // 焦点区域（空=全屏）。用于"感知对话输出"式定向观察
    static string lastOcrText = "";                 // 上次 OCR 摘要（增量 diff 基准）
    static readonly int[] LastSamples = new int[(3840 / 16 + 1) * (2160 / 16 + 1)];
    static readonly bool[] SampleValid = new bool[(3840 / 16 + 1) * (2160 / 16 + 1)];

    public static bool Motion => motion;
    public static double MotionRatio => motionRatio;
    public static Rectangle MotionRect => motionRect;

    public static void Start(int intervalMs)
    {
        IntervalMs = Math.Max(100, intervalMs);
        if (watching) return;
        watching = true;
        FrameHub.Acquire();
        thread = new Thread(Loop) { IsBackground = true };
        thread.Start();
    }

    public static void Stop() { watching = false; FrameHub.Release(); }

    // 稀疏网格采样（步长 32px）：当前帧与上一帧逐格比较，统计变化率与包围盒
    static readonly int[] RecentChanges = new int[6];  // 近 6 帧变化点数（滑动窗口）
    static int windowSum;
    static int frameIdx;

    // 返回本帧变化点数；运动判定在 Loop 里用窗口累计 + 单帧比例
    static int DetectMotion(byte[] buf, int w, int h)
    {
        const int step = 16;
        const int chTh = 20;             // 通道差阈值
        const double ratioTh = 0.0008;   // 变化格比例阈值（0.08%）
        int cols = (w + step - 1) / step, rows = (h + step - 1) / step;
        int changes = 0, total = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int gy = 0; gy < rows; gy++)
        {
            int y = gy * step;
            if (y >= h) break;
            for (int gx = 0; gx < cols; gx++)
            {
                int x = gx * step;
                if (x >= w) break;
                int idx = gy * cols + gx;
                if (idx >= LastSamples.Length) continue;
                int off = (y * w + x) * 4;
                int b = buf[off], g = buf[off + 1], r = buf[off + 2];
                int cur = (r << 16) | (g << 8) | b;
                if (SampleValid[idx])
                {
                    int pr = (LastSamples[idx] >> 16) & 0xFF, pg = (LastSamples[idx] >> 8) & 0xFF, pb = LastSamples[idx] & 0xFF;
                    if (Math.Abs(r - pr) > chTh || Math.Abs(g - pg) > chTh || Math.Abs(b - pb) > chTh)
                    {
                        changes++;
                        if (x < minX) minX = x; if (y < minY) minY = y;
                        if (x > maxX) maxX = x; if (y > maxY) maxY = y;
                    }
                }
                LastSamples[idx] = cur;
                SampleValid[idx] = true;
                total++;
            }
        }
        double ratio = total > 0 ? (double)changes / total : 0;
        lock (SyncLock)
        {
            motionRatio = ratio;
            // 滑动窗口：近 6 帧累计变化点数（细粒度运动：滚动文字/动画小变化也能感知）
            windowSum -= RecentChanges[frameIdx];
            RecentChanges[frameIdx] = changes;
            windowSum += changes;
            frameIdx = (frameIdx + 1) % RecentChanges.Length;
            bool m = ratio > ratioTh || windowSum >= 12;
            if (m)
            {
                motionRect = new Rectangle(Math.Max(0, minX), Math.Max(0, minY), Math.Max(1, maxX - minX + step), Math.Max(1, maxY - minY + step));
                if (!motion) motionSince = DateTime.UtcNow.Ticks;
                motion = true;
            }
            else
            {
                if (motion) { motion = false; motionRect = Rectangle.Empty; }
            }
            // 变化事件：motion 翻转 或 持续变化每 3s（抑制刷屏）
            long now = DateTime.UtcNow.Ticks;
            if (m && ((now - lastChangeEventAt) / (long)TimeSpan.TicksPerSecond >= 3 || !everDetected))
            {
                lastChangeEventAt = now;
                everDetected = true;
                try { Program.Event("change", new { ratio = Math.Round(ratio, 4), rect = new { x = motionRect.X, y = motionRect.Y, w = motionRect.Width, h = motionRect.Height } }); } catch { }
            }
        }
        return changes;
    }

    static void Loop()
    {
        long lastSeen = 0;
        long lastTick = 0;
        int steady = 0;   // 连续静止帧计数（降速迟滞）
        int active = 0;   // 连续运动帧计数（升速迟滞）
        while (watching)
        {
            var t0 = Stopwatch.StartNew();
            try
            {
                var buf = FrameHub.WaitNext(lastSeen, 250, out int w, out int h, out long s, out _);
                if (buf == null) { continue; }
                lastSeen = s;
                lock (Overlay.Sync)
                {
                    Overlay.frame = buf;
                    Overlay.fw = w; Overlay.fh = h;
                    long now = DateTime.UtcNow.Ticks;
                    if (lastTick > 0)
                    {
                        double gapMs = (now - lastTick) / (double)TimeSpan.TicksPerMillisecond;
                        Overlay.fps = 1000.0 / Math.Max(1, gapMs);
                    }
                    lastTick = now;
                }
                bool before = motion;
                DetectMotion(buf, w, h);
                // 迟滞调速：连续运动→快模式；连续静止→慢模式
                if (motion) { active++; steady = 0; } else { steady++; active = 0; }
                int curMs = FastMs;
                if (before == motion) curMs = motion ? FastMs : IntervalMs;   // 状态未翻转时按当前状态
                if (motion && active >= 3) curMs = FastMs;
                else if (!motion && steady >= 6) curMs = IntervalMs;
                else if (!motion) curMs = IntervalMs;
                int sleep = curMs - (int)t0.ElapsedMilliseconds;
                Thread.Sleep(Math.Max(10, sleep));
            }
            catch { Thread.Sleep(20); }
        }
    }

    public static string DiffText(string prev, string cur) {
        if (string.IsNullOrEmpty(prev)) return cur;
        if (string.IsNullOrEmpty(cur)) return "";
        if (cur.StartsWith(prev)) return cur.Substring(prev.Length);
        if (prev.StartsWith(cur)) return "";
        int p = 0; while (p < prev.Length && p < cur.Length && prev[p] == cur[p]) p++;
        int s = 0; while (s < prev.Length - p && s < cur.Length - p && prev[prev.Length - 1 - s] == cur[cur.Length - 1 - s]) s++;
        string mid = cur.Substring(p, cur.Length - p - s);
        return mid.Length > 0 ? mid : cur.Substring(Math.Max(0, cur.Length - 200));
    }

    public static void SetFocus(Rectangle rect) { lock (SyncLock) { focusRect = rect; lastOcrText = ""; } }
    public static Rectangle FocusRect { get { lock (SyncLock) return focusRect; } }

    public static object Snapshot()
    {
        lock (SyncLock)
        {
            int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
            // 焦点模式：即使当前帧无"新运动"，也对焦点区做 OCR（获得当前文本快照）
            var r = motionRect;
            bool useFocus = !focusRect.IsEmpty;
            Rectangle ocrRect = useFocus ? focusRect : (motion && !r.IsEmpty ? r : Rectangle.Empty);
            if (ocrRect.IsEmpty)
                return new { motion = motion, ratio = Math.Round(motionRatio, 4), text = "", chars = 0, normalized = false };
            try
            {
                int x = Math.Max(0, ocrRect.X - 8), y = Math.Max(0, ocrRect.Y - 8);
                int w = Math.Min(sw - x, ocrRect.Width + 16), h = Math.Min(sh - y, ocrRect.Height + 16);
                var (buf, fw, fh) = Gdi.Capture(x, y, w, h);
                var res = Ocr.Run(buf, fw, fh, 2.0);
                var items = (res.TryGetValue("items", out var iv) && iv is List<Dictionary<string, object>> list) ? list : new List<Dictionary<string, object>>();
                var texts = items.Select(t => t.TryGetValue("text", out var tv) ? Convert.ToString(tv) : "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                string joined = string.Join(" | ", texts);
                if (joined.Length > 1200) joined = joined.Substring(0, 1200) + "…";
                string added = DiffText(lastOcrText, joined);
                bool changed = added != joined || added.Length > 0;
                lastOcrText = joined;
                int chars = 0; foreach (var t in texts) chars += t.Length;
                return new { motion = motion, ratio = Math.Round(motionRatio, 4), rect = new { x = ocrRect.X, y = ocrRect.Y, w = ocrRect.Width, h = ocrRect.Height }, text = joined, added, chars, changed, ms = (int)(res.TryGetValue("ms", out var mv) ? Convert.ToDouble(mv) : 0) };
            }
            catch (Exception e) { return new { motion = motion, ratio = Math.Round(motionRatio, 4), text = "(ocr 失败: " + e.Message + ")", chars = 0, changed = false, ms = 0 }; }
        }
    }


}
// ============ 连续解读（持续获取 + 变化区域 OCR + 内容/噪声分类） ============
static class Interpret
{
    static Thread thread;
    static volatile bool running;
    static int intervalMs = 800;
    static Rectangle region = Rectangle.Empty;      // 空=自动检测变化区域
    static readonly object L = new object();
    static byte[] prevBuf; static int pw, ph;
    static string lastText = "";
    static long ticks, ocrRuns, totalOcrMs;
    static string lastKind = "idle", lastAdded = "", lastError = "", lastTextNow = "";
    static int lastDiffChanges;
    static long lastDiffMs, lastCaptureMs;
    static readonly List<long> ocrSamples = new List<long>();
    static readonly List<Dictionary<string, object>> history = new List<Dictionary<string, object>>();

    public static bool Running => running;

    public static void Start(int ms, Rectangle? r)
    {
        intervalMs = Math.Max(100, ms);
        if (running) { lock (L) region = r ?? Rectangle.Empty; return; }
        lock (L)
        {
            region = r ?? Rectangle.Empty; history.Clear(); lastText = ""; lastKind = "idle";
            lastAdded = ""; lastError = ""; lastTextNow = "";
            ticks = 0; ocrRuns = 0; totalOcrMs = 0; prevBuf = null;
        }
        running = true;
        FrameHub.Acquire();
        thread = new Thread(Loop) { IsBackground = true };
        thread.Start();
    }

    public static void Stop() { running = false; FrameHub.Release(); }

    static long Percentile(int p)
    {
        lock (L)
        {
            if (ocrSamples.Count == 0) return 0;
            var arr = ocrSamples.ToArray();
            Array.Sort(arr);
            int idx = (int)Math.Ceiling(p / 100.0 * arr.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= arr.Length) idx = arr.Length - 1;
            return arr[idx];
        }
    }

    public static object Status(int limit)
    {
        lock (L)
        {
            int n = Math.Min(limit, history.Count);
            var hist = history.GetRange(history.Count - n, n);
            return new Dictionary<string, object> {
                { "running", running }, { "intervalMs", intervalMs },
                { "ticks", ticks }, { "ocrRuns", ocrRuns },
                { "avgOcrMs", ocrRuns > 0 ? Math.Round((double)totalOcrMs / ocrRuns, 1) : 0 },
                { "kind", lastKind }, { "added", lastAdded }, { "diffChanges", lastDiffChanges },
                { "fps", Math.Round(FrameHub.Fps, 1) },
                { "captureMs", lastCaptureMs > 0 ? lastCaptureMs : FrameHub.LastCaptureMs },
                { "avgCaptureMs", Math.Round(FrameHub.AvgCaptureMs, 1) },
                { "diffMs", lastDiffMs },
                { "ocrP50Ms", Percentile(50) }, { "ocrP90Ms", Percentile(90) },
                { "textLen", lastTextNow.Length },
                { "text", lastTextNow.Length > 600 ? lastTextNow.Substring(0, 600) : lastTextNow },
                { "error", lastError }, { "history", hist },
            };
        }
    }

    // 粗粒度帧差（步长 32px）：返回变化包围盒；无变化返回 Empty
    static Rectangle DiffRect(byte[] buf, int w, int h)
    {
        const int step = 32; const int chTh = 24;
        if (prevBuf == null || pw != w || ph != h) { prevBuf = (byte[])buf.Clone(); pw = w; ph = h; return Rectangle.Empty; }
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, changes = 0;
        for (int y = 0; y + step <= h; y += step)
            for (int x = 0; x + step <= w; x += step)
            {
                int off = (y * w + x) * 4;
                if (Math.Abs(buf[off] - prevBuf[off]) > chTh || Math.Abs(buf[off + 1] - prevBuf[off + 1]) > chTh || Math.Abs(buf[off + 2] - prevBuf[off + 2]) > chTh)
                {
                    changes++;
                    if (x < minX) minX = x; if (y < minY) minY = y;
                    if (x > maxX) maxX = x; if (y > maxY) maxY = y;
                }
            }
        System.Buffer.BlockCopy(buf, 0, prevBuf, 0, buf.Length);
        lastDiffChanges = changes;
        if (changes == 0) return Rectangle.Empty;
        return new Rectangle(Math.Max(0, minX), Math.Max(0, minY), Math.Max(1, maxX - minX + step), Math.Max(1, maxY - minY + step));
    }

    static Rectangle Clamp(Rectangle r, int sw, int sh)
    {
        int x = Math.Max(0, Math.Min(r.X, sw - 1)), y = Math.Max(0, Math.Min(r.Y, sh - 1));
        int w = Math.Max(1, Math.Min(r.Width, sw - x)), h = Math.Max(1, Math.Min(r.Height, sh - y));
        return new Rectangle(x, y, w, h);
    }

    static void Loop()
    {
        long lastSeen = 0;
        int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
        while (running)
        {
            var t0 = Stopwatch.StartNew();
            try
            {
                var frame = FrameHub.WaitNext(lastSeen, 250, out int w, out int h, out long s, out _);
                if (frame == null) { continue; }
                lastSeen = s;
                var d0 = Stopwatch.StartNew();
                Rectangle rect;
                lock (L) rect = region;
                if (!rect.IsEmpty) rect = Clamp(rect, sw, sh);
                else
                {
                    var dr = DiffRect(frame, w, h);
                    if (dr.IsEmpty) { lock (L) lastKind = "idle"; }
                    rect = dr;
                }
                long diffMs = d0.ElapsedMilliseconds;
                if (!rect.IsEmpty)
                {
                    int pad = 12;
                    int x = Math.Max(0, rect.X - pad), y = Math.Max(0, rect.Y - pad);
                    int cw = Math.Min(sw - x, rect.Width + pad * 2), ch = Math.Min(sh - y, rect.Height + pad * 2);
                    var (rb, rw, rh) = Gdi.Capture(x, y, cw, ch);
                    // 区域限幅：大区域降 scale + 限制识别框数（控制单次 OCR 预算）
                    long area = (long)cw * ch;
                    double scale = 1.5;
                    int maxBoxes = 0;
                    if (area > 700000) { scale = 1.0; maxBoxes = 60; }
                    else if (area > 350000) { scale = 1.2; maxBoxes = 120; }
                    var res = Ocr.Run(rb, rw, rh, scale, maxBoxes);
                    var items = (res.TryGetValue("items", out var iv) && iv is List<Dictionary<string, object>> list) ? list : new List<Dictionary<string, object>>();
                    var texts = items.Select(t => t.TryGetValue("text", out var tv) ? Convert.ToString(tv) : "").Where(t => !string.IsNullOrEmpty(t)).ToList();
                    string joined = string.Join(" | ", texts);
                    if (joined.Length > 1200) joined = joined.Substring(0, 1200);
                    string added = joined == lastText ? "" : Watch.DiffText(lastText, joined);
                    string kind = joined == lastText ? "dynamic_noise" : (added.Length > 0 ? "content_change" : "text_shift");
                    double ocrMs = res.TryGetValue("ms", out var mv) ? Convert.ToDouble(mv) : 0;
                    lock (L)
                    {
                        lastText = joined; lastTextNow = joined; lastAdded = added; lastKind = kind;
                        ocrRuns++; totalOcrMs += (long)ocrMs;
                        lastDiffMs = diffMs; lastCaptureMs = FrameHub.LastCaptureMs;
                        ocrSamples.Add((long)ocrMs);
                        if (ocrSamples.Count > 200) ocrSamples.RemoveAt(0);
                        history.Add(new Dictionary<string, object> {
                            { "t", DateTime.Now.ToString("HH:mm:ss.fff") }, { "kind", kind },
                            { "added", added.Length > 200 ? added.Substring(0, 200) : added },
                            { "textLen", joined.Length }, { "ocrMs", Math.Round(ocrMs, 0) },
                            { "diffMs", diffMs }, { "boxes", items.Count },
                            { "rect", new { x, y, w = cw, h = ch } },
                        });
                        if (history.Count > 300) history.RemoveAt(0);
                    }
                }
                else { lock (L) { lastDiffMs = diffMs; lastCaptureMs = FrameHub.LastCaptureMs; } }
                ticks++;
            }
            catch (Exception e) { lock (L) lastError = e.Message; }
            int sleep = intervalMs - (int)t0.ElapsedMilliseconds;
            Thread.Sleep(Math.Max(20, sleep));
        }
    }
}

// ============ 实时逐帧识别（Infer）：每一帧都送识别，结果流式输出 ============
static class Infer
{
    static Thread thread;
    static volatile bool running;
    static int maxBoxes = 60;
    static bool objects;
    static bool sceneMode;
    static bool accurateDetect;
    static bool facesMode;
    static int sceneEvery = 8, faceEvery = 4;
    static long frameNo;
    static readonly object L = new object();
    static string latestText = "", latestErr = "";
    static long latestSeq; static int latestOcrMs, latestBoxes, latestHits, latestMiss;
    static int latestObjMs, latestObjCount;
    static string latestScene; static int latestSceneMs; static List<Dictionary<string, object>> latestSceneLabels;
    static List<Dictionary<string, object>> latestFaces = new List<Dictionary<string, object>>();
    static int latestFaceMs, latestFaceCount;
    static string latestUnderstanding = "", latestFgTitle = "", prevFgTitle = "";
    static List<Dictionary<string, object>> latestObjects = new List<Dictionary<string, object>>();
    static long seen, processed, dropped, staticSkipped, totalOcrMs;
    static readonly List<long> samples = new List<long>();
    static readonly List<Dictionary<string, object>> history = new List<Dictionary<string, object>>();
    static double fps;

    sealed class Track
    {
        public int Id; public string Label = "", LabelZh = "", Color = "", Identity = "";
        public float Conf; public double X, Y, W, H; public int Miss; public bool Matched;
    }
    static readonly List<Track> tracks = new List<Track>();
    static int nextTrackId = 1;
    static List<string> latestChanges = new List<string>();
    static readonly List<string> changeLog = new List<string>();
    static byte[] prevInferBuf;
    static int piw, pih;
    static int staticCount = 0;
    static int lastChanges = 0;
    static bool isMotionActive = true;

    static int DetectMotion(byte[] buf, int w, int h)
    {
        const int step = 32; const int chTh = 24;
        if (prevInferBuf == null || piw != w || pih != h || prevInferBuf.Length != buf.Length)
        {
            prevInferBuf = (byte[])buf.Clone();
            piw = w; pih = h;
            return 999;
        }
        int changes = 0;
        for (int y = 0; y + step <= h; y += step)
        {
            for (int x = 0; x + step <= w; x += step)
            {
                int off = (y * w + x) * 4;
                if (Math.Abs(buf[off] - prevInferBuf[off]) > chTh ||
                    Math.Abs(buf[off + 1] - prevInferBuf[off + 1]) > chTh ||
                    Math.Abs(buf[off + 2] - prevInferBuf[off + 2]) > chTh)
                {
                    changes++;
                }
            }
        }
        System.Buffer.BlockCopy(buf, 0, prevInferBuf, 0, buf.Length);
        return changes;
    }

    public static bool Running => running;

    public static void Start(int boxes, bool withObjects, bool withScene, bool accurate, bool withFaces = false, int sceneEveryIn = 8, int faceEveryIn = 4)
    {
        lock (L)
        {
            maxBoxes = boxes > 0 ? boxes : 60;
            sceneEvery = sceneEveryIn > 0 ? sceneEveryIn : 8;
            faceEvery = faceEveryIn > 0 ? faceEveryIn : 4;
            objects = withObjects;
            sceneMode = withScene;
            accurateDetect = accurate;
            facesMode = withFaces;
            if (!running)
            {
                frameNo = 0;
                latestScene = null; latestSceneMs = 0; latestSceneLabels = null;
                latestFaces = new List<Dictionary<string, object>>();
                latestFaceMs = 0; latestFaceCount = 0; latestUnderstanding = ""; latestFgTitle = ""; prevFgTitle = "";
                history.Clear(); samples.Clear();
                seen = processed = dropped = staticSkipped = totalOcrMs = 0;
                latestText = ""; latestErr = ""; fps = 0;
                changeLog.Clear(); tracks.Clear(); nextTrackId = 1; latestChanges = new List<string>();
                prevInferBuf = null; piw = pih = 0; staticCount = 0; lastChanges = 0; isMotionActive = true;
            }
        }
        if (withScene || withObjects) ClipScene.WarmupAsync();
        if (running) return;
        running = true;
        FrameHub.Acquire();
        thread = new Thread(Loop) { IsBackground = true };
        thread.Start();
    }

    public static void Stop() { running = false; FrameHub.Release(); }

    static double IouDet(System.Collections.Generic.Dictionary<string, object> d, Track t)
    {
        double x1 = Convert.ToDouble(d["x"]), y1 = Convert.ToDouble(d["y"]);
        double w1 = Convert.ToDouble(d["w"]), h1 = Convert.ToDouble(d["h"]);
        double ix1 = Math.Max(x1, t.X), iy1 = Math.Max(y1, t.Y);
        double ix2 = Math.Min(x1 + w1, t.X + t.W), iy2 = Math.Min(y1 + h1, t.Y + t.H);
        double iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        double inter = iw * ih;
        double ua = w1 * h1 + t.W * t.H - inter;
        return ua <= 0 ? 0 : inter / ua;
    }

    static (List<Dictionary<string, object>>, List<string>) UpdateTracks(List<Dictionary<string, object>> dets, int fw, int fh)
    {
        var changes = new List<string>();
        long frameArea = Math.Max(1, (long)fw * fh);
        foreach (var t in tracks) t.Matched = false;
        var outList = new List<Dictionary<string, object>>();
        foreach (var d in dets)
        {
            double area = Convert.ToDouble(d["w"]) * Convert.ToDouble(d["h"]);
            if (area > frameArea * 0.70) continue;   // UI 大窗口误报过滤
            string label = Convert.ToString(d["label"]);
            int best = -1; double bestIou = 0;
            for (int j = 0; j < tracks.Count; j++)
            {
                var t = tracks[j];
                if (t.Miss > 3 || t.Label != label) continue;
                double iou = IouDet(d, t);
                if (iou > bestIou) { bestIou = iou; best = j; }
            }
            if (best >= 0 && bestIou > 0.40)
            {
                var t = tracks[best];
                double dx = Convert.ToDouble(d["x"]) - t.X, dy = Convert.ToDouble(d["y"]) - t.Y;
                if (Math.Abs(dx) > 10 || Math.Abs(dy) > 10) changes.Add((t.Identity != "" ? t.Identity : t.LabelZh) + "#" + t.Id + " 移动");
                t.Conf = (float)Convert.ToDouble(d["conf"]);
                t.X = Convert.ToDouble(d["x"]); t.Y = Convert.ToDouble(d["y"]);
                t.W = Convert.ToDouble(d["w"]); t.H = Convert.ToDouble(d["h"]);
                t.Color = Convert.ToString(d["color"]); t.Miss = 0; t.Matched = true;
                t.Identity = d.ContainsKey("identity") ? Convert.ToString(d["identity"]) : (d.ContainsKey("unknownId") ? Convert.ToString(d["unknownId"]) : "");
                d["id"] = t.Id;
            }
            else
            {
                var t = new Track {
                    Id = nextTrackId++, Label = label,
                    LabelZh = Convert.ToString(d["labelZh"]), Color = Convert.ToString(d["color"]),
                    Conf = (float)Convert.ToDouble(d["conf"]),
                    X = Convert.ToDouble(d["x"]), Y = Convert.ToDouble(d["y"]),
                    W = Convert.ToDouble(d["w"]), H = Convert.ToDouble(d["h"]),
                    Miss = 0, Matched = true,
                };
                t.Identity = d.ContainsKey("identity") ? Convert.ToString(d["identity"]) : (d.ContainsKey("unknownId") ? Convert.ToString(d["unknownId"]) : "");
                tracks.Add(t);
                changes.Add((t.Identity != "" ? t.Identity : t.LabelZh) + "#" + t.Id + " 出现");
                d["id"] = t.Id;
            }
            outList.Add(d);
        }
        for (int j = tracks.Count - 1; j >= 0; j--)
        {
            var t = tracks[j];
            if (t.Matched) continue;
            t.Miss++;
            if (t.Miss > 2) { changes.Add((t.Identity != "" ? t.Identity : t.LabelZh) + "#" + t.Id + " 消失"); tracks.RemoveAt(j); }
        }
        return (outList, changes);
    }

    static long Pct(int p)
    {
        if (samples.Count == 0) return 0;
        var a = samples.ToArray(); Array.Sort(a);
        int i = (int)Math.Ceiling(p / 100.0 * a.Length) - 1;
        if (i < 0) i = 0; if (i >= a.Length) i = a.Length - 1;
        return a[i];
    }

    public static object Status(int limit)
    {
        lock (L)
        {
            int n = Math.Min(limit, history.Count);
            var hist = history.GetRange(history.Count - n, n);
            return new Dictionary<string, object> {
                { "running", running }, { "fps", Math.Round(fps, 1) }, { "motion", isMotionActive }, { "motionChanges", lastChanges },
                { "framesSeen", seen }, { "framesProcessed", processed }, { "framesDropped", dropped }, { "staticSkipped", staticSkipped },
                { "avgOcrMs", processed > 0 ? Math.Round((double)totalOcrMs / processed, 1) : 0 },
                { "ocrP50Ms", Pct(50) }, { "ocrP90Ms", Pct(90) },
                { "latest", new Dictionary<string, object> {
                    { "seq", latestSeq }, { "ocrMs", latestOcrMs }, { "boxes", latestBoxes },
                    { "recHits", latestHits }, { "recMiss", latestMiss },
                    { "text", latestText.Length > 400 ? latestText.Substring(0, 400) : latestText },
                    { "objects", latestObjects.GetRange(0, Math.Min(latestObjects.Count, 20)) },
                    { "objectCount", latestObjCount }, { "objectMs", latestObjMs },
                    { "changes", latestChanges.ToArray() }, { "tracks", tracks.Count },
                    { "changesRecent", changeLog.GetRange(Math.Max(0, changeLog.Count - 10), Math.Min(10, changeLog.Count)) },
                    { "scene", latestScene }, { "sceneMs", latestSceneMs },
                    { "sceneLabels", latestSceneLabels },
                    { "faces", latestFaces.GetRange(0, Math.Min(latestFaces.Count, 20)) },
                    { "faceCount", latestFaceCount }, { "faceMs", latestFaceMs },
                    { "fgTitle", latestFgTitle },
                    { "understanding", latestUnderstanding } } },
                { "error", latestErr }, { "history", hist },
            };
        }
    }

    static void Loop()
    {
        long lastSeen = 0;
        long fpsTs = Environment.TickCount64, fpsCount = 0;
        while (running)
        {
            var frame = FrameHub.WaitNext(lastSeen, 300, out int w, out int h, out long seq, out _);
            if (frame == null) { lock (L) latestErr = "等待新帧超时（采集已停止？）"; continue; }
            long skipped = seq - lastSeen - 1;
            lastSeen = seq;
            int changes = DetectMotion(frame, w, h);
            bool needInitialScene = sceneMode && string.IsNullOrEmpty(latestScene) && ClipScene.IsReady;
            bool isStatic = changes <= 2 && !needInitialScene;
            lock (L) { lastChanges = changes; isMotionActive = !isStatic; }
            if (isStatic)
            {
                staticCount++;
                // 静止降频：每 10 帧(~1s)跑一次兜底心跳更新；其余静止帧跳过全量推理，节省 DirectML 与算力
                if (staticCount % 10 != 0)
                {
                    lock (L)
                    {
                        seen += skipped + 1;
                        staticSkipped++;
                        latestSeq = seq;
                        long nowSkip = Environment.TickCount64;
                        if (nowSkip - fpsTs >= 1000) { fps = fpsCount * 1000.0 / Math.Max(1, nowSkip - fpsTs); fpsTs = nowSkip; fpsCount = 0; }
                    }
                    continue;
                }
            }
            else
            {
                staticCount = 0;
            }

            var t0 = Stopwatch.StartNew();
            try
            {
                var res = PaddleOcr.RunStreaming(frame, w, h, maxBoxes);
                int ocrMs = (int)t0.ElapsedMilliseconds;
                var items = (res.TryGetValue("items", out var iv) && iv is List<Dictionary<string, object>> list) ? list : new List<Dictionary<string, object>>();
                var texts = items.Select(t => t.TryGetValue("text", out var tv) ? Convert.ToString(tv) : "").Where(t => !string.IsNullOrEmpty(t)).ToList();
                string joined = string.Join(" | ", texts);
                if (joined.Length > 1200) joined = joined.Substring(0, 1200);
                int hits = res.TryGetValue("recHits", out var hv) ? Convert.ToInt32(hv) : 0;
                int miss = res.TryGetValue("recMiss", out var mv2) ? Convert.ToInt32(mv2) : 0;
                frameNo++;
                string sceneTop = null; int sceneMs = 0; List<Dictionary<string, object>> sceneLabels = null;
                bool ranScene = false;
                if (sceneMode && ClipScene.IsReady && (frameNo == 1 || frameNo % sceneEvery == 0 || string.IsNullOrEmpty(latestScene)))
                {
                    try
                    {
                        var scRes = ClipScene.Run(frame, w, h, 5);
                        sceneTop = Convert.ToString(scRes["top"]);
                        sceneMs = scRes.TryGetValue("ms", out var sms) ? Convert.ToInt32(sms) : 0;
                        sceneLabels = scRes.TryGetValue("labels", out var sl) && sl is List<Dictionary<string, object>> sll ? sll : null;
                        ranScene = true;
                    }
                    catch { }
                }
                List<Dictionary<string, object>> faces = null; int faceMs = 0;
                bool ranFaces = false;
                if (facesMode && (frameNo == 1 || frameNo % faceEvery == 0))
                {
                    try
                    {
                        var fr = AnimeFaceDetect.Run(frame, w, h, 0.25f, 20);
                        faces = fr.TryGetValue("faces", out var fv) && fv is List<Dictionary<string, object>> fl ? fl : null;
                        faceMs = fr.TryGetValue("ms", out var fms) ? Convert.ToInt32(fms) : 0;
                        ranFaces = true;
                    }
                    catch { }
                }
                List<Dictionary<string, object>> objItems = null; int objMs = 0;
                if (objects)
                {
                    try
                    {
                        var objRes = YoloDetect.Run(frame, w, h, accurateDetect);
                        objItems = objRes.TryGetValue("items", out var oi) && oi is List<Dictionary<string, object>> ol ? ol : null;
                        objMs = objRes.TryGetValue("ms", out var oms) ? Convert.ToInt32(oms) : 0;
                        if (objItems != null && ClipScene.IsReady && CharId.Names().Count > 0)
                        {
                            foreach (var it in objItems)
                            {
                                if (Convert.ToString(it["label"]) != "person") continue;
                                if (!(Convert.ToDouble(it["conf"]) >= 0.55 && Convert.ToInt32(it["w"]) >= 60 && Convert.ToInt32(it["h"]) >= 90)) continue;
                                try
                                {
                                    var emb = ClipScene.Embed(frame, w, h, Convert.ToInt32(it["x"]), Convert.ToInt32(it["y"]), Convert.ToInt32(it["w"]), Convert.ToInt32(it["h"]));
                                    if (emb == null) continue;
                                    var (nm, sc, uid) = CharId.MatchOrUnknown(emb);
                                    if (nm != null) { it["identity"] = nm; it["identityScore"] = Math.Round(sc, 3); it["identityNote"] = CharId.NoteOf(nm); }
                                    else if (uid != null) { it["unknownId"] = uid; it["unknownScore"] = Math.Round(sc, 3); }
                                }
                                catch { }
                            }
                        }
                        if (objItems != null)
                        {
                            var (tr, ch) = UpdateTracks(objItems, w, h);
                            objItems = tr; latestChanges = ch;
                            if (ch.Count > 0)
                            {
                                lock (L)
                                {
                                    string ts = DateTime.Now.ToString("HH:mm:ss");
                                    foreach (var chg in ch) changeLog.Add(ts + " " + chg);
                                    while (changeLog.Count > 100) changeLog.RemoveAt(0);
                                }
                            }
                        }
                    }
                    catch { }
                }
                lock (L)
                {
                    seen += skipped + 1; processed++; dropped += Math.Max(0, skipped);
                    totalOcrMs += ocrMs;
                    samples.Add(ocrMs); if (samples.Count > 200) samples.RemoveAt(0);
                    latestText = joined; latestSeq = seq; latestOcrMs = ocrMs; latestBoxes = items.Count;
                    latestHits = hits; latestMiss = miss; latestErr = "";
                    if (objItems != null) { latestObjects = objItems; latestObjCount = objItems.Count; latestObjMs = objMs; }
                    if (ranScene) { latestScene = sceneTop; latestSceneMs = sceneMs; latestSceneLabels = sceneLabels; }
                    if (ranFaces) { latestFaces = faces ?? new List<Dictionary<string, object>>(); latestFaceMs = faceMs; latestFaceCount = faces != null ? faces.Count : 0; }
                    // B3 & B4: 前台窗口切换检测 + 综合理解（窗口 + 场景 + 物体(含身份) + 动漫脸 + 文本）
                    string fgNow = Win.ForegroundTitle();
                    if (!string.IsNullOrEmpty(fgNow))
                    {
                        if (!string.IsNullOrEmpty(prevFgTitle) && fgNow != prevFgTitle)
                        {
                            string swMsg = "窗口切换 -> " + fgNow;
                            latestChanges.Add(swMsg);
                            changeLog.Add(DateTime.Now.ToString("HH:mm:ss") + " " + swMsg);
                            while (changeLog.Count > 100) changeLog.RemoveAt(0);
                        }
                        prevFgTitle = fgNow;
                        latestFgTitle = fgNow;
                    }
                    var up = new List<string>();
                    if (!string.IsNullOrEmpty(latestFgTitle)) up.Add("窗口「" + latestFgTitle + "」");
                    if (!string.IsNullOrEmpty(latestScene)) up.Add(latestScene);
                    if (latestObjects.Count > 0)
                    {
                        var objs = latestObjects.GroupBy(o => Convert.ToString(o["labelZh"]))
                            .Select(g => g.Key + "×" + g.Count() + (g.Any(x => x.ContainsKey("identity") || x.ContainsKey("unknownId")) ? "(" + string.Join(",", g.Select(x => x.ContainsKey("identity") ? Convert.ToString(x["identity"]) : (x.ContainsKey("unknownId") ? Convert.ToString(x["unknownId"]) : "")).Where(nm => nm != "")) + ")" : ""))
                            .ToArray();
                        up.Add("物体:" + string.Join(",", objs));
                    }
                    if (latestFaceCount > 0) up.Add("动漫脸×" + latestFaceCount);
                    if (latestText.Length > 0)
                    {
                        string snip = latestText.Length > 40 ? latestText.Substring(0, 40) + "…" : latestText;
                        up.Add("文字「" + snip + "」");
                    }
                    latestUnderstanding = string.Join("；", up);
                    history.Add(new Dictionary<string, object> {
                        { "t", DateTime.Now.ToString("HH:mm:ss.fff") }, { "seq", seq },
                        { "ocrMs", ocrMs }, { "boxes", items.Count }, { "recHits", hits }, { "recMiss", miss },
                        { "textLen", joined.Length }, { "text", joined.Length > 200 ? joined.Substring(0, 200) : joined },
                        { "objCount", objItems != null ? objItems.Count : 0 }, { "objMs", objMs },
                        { "changes", latestChanges.ToArray() },
                        { "scene", latestScene }, { "sceneMs", latestSceneMs },
                        { "faceCount", latestFaceCount }, { "understanding", latestUnderstanding },
                    });
                    if (history.Count > 300) history.RemoveAt(0);
                    fpsCount++;
                    long now = Environment.TickCount64;
                    if (now - fpsTs >= 1000) { fps = fpsCount * 1000.0 / Math.Max(1, now - fpsTs); fpsTs = now; fpsCount = 0; }
                }
            }
            catch (Exception e) { lock (L) latestErr = e.Message; }
        }
    }
}

// ============ main ============
class Program
{
    static bool locked = false;
    static readonly object Wl = new object();
    static readonly object LockObj = new object();
    const int HOTKEY_ID = 1;
    const uint VK_PAUSE = 0x13;
    const int WM_HOTKEY = 0x0312;

    static void WriteLine(string json) { lock (Wl) { Console.Out.WriteLine(json); Console.Out.Flush(); } }
    public static void Event(string name, object data = null)
    {
        WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { { "event", name }, { "data", data ?? new { } } }));
    }

    static void HotkeyThread()
    {
        try
        {
            if (!Win.RegisterHotKey(IntPtr.Zero, HOTKEY_ID, 0, VK_PAUSE)) return;
            Win.MSG msg;
            while (Win.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && msg.wParam == (IntPtr)HOTKEY_ID)
                {
                    lock (LockObj) locked = !locked;
                    Event("hotkey", new { locked });
                }
                Win.TranslateMessage(ref msg);
                Win.DispatchMessage(ref msg);
            }
        }
        catch { }
    }

    static void GuardInject()
    {
        lock (LockObj)
        {
            if (locked) throw new Exception("LOCKED: 注入已锁定（热键或 stop 命令）");
        }
        // 让位语义：用户点击审批"允许"按钮本身是输入活动，注入立即执行必然撞车。
        // 等待用户停止操作（IdleMs >= 阈值）再注入，最多等 5s；持续操作才报 USER_ACTIVE。
        const int minIdle = 300;
        const int timeoutMs = 5000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Win.IdleMs() < minIdle)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new Exception("USER_ACTIVE: 用户持续操作输入设备（等待 " + (timeoutMs / 1000) + "s 超时），注入已让位；稍后重试");
            Thread.Sleep(50);
        }
    }

    static object Run(string cmd, JsonElement req)
    {
        switch (cmd)
        {
            case "handshake":
                {
                    int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
                    return new { version = 1, cores = Environment.ProcessorCount, screen = new { w = sw, h = sh }, ocrLang = Ocr.Lang };
                }
            case "ping": return new { pong = true };
            case "screen_bounds": return new { x = 0, y = 0, w = Gdi.ScreenW(), h = Gdi.ScreenH() };
            case "windows": return new { windows = Win.ListWindows() };
            case "idle": return new { idleMs = Win.IdleMs() };

            case "screenshot":
                {
                    // 区域截图（支持 x/y/w/h 子区域 + 自定义 scale 拉近 2x；默认全屏且 maxW 限制）
                    int vx = Gdi.GetSystemMetrics(76), vy = Gdi.GetSystemMetrics(77);
                    int vw = Gdi.GetSystemMetrics(78), vh = Gdi.GetSystemMetrics(79);
                    int minX = Math.Min(0, vx), minY = Math.Min(0, vy);
                    int sw = vw, sh = vh;
                    int x = req.TryGetProperty("x", out var px) ? px.GetInt32() : vx;
                    int y = req.TryGetProperty("y", out var py) ? py.GetInt32() : vy;
                    int w = req.TryGetProperty("w", out var pw) ? pw.GetInt32() : vw;
                    int h = req.TryGetProperty("h", out var ph) ? ph.GetInt32() : vh;
                    int maxW = req.TryGetProperty("maxW", out var mw) ? mw.GetInt32() : 0;
                    double userScale = req.TryGetProperty("scale", out var ps) ? ps.GetDouble() : 0;
                    double scale = userScale > 0 ? userScale : (maxW > 0 && w > maxW ? (double)maxW / w : 1.0);
                    if (w <= 0) w = 1;
                    if (h <= 0) h = 1;
                    var (buf, bw, bh) = Gdi.CaptureScaled(x, y, w, h, scale);
                    string tmpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
                    Directory.CreateDirectory(tmpDir);
                    string tmp = Path.Combine(tmpDir, "sa-" + Guid.NewGuid().ToString("N") + ".jpg");
                    var jpg = GdiPlus.EncodeJpeg(buf, bw, bh, tmp);
                    return new { w, h, bw, bh, rect = new { x, y, w, h }, jpegB64 = Convert.ToBase64String(jpg), bytes = jpg.Length };
                }

            case "ocr":
                {
                    int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
                    int x = req.TryGetProperty("x", out var px) ? px.GetInt32() : 0;
                    int y = req.TryGetProperty("y", out var py) ? py.GetInt32() : 0;
                    int w = req.TryGetProperty("w", out var pw) ? pw.GetInt32() : sw - x;
                    int h = req.TryGetProperty("h", out var ph) ? ph.GetInt32() : sh - y;
                    double scale = req.TryGetProperty("scale", out var ps) ? ps.GetDouble() : 1.0;
                    var (buf, bw, bh) = Gdi.Capture(x, y, w, h);
                    var res = Ocr.Run(buf, bw, bh, scale);
                    var d = new Dictionary<string, object>(res) { { "rect", new { x, y, w, h } } };
                    return d;
                }

            case "uia":
                {
                    long hwnd = req.TryGetProperty("hwnd", out var phw) ? phw.GetInt64() : 0;
                    var items = Uia.Run(hwnd);
                    return new { items };
                }
            case "ocr_file":
                {
                    string path = req.GetProperty("path").GetString();
                    double scale = req.TryGetProperty("scale", out var psf) ? psf.GetDouble() : 1.0;
                    return ImageFile.Ocr(path, scale);
                }

            case "click":
                {
                    GuardInject();
                    int x = req.GetProperty("x").GetInt32(), y = req.GetProperty("y").GetInt32();
                    string button = req.TryGetProperty("button", out var b) ? b.GetString() : "left";
                    int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
                    int px = Math.Max(0, Math.Min(sw - 96, x - 48)), py = Math.Max(0, Math.Min(sh - 64, y - 32));
                    var (beforeBuf, pw, ph) = Gdi.Capture(px, py, 96, 64);
                    Win.SetCursorPos(x, y);
                    uint down = button == "right" ? 0x0008u : button == "middle" ? 0x0020u : 0x0002u;
                    uint up = button == "right" ? 0x0010u : button == "middle" ? 0x0040u : 0x0004u;
                    Win.MouseButton(down, up);
                    Thread.Sleep(90);
                    var (afterBuf, _, _) = Gdi.Capture(px, py, 96, 64);
                    int diffPixels = 0, totalPixels = Math.Max(1, pw * ph);
                    for (int i = 0; i + 3 < beforeBuf.Length && i + 3 < afterBuf.Length; i += 4)
                    {
                        if (Math.Abs(beforeBuf[i] - afterBuf[i]) > 16 ||
                            Math.Abs(beforeBuf[i + 1] - afterBuf[i + 1]) > 16 ||
                            Math.Abs(beforeBuf[i + 2] - afterBuf[i + 2]) > 16)
                            diffPixels++;
                    }
                    double diffRatio = (double)diffPixels / totalPixels;
                    return new { done = true, x, y, verified = diffRatio >= 0.005, diffRatio = Math.Round(diffRatio, 4) };
                }
            case "doubleclick":
                {
                    GuardInject();
                    int x = req.GetProperty("x").GetInt32(), y = req.GetProperty("y").GetInt32();
                    Win.SetCursorPos(x, y);
                    Win.MouseButton(0x0002, 0x0004, 2);
                    return new { done = true };
                }
            case "move":
                {
                    GuardInject();
                    Win.MouseMove(req.GetProperty("x").GetInt32(), req.GetProperty("y").GetInt32());
                    return new { done = true };
                }
            case "drag":
                {
                    GuardInject();
                    int x1 = req.GetProperty("x1").GetInt32(), y1 = req.GetProperty("y1").GetInt32();
                    int x2 = req.GetProperty("x2").GetInt32(), y2 = req.GetProperty("y2").GetInt32();
                    int dur = req.TryGetProperty("durationMs", out var dm) ? dm.GetInt32() : 300;
                    Win.SetCursorPos(x1, y1);
                    Win.MouseDown(0x0002); // 左键按下（按住拖拽）
                    int steps = Math.Max(1, dur / 10);
                    for (int i = 1; i <= steps; i++)
                    {
                        int cx = x1 + (x2 - x1) * i / steps;
                        int cy = y1 + (y2 - y1) * i / steps;
                        Win.SetCursorPos(cx, cy);
                        Thread.Sleep(10);
                    }
                    Win.MouseUp(0x0004); // 左键释放
                    return new { done = true };
                }
            case "scroll":
                {
                    GuardInject();
                    Win.MouseWheel(req.GetProperty("amount").GetInt32() * 120);
                    return new { done = true };
                }
            case "type":
                {
                    GuardInject();
                    string text = req.GetProperty("text").GetString();
                    Win.TypeText(text);
                    return new { done = true, chars = text.Length };
                }
            case "key":
                {
                    GuardInject();
                    Win.KeyPress(Win.VkOf(req.GetProperty("name").GetString()));
                    return new { done = true };
                }
            case "hotkey":
                {
                    GuardInject();
                    var keys = req.GetProperty("keys").EnumerateArray().Select(x => x.GetString()).ToArray();
                    Win.Hotkey(keys);
                    return new { done = true, keys };
                }
            case "clipboard_get":
                return new { text = Win.ClipboardGet() };
            case "clipboard_set":
                {
                    string text = req.GetProperty("text").GetString();
                    return new { done = Win.ClipboardSet(text), chars = text.Length };
                }
            case "focus":
                {
                    long hwnd = req.TryGetProperty("hwnd", out var phw2) ? phw2.GetInt64() : 0;
                    if (hwnd == 0 && req.TryGetProperty("title", out var ptt))
                        hwnd = Win.FindWindowByTitle(ptt.GetString());
                    if (hwnd == 0) return new { done = false, error = "no window (hwnd=0, title not found)" };
                    return new { done = Win.FocusWindow(hwnd), hwnd };
                }

            case "stop": lock (LockObj) { locked = true; } return new { locked = true };
            case "resume": lock (LockObj) { locked = false; } return new { locked = false };
            case "overlay_open":
                {
                    int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
                    int w = req.TryGetProperty("w", out var pw) ? pw.GetInt32() : 480;
                    int h = req.TryGetProperty("h", out var ph) ? ph.GetInt32() : 270;
                    int x = req.TryGetProperty("x", out var px) ? px.GetInt32() : sw - w - 40;
                    int y = req.TryGetProperty("y", out var py) ? py.GetInt32() : sh - h - 80;
                    Overlay.Open(x, y, w, h);
                    bool visible = Overlay.WaitReady(3000);
                    return new { open = true, visible, rect = new { x, y, w, h } };
                }
            case "overlay_close": Overlay.Close(); return new { open = false };
            case "overlay_set":
                {
                    lock (Overlay.Sync)
                    {
                        if (req.TryGetProperty("region", out var rg) && rg.ValueKind == JsonValueKind.Object)
                        {
                            Overlay.hasRect = true;
                            Overlay.attRect = new Rectangle(rg.GetProperty("x").GetInt32(), rg.GetProperty("y").GetInt32(), rg.GetProperty("w").GetInt32(), rg.GetProperty("h").GetInt32());
                        }
                        if (req.TryGetProperty("gaze", out var gz) && gz.ValueKind == JsonValueKind.Object)
                        {
                            Overlay.hasGaze = true;
                            Overlay.gazePt = new Point(gz.GetProperty("x").GetInt32(), gz.GetProperty("y").GetInt32());
                        }
                        if (req.TryGetProperty("clearRegion", out var cr) && cr.GetBoolean()) Overlay.hasRect = false;
                        if (req.TryGetProperty("clearGaze", out var cg) && cg.GetBoolean()) Overlay.hasGaze = false;
                        if (req.TryGetProperty("note", out var nt)) Overlay.note = nt.GetString() ?? "";
                    }
                    return new { hasRect = Overlay.hasRect, hasGaze = Overlay.hasGaze, note = Overlay.note };
                }
            case "watch_start":
                {
                    int iv = req.TryGetProperty("intervalMs", out var pi) ? pi.GetInt32() : 1000;
                    Watch.Start(iv);
                    return new { watching = true, intervalMs = Watch.IntervalMs };
                }
            case "watch_stop":
                {
                    Watch.Stop();
                    bool keepOvW = req.TryGetProperty("keepOverlay", out var pkow) && pkow.GetBoolean();
                    if (!keepOvW && !Infer.Running && !Interpret.Running) Overlay.Close();
                    return new { watching = false, overlayClosed = !keepOvW && !Overlay.IsOpen };
                }
            case "watch_status":
                {
                    var mr = Watch.MotionRect;
                    var fr = Watch.FocusRect;
                    return new
                    {
                        watching = Watch.IsWatching,
                        intervalMs = Watch.IntervalMs,
                        motion = Watch.Motion,
                        motionRatio = Math.Round(Watch.MotionRatio, 4),
                        motionRect = mr.IsEmpty ? null : new { x = mr.X, y = mr.Y, w = mr.Width, h = mr.Height },
                        focus = fr.IsEmpty ? null : new { x = fr.X, y = fr.Y, w = fr.Width, h = fr.Height },
                    };
                }
            case "char_register":
                {
                    string name = req.GetProperty("name").GetString();
                    string path = req.TryGetProperty("path", out var pcr) ? pcr.GetString() : null;
                    byte[] buf; int w, h;
                    if (!string.IsNullOrEmpty(path)) { var (b, ww, hh) = ImageFile.Load(path); buf = b; w = ww; h = hh; }
                    else { int sw = Gdi.ScreenW(), sh = Gdi.ScreenH(); (buf, w, h) = Gdi.Capture(0, 0, sw, sh); }
                    bool hasRegion = req.TryGetProperty("x", out _) && req.TryGetProperty("y", out _) && req.TryGetProperty("w", out _) && req.TryGetProperty("h", out _);
                    int rx = hasRegion ? req.GetProperty("x").GetInt32() : 0;
                    int ry = hasRegion ? req.GetProperty("y").GetInt32() : 0;
                    int rw = hasRegion ? req.GetProperty("w").GetInt32() : 0;
                    int rh = hasRegion ? req.GetProperty("h").GetInt32() : 0;
                    if (hasRegion) { }
                    else
                    {
                        var det = YoloDetect.Run(buf, w, h, true);
                        var items = (det.TryGetValue("items", out var iv) && iv is List<Dictionary<string, object>> l) ? l : new List<Dictionary<string, object>>();
                        var person = items.Where(i => Convert.ToString(i["label"]) == "person").OrderByDescending(i => Convert.ToDouble(i["w"]) * Convert.ToDouble(i["h"])).FirstOrDefault();
                        if (person == null) return new { ok = false, error = "未检测到人物，请指定区域 x/y/w/h" };
                        rx = Convert.ToInt32(person["x"]); ry = Convert.ToInt32(person["y"]); rw = Convert.ToInt32(person["w"]); rh = Convert.ToInt32(person["h"]);
                    }
                    var emb = ClipScene.Embed(buf, w, h, rx, ry, rw, rh);
                    if (emb == null) return new { ok = false, error = "区域无效" };
                    CharId.Register(name, emb);
                    if (req.TryGetProperty("note", out var pn) && pn.GetString() != null) CharId.SetNote(name, pn.GetString());
                    return new { ok = true, name, dim = emb.Length, rect = new { x = rx, y = ry, w = rw, h = rh }, total = CharId.Names().Count };
                }
            case "char_list":
                return new { count = CharId.Names().Count, characters = CharId.List() };
            case "char_merge":
                {
                    string from = req.GetProperty("from").GetString();
                    string to = req.GetProperty("to").GetString();
                    return new { ok = CharId.Merge(from, to), from, to, count = CharId.Names().Count };
                }
            case "char_note":
                {
                    string name = req.GetProperty("name").GetString();
                    string note = req.GetProperty("note").GetString();
                    return new { ok = CharId.SetNote(name, note), name, note };
                }
            case "char_unknowns":
                return new { count = CharId.ListUnknowns().Count, unknowns = CharId.ListUnknowns() };
            case "char_promote":
                {
                    string id = req.GetProperty("unknown").GetString();
                    string name = req.GetProperty("name").GetString();
                    return new { ok = CharId.Promote(id, name), from = id, name, count = CharId.Names().Count };
                }
            case "char_ignore":
                {
                    string uid = req.TryGetProperty("unknown", out var pu) ? pu.GetString()
                        : (req.TryGetProperty("targetId", out var pt) ? pt.GetString()
                        : (req.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null));
                    if (string.IsNullOrEmpty(uid)) throw new Exception("缺少 unknown/id 参数");
                    return new { ok = CharId.Ignore(uid), id = uid };
                }
            case "char_delete":
                {
                    string name = req.GetProperty("name").GetString();
                    return new { ok = CharId.Delete(name), name, count = CharId.Names().Count };
                }
            case "char_rename":
                {
                    string from = req.GetProperty("from").GetString();
                    string to = req.GetProperty("to").GetString();
                    return new { ok = CharId.Rename(from, to), from, to, count = CharId.Names().Count };
                }
            case "char_match":
                {
                    string path = req.TryGetProperty("path", out var pcm) ? pcm.GetString() : null;
                    byte[] buf; int w, h;
                    if (!string.IsNullOrEmpty(path)) { var (b, ww, hh) = ImageFile.Load(path); buf = b; w = ww; h = hh; }
                    else { int sw = Gdi.ScreenW(), sh = Gdi.ScreenH(); (buf, w, h) = Gdi.Capture(0, 0, sw, sh); }
                    bool hasRegion = req.TryGetProperty("x", out _) && req.TryGetProperty("y", out _) && req.TryGetProperty("w", out _) && req.TryGetProperty("h", out _);
                    var results = new List<Dictionary<string, object>>();
                    if (hasRegion)
                    {
                        int mx = req.GetProperty("x").GetInt32(), my = req.GetProperty("y").GetInt32();
                        int mw = req.GetProperty("w").GetInt32(), mh = req.GetProperty("h").GetInt32();
                        var emb = ClipScene.Embed(buf, w, h, mx, my, mw, mh);
                        var (nm, sc, uid) = CharId.MatchOrUnknown(emb);
                        results.Add(new Dictionary<string, object> { { "name", nm }, { "unknownId", uid }, { "score", Math.Round(sc, 3) }, { "note", nm != null ? CharId.NoteOf(nm) : null }, { "rect", new { x = mx, y = my, w = mw, h = mh } } });
                    }
                    else
                    {
                        var det = YoloDetect.Run(buf, w, h, true);
                        var items = (det.TryGetValue("items", out var iv2) && iv2 is List<Dictionary<string, object>> l2) ? l2 : new List<Dictionary<string, object>>();
                        foreach (var per in items.Where(i => Convert.ToString(i["label"]) == "person"))
                        {
                            int bx = Convert.ToInt32(per["x"]), by = Convert.ToInt32(per["y"]), bw = Convert.ToInt32(per["w"]), bh = Convert.ToInt32(per["h"]);
                            var emb = ClipScene.Embed(buf, w, h, bx, by, bw, bh);
                            var (nm, sc, uid) = CharId.MatchOrUnknown(emb);
                            results.Add(new Dictionary<string, object> { { "name", nm }, { "unknownId", uid }, { "score", Math.Round(sc, 3) }, { "note", nm != null ? CharId.NoteOf(nm) : null }, { "rect", new { x = bx, y = by, w = bw, h = bh } } });
                        }
                    }
                    return new { registered = CharId.Names(), matches = results };
                }
            case "animeface":
                {
                    string path = req.TryGetProperty("path", out var paf) ? paf.GetString() : null;
                    byte[] buf; int w, h;
                    if (!string.IsNullOrEmpty(path)) { var (b, ww, hh) = ImageFile.Load(path); buf = b; w = ww; h = hh; }
                    else { int sw = Gdi.ScreenW(), sh = Gdi.ScreenH(); (buf, w, h) = Gdi.Capture(0, 0, sw, sh); }
                    float cf = req.TryGetProperty("conf", out var pcf) ? (float)pcf.GetDouble() : 0.25f;
                    int mf = req.TryGetProperty("maxFaces", out var pmf) ? pmf.GetInt32() : 20;
                    var res = AnimeFaceDetect.Run(buf, w, h, cf, mf);
                    res["rect"] = new { x = 0, y = 0, w, h };
                    return res;
                }
            case "health":
                {
                    string md = PaddleOcr.ModelDir();
                    string[] requiredModels =
                    {
                        Path.Combine(md, "ch_PP-OCRv4_det_infer.onnx"),
                        Path.Combine(md, "ch_PP-OCRv4_rec_infer.onnx"),
                        Path.Combine(md, "yolov8m.onnx"),
                        Path.Combine(md, "animeface", "model.onnx"),
                    };
                    var models = new Dictionary<string, object>();
                    foreach (var f in requiredModels) models[Path.GetFileName(Path.GetDirectoryName(f)) == "animeface" ? "animeface" : Path.GetFileNameWithoutExtension(f)] = File.Exists(f);
                    string clipLarge = Path.Combine(md, "chinese-clip-large", "image_encoder.onnx");
                    string clipBase = Path.Combine(md, "chinese-clip", "image_encoder.onnx");
                    string clip = File.Exists(clipLarge) ? "chinese-clip-large" : (File.Exists(clipBase) ? "chinese-clip" : null);
                    return new
                    {
                        ok = clip != null && File.Exists(Path.Combine(md, "ch_PP-OCRv4_det_infer.onnx")) && File.Exists(Path.Combine(md, "yolov8m.onnx")),
                        models,
                        clip,
                        faceModel = File.Exists(Path.Combine(md, "animeface", "model.onnx")),
                        characters = CharId.Names().Count,
                        unknowns = CharId.ListUnknowns().Count,
                        autoLearned = CharId.AutoLearnedCount,
                        workingSetMB = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1),
                        providers = Providers.Available,
                        secureDesktop = Gdi.LastSecureDesktop,
                        privacyKeywords = Gdi.GetPrivacyKeywords(),
                        privacyRedactions = Gdi.PrivacyRedactedCount,
                        fgTitle = Win.ForegroundTitle(),
                        sessionId = Process.GetCurrentProcess().SessionId,
                    };
                }
            case "providers":
                return new
                {
                    available = Providers.Available,
                    ocr = PaddleOcr.Backend,
                    detect = YoloDetect.Backend,
                    clip = ClipScene.Backend,
                };
            case "scene":
                {
                    string path = req.TryGetProperty("path", out var psp) ? psp.GetString() : null;
                    byte[] buf; int w, h;
                    if (!string.IsNullOrEmpty(path)) { var (b, ww, hh) = ImageFile.Load(path); buf = b; w = ww; h = hh; }
                    else { int sw = Gdi.ScreenW(), sh = Gdi.ScreenH(); (buf, w, h) = Gdi.Capture(0, 0, sw, sh); }
                    int topK = req.TryGetProperty("topK", out var ptk) ? ptk.GetInt32() : 5;
                    var res = ClipScene.Run(buf, w, h, topK);
                    res["rect"] = new { x = 0, y = 0, w, h };
                    return res;
                }
            case "detect":
                {
                    string path = req.TryGetProperty("path", out var pdp) ? pdp.GetString() : null;
                    byte[] buf; int w, h;
                    if (!string.IsNullOrEmpty(path)) { var (b, ww, hh) = ImageFile.Load(path); buf = b; w = ww; h = hh; }
                    else { int sw = Gdi.ScreenW(), sh = Gdi.ScreenH(); (buf, w, h) = Gdi.Capture(0, 0, sw, sh); }
                    bool accurate = req.TryGetProperty("model", out var pmd) && (pmd.GetString() == "m" || pmd.GetString() == "l" || pmd.GetString() == "x");
                    var res = YoloDetect.Run(buf, w, h, accurate);
                    res["rect"] = new { x = 0, y = 0, w, h };
                    if (res.TryGetValue("items", out var div) && div is List<Dictionary<string, object>> dl)
                    {
                        foreach (var it in dl)
                        {
                            if (Convert.ToString(it["label"]) != "person") continue;
                            try
                            {
                                var emb = ClipScene.Embed(buf, w, h, Convert.ToInt32(it["x"]), Convert.ToInt32(it["y"]), Convert.ToInt32(it["w"]), Convert.ToInt32(it["h"]));
                                if (emb == null) continue;
                                var (nm, sc, uid) = CharId.MatchOrUnknown(emb);
                                if (nm != null) { it["identity"] = nm; it["identityScore"] = Math.Round(sc, 3); it["identityNote"] = CharId.NoteOf(nm); }
                                else if (uid != null) { it["unknownId"] = uid; it["unknownScore"] = Math.Round(sc, 3); }
                            }
                            catch { }
                        }
                    }
                    return res;
                }
            case "infer_start":
                {
                    int boxes = req.TryGetProperty("maxBoxes", out var pb) ? pb.GetInt32() : 60;
                    bool withObjects = req.TryGetProperty("objects", out var pobj) && pobj.GetBoolean();
                    bool withScene = req.TryGetProperty("scene", out var psc) && psc.GetBoolean();
                    bool accurate = req.TryGetProperty("accurate", out var pac) && pac.GetBoolean();
                    bool withFaces = req.TryGetProperty("animeface", out var paf) && paf.GetBoolean();
                    int sceneEvery = req.TryGetProperty("sceneEvery", out var pse) ? pse.GetInt32() : 8;
                    int faceEvery = req.TryGetProperty("faceEvery", out var pfe) ? pfe.GetInt32() : 4;
                    Infer.Start(boxes, withObjects, withScene, accurate, withFaces, sceneEvery, faceEvery);
                    return new { running = true, maxBoxes = boxes, objects = withObjects, scene = withScene, accurate = accurate, animeface = withFaces, sceneEvery, faceEvery };
                }
            case "infer_stop":
                {
                    Infer.Stop();
                    bool keepOv = req.TryGetProperty("keepOverlay", out var pko) && pko.GetBoolean();
                    if (!keepOv && !Watch.IsWatching) Overlay.Close();
                    return new { running = false, overlayClosed = !keepOv && !Overlay.IsOpen };
                }
            case "infer_status":
                {
                    int limit = req.TryGetProperty("limit", out var pli) ? pli.GetInt32() : 10;
                    return Infer.Status(limit);
                }
            case "interpret_start":
                {
                    int ms = req.TryGetProperty("intervalMs", out var pim) ? pim.GetInt32() : 800;
                    Rectangle? rr = null;
                    if (req.TryGetProperty("x", out var ix) && req.TryGetProperty("y", out var iy) &&
                        req.TryGetProperty("w", out var iw) && req.TryGetProperty("h", out var ih))
                        rr = new Rectangle(ix.GetInt32(), iy.GetInt32(), iw.GetInt32(), ih.GetInt32());
                    Interpret.Start(ms, rr);
                    return new { running = true, intervalMs = ms, region = rr.HasValue ? new { x = rr.Value.X, y = rr.Value.Y, w = rr.Value.Width, h = rr.Value.Height } : (object)null };
                }
            case "interpret_stop":
                {
                    Interpret.Stop();
                    bool keepOv2 = req.TryGetProperty("keepOverlay", out var pko2) && pko2.GetBoolean();
                    if (!keepOv2 && !Watch.IsWatching) Overlay.Close();
                    return new { running = false, overlayClosed = !keepOv2 && !Overlay.IsOpen };
                }
            case "interpret_status":
                {
                    int limit = req.TryGetProperty("limit", out var plm) ? plm.GetInt32() : 20;
                    return Interpret.Status(limit);
                }
            case "watch_focus":
                {
                    if (req.TryGetProperty("clear", out var cl) && cl.GetBoolean())
                    {
                        Watch.SetFocus(Rectangle.Empty);
                        return new { focus = "cleared" };
                    }
                    int fx = req.GetProperty("x").GetInt32(), fy = req.GetProperty("y").GetInt32();
                    int fw = req.GetProperty("w").GetInt32(), fh = req.GetProperty("h").GetInt32();
                    Watch.SetFocus(new Rectangle(fx, fy, fw, fh));
                    return new { focus = new { x = fx, y = fy, w = fw, h = fh } };
                }
            case "watch_snapshot": return Watch.Snapshot();
            case "shutdown":
                try { Infer.Stop(); Interpret.Stop(); Watch.Stop(); Overlay.Close(); } catch { }
                return new { bye = true };

            default: throw new Exception("UNKNOWN_CMD: " + cmd);
        }
    }

    static void Main(string[] args)
    {
        try { if (!OperatingSystem.IsWindows()) throw new Exception("windows only"); } catch { }
        // stdio JSON 行协议强制 UTF-8（客户端可能是 Node(UTF-8) 或 PowerShell(GBK) 编码写入；须在 SessionBridge 重定向前设置）
        try { Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = Encoding.UTF8; } catch { }
        if (SessionBridge.TryBridgeIfSession0(args, out int bridgeCode))
        {
            Environment.Exit(bridgeCode);
            return;
        }
        Gdi.EnablePerMonitorDpi();
        var ht = new Thread(HotkeyThread) { IsBackground = true };
        ht.Start();
        // 后台预热 PaddleOCR 模型（DirectML/CPU 加载 ~1.6s），避免首次 ocr 调用冷启动
        var warm = new Thread(() => { try { PaddleOcr.Warmup(); } catch { } }) { IsBackground = true };
        warm.Start();
        Event("ready");

        string line;
        long lastNumericId = 0;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            long id = 0;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var ie))
                {
                    if (ie.ValueKind == JsonValueKind.Number) { id = ie.GetInt64(); lastNumericId = id; }
                    else { id = ++lastNumericId; }
                }
                string cmd = root.GetProperty("cmd").GetString();
                object data = Run(cmd, root);
                WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { { "id", id }, { "ok", true }, { "data", data } }));
                if (cmd == "shutdown") break;
            }
            catch (Exception e)
            {
                WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { { "id", id }, { "ok", false }, { "error", e.Message } }));
            }
        }
        Win.UnregisterHotKey(IntPtr.Zero, HOTKEY_ID);
    }
}
