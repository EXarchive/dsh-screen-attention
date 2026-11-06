using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
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
    // ==== 采集资源复用（60fps 关键）：DC / 位图 / 输出缓冲按尺寸缓存，避免每帧 ~16MB 的分配与 GDI 重建 ====
    static IntPtr _capMem = IntPtr.Zero, _capBmp = IntPtr.Zero, _capOld = IntPtr.Zero;
    static int _capW, _capH;
    static byte[] _capBuf = Array.Empty<byte>();

    static readonly object CapLock = new object();

    public static (byte[] buf, int w, int h) Capture(int x, int y, int w, int h)
    {
        lock (CapLock)
        {
        w = Math.Max(1, w); h = Math.Max(1, h);
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) { LastSecureDesktop = true; return (new byte[w * h * 4], w, h); }
        if (_capMem == IntPtr.Zero || _capW != w || _capH != h)
        {
            if (_capBmp != IntPtr.Zero) { try { SelectObject(_capMem, _capOld); } catch { } DeleteObject(_capBmp); _capBmp = IntPtr.Zero; }
            if (_capMem != IntPtr.Zero) { DeleteDC(_capMem); _capMem = IntPtr.Zero; }
            _capMem = CreateCompatibleDC(dc);
            _capBmp = CreateCompatibleBitmap(dc, w, h);
            _capOld = SelectObject(_capMem, _capBmp);
            _capW = w; _capH = h;
            _capBuf = new byte[w * h * 4];
        }
        bool ok = BitBlt(_capMem, 0, 0, w, h, dc, x, y, 0x00CC0020); // SRCCOPY
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
        int lines = ok ? GetDIBits(_capMem, _capBmp, 0, (uint)h, _capBuf, ref bi, 0) : 0;
        ReleaseDC(IntPtr.Zero, dc);
        var buf = _capBuf;
        LastSecureDesktop = !ok || lines <= 0;
        if (!LastSecureDesktop)
        {
            var privRects = GetPrivacyRects();
            for (int i = 0; i < privRects.Count; i++) RedactRect(buf, w, h, x, y, privRects[i]);
        }
        return (buf, w, h);
        }
    }

    // ==== 高速采集：StretchBlt 直接从屏幕 DC 缩放到小位图，跳过 16MB 全屏中间缓冲 ====
    static IntPtr _sMem = IntPtr.Zero, _sBmp = IntPtr.Zero, _sOld = IntPtr.Zero;
    static int _sW, _sH;
    static byte[] _sBuf = Array.Empty<byte>();

    static readonly object SmallLock = new object();

    public static (byte[] buf, int w, int h) CaptureSmall(int x, int y, int w, int h, double scale)
    {
        lock (SmallLock)
        {
            int dw = Math.Max(1, (int)Math.Round(w * scale));
            int dh = Math.Max(1, (int)Math.Round(h * scale));
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) { LastSecureDesktop = true; return (new byte[dw * dh * 4], dw, dh); }
            if (_sMem == IntPtr.Zero || _sW != dw || _sH != dh)
            {
                if (_sBmp != IntPtr.Zero) { try { SelectObject(_sMem, _sOld); } catch { } DeleteObject(_sBmp); _sBmp = IntPtr.Zero; }
                if (_sMem != IntPtr.Zero) { DeleteDC(_sMem); _sMem = IntPtr.Zero; }
                _sMem = CreateCompatibleDC(dc);
                _sBmp = CreateCompatibleBitmap(dc, dw, dh);
                _sOld = SelectObject(_sMem, _sBmp);
                _sW = dw; _sH = dh;
                _sBuf = new byte[dw * dh * 4];
            }
            SetStretchBltMode(_sMem, 3); // COLORONCOLOR：最近邻缩放，比 HALFTONE 快数倍
            bool ok = StretchBlt(_sMem, 0, 0, dw, dh, dc, x, y, w, h, 0x00CC0020);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = dw, biHeight = -dh, biPlanes = 1, biBitCount = 32, biCompression = 0 };
            int lines = ok ? GetDIBits(_sMem, _sBmp, 0, (uint)dh, _sBuf, ref bi, 0) : 0;
            ReleaseDC(IntPtr.Zero, dc);
            LastSecureDesktop = !ok || lines <= 0;
            return (_sBuf, dw, dh);
        }
    }

    // ==== 并行采集：上下半屏两条流各用独立 DC/位图/锁，接近翻倍吞吐 ====
    static readonly object SL0 = new object(), SL1 = new object();
    static readonly IntPtr[] _slotMem = new IntPtr[2];
    static readonly IntPtr[] _slotBmp = new IntPtr[2];
    static readonly IntPtr[] _slotOld = new IntPtr[2];
    static readonly int[] _slotW = new int[2];
    static readonly int[] _slotH = new int[2];
    static readonly byte[][] _slotBuf = new byte[2][];
    static byte[] _parBuf;

    static (byte[] buf, int w, int h) CaptureSmallSlot(int slot, int x, int y, int w, int h, double scale)
    {
        lock (slot == 0 ? SL0 : SL1)
        {
            int dw = Math.Max(1, (int)Math.Round(w * scale));
            int dh = Math.Max(1, (int)Math.Round(h * scale));
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) { LastSecureDesktop = true; return (new byte[dw * dh * 4], dw, dh); }
            if (_slotMem[slot] == IntPtr.Zero || _slotW[slot] != dw || _slotH[slot] != dh)
            {
                if (_slotBmp[slot] != IntPtr.Zero) { try { SelectObject(_slotMem[slot], _slotOld[slot]); } catch { } DeleteObject(_slotBmp[slot]); _slotBmp[slot] = IntPtr.Zero; }
                if (_slotMem[slot] != IntPtr.Zero) { DeleteDC(_slotMem[slot]); _slotMem[slot] = IntPtr.Zero; }
                _slotMem[slot] = CreateCompatibleDC(dc);
                _slotBmp[slot] = CreateCompatibleBitmap(dc, dw, dh);
                _slotOld[slot] = SelectObject(_slotMem[slot], _slotBmp[slot]);
                _slotW[slot] = dw; _slotH[slot] = dh;
                _slotBuf[slot] = new byte[dw * dh * 4];
            }
            SetStretchBltMode(_slotMem[slot], 3);
            bool ok = StretchBlt(_slotMem[slot], 0, 0, dw, dh, dc, x, y, w, h, 0x00CC0020);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = dw, biHeight = -dh, biPlanes = 1, biBitCount = 32, biCompression = 0 };
            int lines = ok ? GetDIBits(_slotMem[slot], _slotBmp[slot], 0, (uint)dh, _slotBuf[slot], ref bi, 0) : 0;
            ReleaseDC(IntPtr.Zero, dc);
            LastSecureDesktop = !ok || lines <= 0;
            return (_slotBuf[slot], dw, dh);
        }
    }

    public static (byte[] buf, int w, int h) CaptureSmallParallel(int w, int h, double scale)
    {
        int half = h / 2;
        byte[] b0 = null, b1 = null; int w0 = 0, h0 = 0, w1 = 0, h1 = 0;
        var t0 = System.Threading.Tasks.Task.Run(() => { var r = CaptureSmallSlot(0, 0, 0, w, half, scale); b0 = r.buf; w0 = r.w; h0 = r.h; });
        var t1 = System.Threading.Tasks.Task.Run(() => { var r = CaptureSmallSlot(1, 0, half, w, h - half, scale); b1 = r.buf; w1 = r.w; h1 = r.h; });
        System.Threading.Tasks.Task.WaitAll(t0, t1);
        if (b0 == null || b1 == null) return (new byte[w * h * 4], w, h);
        int total = b0.Length + b1.Length;
        if (_parBuf == null || _parBuf.Length != total) _parBuf = new byte[total];
        Buffer.BlockCopy(b0, 0, _parBuf, 0, b0.Length);
        Buffer.BlockCopy(b1, 0, _parBuf, b0.Length, b1.Length);
        return (_parBuf, w0, h0 + h1);
    }

    // capture + optional scale (StretchBlt HALFTONE), returns top-down BGRA
    public static (byte[] buf, int w, int h) CaptureScaled(int x, int y, int w, int h, double scale)
    {
        var (src, sw, sh) = Capture(x, y, w, h);
        if (Math.Abs(scale - 1.0) < 0.01) return (src, sw, sh);
        int dw = Math.Max(1, (int)Math.Round(sw * scale));
        int dh = Math.Max(1, (int)Math.Round(sh * scale));
        return (ScaleBuffer(src, sw, sh, dw, dh), dw, dh);
    }

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);

    /// <summary>
    /// 抓窗口自身内容，不依赖它在屏幕上是否可见。
    /// PrintWindow + PW_RENDERFULLCONTENT(0x2)：对**被遮挡**的窗口有效（DWM 会渲染离屏内容）；
    /// 最小化窗口通常拿不到，所以调用方要先 SW_SHOWNOACTIVATE 恢复（不抢焦点）再抓。
    /// </summary>
    public static (byte[] buf, int w, int h, bool ok) PrintWindowCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return (null, 0, 0, false);
        Win.RECT r;
        if (!Win.GetWindowRect(hwnd, out r)) return (null, 0, 0, false);
        int w = r.R - r.L, hh = r.B - r.T;
        if (w < 8 || hh < 8 || w > 8000 || hh > 8000) return (null, 0, 0, false);

        IntPtr dc = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(dc);
        IntPtr bmp = CreateCompatibleBitmap(dc, w, hh);
        IntPtr old = SelectObject(mem, bmp);
        bool ok = PrintWindow(hwnd, mem, 2);   // PW_RENDERFULLCONTENT
        var buf = new byte[w * hh * 4];
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -hh, biPlanes = 1, biBitCount = 32, biCompression = 0 };
        GetDIBits(mem, bmp, 0, (uint)hh, buf, ref bi, 0);
        SelectObject(mem, old); DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, dc);
        return (buf, w, hh, ok);
    }



    /// <summary>把已有的 BGRA buffer 缩放到目标尺寸（HALFTONE）。用于"先高分辨率打分、再缩到格子尺寸留帧"。</summary>
    public static byte[] ScaleBuffer(byte[] src, int sw, int sh, int dw, int dh)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        IntPtr smem = CreateCompatibleDC(dc), dmem = CreateCompatibleDC(dc);
        IntPtr sbmp = CreateCompatibleBitmap(dc, sw, sh), dbmp = CreateCompatibleBitmap(dc, dw, dh);
        IntPtr sOld = SelectObject(smem, sbmp), dOld = SelectObject(dmem, dbmp);
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
        return outBuf;
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
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
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
    // holdMs：按下与抬起之间的保持时间。0 → 使用默认 25ms（真实点击约 80~120ms，过快会被 DirectInput 类应用忽略）
    public static void MouseButton(uint down, uint up, int clicks = 1, int holdMs = 0)
    {
        int hold = holdMs > 0 ? Math.Min(holdMs, 10000) : 25;
        for (int i = 0; i < clicks; i++)
        {
            MouseDown(down);
            Thread.Sleep(hold);
            MouseUp(up);
            if (clicks > 1 && i < clicks - 1) Thread.Sleep(30);
        }
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
    public static void KeyDown(ushort vk)
    {
        var a = new INPUT[1];
        a[0].type = 1; a[0].ki.vk = vk;
        Send(a);
    }
    public static void KeyUp(ushort vk)
    {
        var a = new INPUT[1];
        a[0].type = 1; a[0].ki.vk = vk; a[0].ki.flags = 0x0002;
        Send(a);
    }
    // holdMs>0 时按住不放，用于长按（游戏移动、连续输入、长按菜单）
    public static void KeyPress(ushort vk, int holdMs = 0)
    {
        KeyDown(vk);
        if (holdMs > 0) Thread.Sleep(Math.Min(holdMs, 10000));
        else Thread.Sleep(12);
        KeyUp(vk);
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
        // 分段发送 + 帧间延迟：一次性同时间戳发送时，部分应用（如 Notepad）会忽略 Alt+F4 等组合键。
        if (mods.Count > 0)
        {
            Send(mods.Select(m => KeyInput(m, false)).ToArray());
            Thread.Sleep(28);
        }
        if (main != 0)
        {
            Send(new[] { KeyInput(main, false) });
            Thread.Sleep(38);
            Send(new[] { KeyInput(main, true) });
            Thread.Sleep(22);
        }
        for (int i = mods.Count - 1; i >= 0; i--)
        {
            Send(new[] { KeyInput(mods[i], true) });
            Thread.Sleep(12);
        }
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

    /// <summary>窗口客户区在屏幕上的矩形（去掉标题栏与边框）。观察画面只该看内容区。</summary>
    public static (int x, int y, int w, int h)? ClientRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        RECT rc;
        if (!GetClientRect(hwnd, out rc)) return null;
        int w = rc.R - rc.L, hh = rc.B - rc.T;
        if (w <= 0 || hh <= 0) return null;
        var pt = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref pt)) return null;
        return (pt.X, pt.Y, w, hh);
    }

    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT rc);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);

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
// ============ 语音合成（Windows 自带 SAPI，System.Speech 包） ============
// 用途：浮窗 say 的同时把话说出来。异步朗读，不阻塞命令循环。
static class Tts
{
    static System.Speech.Synthesis.SpeechSynthesizer _synth;
    static readonly object _lock = new object();
    public static int Rate = 0;

    public static (bool ok, string err) Speak(string text, int rate = 0)
    {
        try
        {
            lock (_lock)
            {
                if (_synth == null)
                {
                    _synth = new System.Speech.Synthesis.SpeechSynthesizer();
                    _synth.SetOutputToDefaultAudioDevice();
                }
                _synth.Rate = Math.Clamp(rate != 0 ? rate : Rate, -10, 10);
                _synth.SpeakAsyncCancelAll();   // 新话打断旧话，不排队积压
                _synth.SpeakAsync(text);
            }
            return (true, null);
        }
        catch (Exception e) { return (false, e.GetType().Name + ": " + e.Message); }
    }

    public static List<object> Voices()
    {
        var list = new List<object>();
        try
        {
            var s = new System.Speech.Synthesis.SpeechSynthesizer();
            foreach (var v in s.GetInstalledVoices())
                list.Add(new { name = v.VoiceInfo.Name, culture = v.VoiceInfo.Culture?.Name, enabled = v.Enabled });
            s.Dispose();
        }
        catch { }
        return list;
    }
}

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
// 双缓冲窗体：直接在 new Form 上设 DoubleBuffered 不行（protected），必须子类。
// 不开这个，每帧 Invalidate 都是「先擦背景再画」→ 肉眼可见闪屏。
sealed class OverlayForm : Form
{
    public OverlayForm()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        UpdateStyles();
    }
    // 彻底不擦背景（WM_ERASEBKGND），否则双缓冲也压不住边缘闪
    protected override void OnPaintBackground(PaintEventArgs e) { }
}

// ============ 消息窗：AI 说的话独立成一个窗口，不与图像窗重叠 ============
// 为什么要分开：气泡画在图像窗里会盖住正在看的内容（实测挡住视频下部）。
// 布局：默认贴在图像窗上方 8px；上方放不下就放下方；没有图像窗则浮在屏幕右下角。
static class SayWindow
{
    sealed class SayForm : Form
    {
        public string Body = "";
        public SayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(12, 20, 44);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            DoubleBuffered = true;
        }
        // 不擦背景：双缓冲 + 不擦 = 不闪（与图像窗同一套手法）
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rc = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Overlay.RoundRect(rc, 10);
            using (var bg = new SolidBrush(Color.FromArgb(235, 12, 20, 44)))
            using (var pen = new Pen(Color.FromArgb(210, 96, 196, 255), 1.5f))
            {
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }
            // 左侧竖条：标记"这句话来自 AI"
            using (var bar2 = new SolidBrush(Color.FromArgb(220, 96, 196, 255)))
                g.FillRectangle(bar2, 3, 8, 3, Height - 16);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, Body, bodyFont, new Rectangle(16, 6, Width - 28, Height - 12), Color.FromArgb(236, 244, 255), flags);
        }
    }

    static SayForm form;
    static string text = "";
    static long until = 0;
    public static string LastError;   // 创建失败时留下来，供 say 命令回报
    static Rectangle lastTarget;
    public static Rectangle LastTarget => lastTarget;
    public static Rectangle? Bounds
    {
        get
        {
            var f = form;
            if (f == null || f.IsDisposed) return null;
            try { return f.Bounds; } catch { return null; }
        }
    }
    static readonly object gate = new object();
    static readonly Font bodyFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
    public static bool IsOpen => form != null;
    public static bool VisibleNow
    {
        get { var f = form; if (f == null || f.IsDisposed) return false; try { return f.Visible; } catch { return false; } }
    }

    static int MeasureH(string t, int w)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        var sz = TextRenderer.MeasureText(t, bodyFont, new Size(Math.Max(60, w - 28), 600), flags);
        return Math.Clamp(sz.Height + 20, 44, 170);
    }

    /// <summary>算目标矩形：图像窗上方优先，其次下方，最后屏幕右下角。</summary>
    static Rectangle Target(string t, Rectangle? anchor)
    {
        int w, x, y, hh;
        if (anchor.HasValue)
        {
            var a = anchor.Value;
            w = Math.Max(200, a.Width);
            hh = MeasureH(t, w);
            x = a.X;
            y = a.Y - hh - 8;
            if (y < 0) y = a.Bottom + 8;                        // 上方放不下 → 下方
            var scr = Screen.FromRectangle(a).WorkingArea;
            if (y + hh > scr.Bottom) y = Math.Max(scr.Top, scr.Bottom - hh - 8);
            if (x + w > scr.Right) x = Math.Max(scr.Left, scr.Right - w);
        }
        else
        {
            var scr = Screen.PrimaryScreen.WorkingArea;
            w = 420;
            hh = MeasureH(t, w);
            x = scr.Right - w - 16;
            y = scr.Bottom - hh - 16;
        }
        return new Rectangle(x, y, w, hh);
    }

    public static void Show(string t, int ms)
    {
        lock (gate) { text = t ?? ""; until = Environment.TickCount64 + Math.Max(1200, ms); }
        if (t.Trim().Length == 0) { Close(); return; }
        var target = Target(text, Overlay.CurrentBounds());
        lastTarget = target;
        if (form == null || form.IsDisposed)
        {
            var sig = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var th = new Thread(() =>
            {
                try
                {
                    form = new SayForm { Bounds = target, Body = text, Text = "SA 消息" };
                    // 拖动：消息窗也能挪走
                    Point off = Point.Empty; bool drag = false;
                    form.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { drag = true; off = e.Location; } };
                    form.MouseMove += (s, e) => { if (drag) form.Location = new Point(form.Left + e.X - off.X, form.Top + e.Y - off.Y); };
                    form.MouseUp += (s, e) => { drag = false; Overlay.SyncSayWindow(); };
                    form.DoubleClick += (s, e) => { try { Close(); } catch { } };
                    var timer = new System.Windows.Forms.Timer { Interval = 120 };
                    timer.Tick += (s, e) =>
                    {
                        try
                        {
                            bool live;
                            lock (gate) live = Environment.TickCount64 < until;
                            if (!live && form != null) { Close(); }
                        }
                        catch { }
                    };
                    timer.Start();
                    form.FormClosed += (s, e) => { try { timer.Stop(); } catch { } };
                    // 关键：Bounds 在构造时设置会被 WinForms 忽略（句柄还没建）。
                    // 必须在 Shown 里再设一次，并显式 Show + 抬高 z-order。
                    form.Shown += (s, e) =>
                    {
                        try
                        {
                            form.Bounds = target;
                            form.TopMost = true;
                            form.Visible = true;
                            form.BringToFront();
                        }
                        catch (Exception ex2) { LastError = "shown: " + ex2.Message; }
                        sig.TrySetResult(true);
                    };
                    Application.Run(form);
                }
                catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; sig.TrySetResult(false); }
                form = null;
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            try { sig.Task.Wait(1500); } catch { }
        }
        else
        {
            try
            {
                form.Invoke(new Action(() =>
                {
                    try { form.Body = text; form.Bounds = target; form.Invalidate(); } catch { }
                }));
            }
            catch { }
        }
    }

    /// <summary>图像窗移动/缩放时跟随。</summary>
    public static void Follow(Rectangle? anchor)
    {
        var f = form;
        if (f == null || f.IsDisposed) return;
        string t; lock (gate) t = text;
        if (t.Length == 0) return;
        var target = Target(t, anchor);
        try { f.Invoke(new Action(() => { try { f.Bounds = target; f.Invalidate(); } catch { } })); } catch { }
    }

    public static void Close()
    {
        var f = form;
        form = null;
        if (f == null) return;
        try { f.Invoke(new Action(() => { try { f.Close(); } catch { } })); } catch { }
    }
}

static class Overlay
{
    public static Form form;
    public static Label bar;   // 保留字段兼容旧引用，信息条已改自绘（见 BarH + DrawBar）
    public const int BarH = 40;
    public static readonly object Sync = new object();
    public static bool hasRect;
    public static Rectangle attRect;
    public static bool hasGaze;
    public static Point gazePt;
    public static string note = "";
    // 讲解字幕：与画面同框（重构：原先走独立的 SayWindow，现并入图像窗底部）
    public static string subtitle = "";
    public static long subtitleAt = 0;
    static readonly Font subtitleFont = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);
    public static byte[] frame;      // 最新采集帧 BGRA（watch 线程写）
    public static volatile int frameStamp;   // 帧序号：只在真换帧时自增，供重绘判断
    static bool sayJustCleared;              // 气泡刚消失的那一帧也要重绘一次
    public static int fw, fh;
    public static double fps;        // 采集帧率
    public static int originX = 0, originY = 0;  // 虚拟屏幕原点（多显示器，帧左上角虚拟坐标）
    public static bool IsOpen => form != null;

    /// <summary>图像窗当前矩形（消息窗据此定位）；未打开返回 null。</summary>
    static Rectangle cachedBounds;
    static volatile bool hasCachedBounds;
    public static Rectangle? CurrentBounds()
    {
        // 跨线程读控件属性不可靠，用 UI 线程刷新的缓存
        if (hasCachedBounds) return cachedBounds;
        return null;
    }

    /// <summary>图像窗移动/缩放后，让消息窗跟着走。</summary>
    public static void SyncSayWindow()
    {
        try { SayWindow.Follow(CurrentBounds()); } catch { }
    }
    // ==== say 气泡：AI 的话直接显示在小窗上（同时可选朗读）====
    public static string sayText = "";
    public static long sayUntil = 0;
    public static void Say(string text, int ms = 5000)
    {
        // 文字走独立消息窗，图像窗只留画面
        SayWindow.Show(text, ms);
    }
    public static void ClearSay() { SayWindow.Close(); }
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
                form = new OverlayForm
                {
                    Text = "SA 注意力",
                    TopMost = true,
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = new Rectangle(x, y, w, h),
                    BackColor = Color.Black,
                };
                // 信息条改为自绘（PaintFrame → DrawBar），不再用 Label：Label 画不了圆点/分隔线/左右分层
                form.DoubleClick += (s, e) => { try { form.Close(); } catch { } };
                Point dragOff = Point.Empty; bool dragging = false;
                form.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragOff = e.Location; } };
                form.MouseMove += (s, e) => { if (dragging) form.Location = new Point(form.Left + e.X - dragOff.X, form.Top + e.Y - dragOff.Y); };
                form.MouseUp += (s, e) => dragging = false;
                form.Paint += (s, e) => PaintFrame(e.Graphics, form.ClientSize);
                // 重绘定时器：100ms 只做事，不样样都干
                // 三条节流（每条都曾是闪屏源）：
                //   ① 标题只在 note 真变时改 —— 每 tick 改 Text 会发 WM_SETTEXT 触发重排
                //   ② SetWindowPos 每 tick 调 = 每 100ms 强制重排 z 序 → 闪；降到 1s
                //   ③ 帧没变就不 Invalidate
                var timer = new System.Windows.Forms.Timer { Interval = 100 };
                string lastTitle = "";
                int lastFrameStamp = -1, tickCount = 0;
                timer.Tick += (s, e) =>
                {
                    try
                    {
                        tickCount++;
                        string want = note.Length > 0 ? "SA 注意力 - " + note : "SA 注意力";
                        if (want != lastTitle) { lock (Sync) { form.Text = want; } lastTitle = want; }
                        if (tickCount % 10 == 0) Win.RaiseTopmost(form.Handle);   // 1s 一次足够
                        int stamp = frameStamp;
                        bool sayLive = sayText.Length > 0 && Environment.TickCount64 < sayUntil;
                        if (stamp != lastFrameStamp || sayLive || sayJustCleared)
                        {
                            lastFrameStamp = stamp;
                            sayJustCleared = sayLive;   // 记录"上一帧还在显示气泡"
                            form.Invalidate();
                        }
                    }
                    catch { }
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
        try { SayWindow.Close(); } catch { }
        var f = form;
        if (f == null) return;
        try { f.Invoke(new Action(() => { try { f.Close(); } catch { } })); } catch { }
        form = null; bar = null;
    }

    /// <summary>运行时改小窗尺寸（手机竖屏画面用竖窗，画面才不被压成细条）。</summary>
    public static bool Resize(int w, int h, int aspect = 0)
    {
        var f = form;
        if (f == null) return false;
        try
        {
            f.Invoke(new Action(() =>
            {
                try
                {
                    var b = f.Bounds;
                    // 保持左上角不动；贴住屏幕右/下边时改为反向扩展，避免跑出屏幕
                    int x = b.X, y = b.Y;
                    var scr = Screen.FromControl(f).WorkingArea;
                    if (x + w > scr.Right) x = Math.Max(scr.Left, scr.Right - w);
                    if (y + h > scr.Bottom) y = Math.Max(scr.Top, scr.Bottom - h);
                    f.Bounds = new Rectangle(x, y, w, h);
                }
                catch { }
            }));
            SyncSayWindow();
            return true;
        }
        catch { return false; }
    }

    // UI 线程：绘制最新帧 + 注意力标注 + say 气泡 + 底部信息条（全部自绘）
    // 设计语言：深蓝底 (10,16,34) / 强调青蓝 (96,196,255) / 主文字 (232,240,255) / 次要 (150,175,205)
    static void PaintFrame(Graphics g, Size client)
    {
        lock (Sync)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int vw = client.Width, vh = Math.Max(1, client.Height - BarH);

            if (frame != null && fw > 0 && fh > 0)
            {
                var bmp = BgraToBitmap(frame, fw, fh);   // 复用缓冲，不能 using（Dispose 会毁掉缓存）
                double scale = Math.Min((double)vw / fw, (double)vh / fh);
                int dw = (int)(fw * scale), dh = (int)(fh * scale);
                int dx = (vw - dw) / 2, dy = (vh - dh) / 2;
                g.DrawImage(bmp, dx, dy, dw, dh);

                // 注意力区域：四角括号（画满框会遮挡内容，括号只标边界）
                if (hasRect)
                {
                    int rx = dx + (int)((attRect.X - originX) * scale), ry = dy + (int)((attRect.Y - originY) * scale);
                    int rw = Math.Max(6, (int)(attRect.Width * scale)), rh = Math.Max(6, (int)(attRect.Height * scale));
                    using var pen = new Pen(Color.FromArgb(235, 255, 86, 96), 2f);
                    DrawCorners(g, pen, new Rectangle(rx, ry, rw, rh));
                }
                // 注视点：同心圆 + 十字
                if (hasGaze)
                {
                    int gx = dx + (int)((gazePt.X - originX) * scale), gy = dy + (int)((gazePt.Y - originY) * scale);
                    using var pen = new Pen(Color.FromArgb(235, 120, 240, 140), 1.6f);
                    g.DrawEllipse(pen, gx - 7, gy - 7, 14, 14);
                    g.DrawEllipse(pen, gx - 2, gy - 2, 4, 4);
                    g.DrawLine(pen, gx - 12, gy, gx - 9, gy);
                    g.DrawLine(pen, gx + 9, gy, gx + 12, gy);
                    g.DrawLine(pen, gx, gy - 12, gx, gy - 9);
                    g.DrawLine(pen, gx, gy + 9, gx, gy + 12);
                }
                // 气泡不再画在图像窗里 —— 文字走独立的消息窗（SayWindow），避免遮挡画面
            }
            // 讲解字幕层：叠在画面底部，半透明底 + 左侧高亮条，多行自适应
            if (!string.IsNullOrEmpty(subtitle))
            {
                var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
                int pad = 12, maxW = Math.Max(40, vw - pad * 2);
                var sz = TextRenderer.MeasureText(subtitle, subtitleFont, new Size(maxW, 300), flags);
                // 上限放到画面区 72%：字幕优先完整显示，宁可压小画面也不截断文字
                int sh = Math.Min(Math.Max(44, (int)(vh * 0.72)), Math.Max(44, sz.Height + 18));
                int sy = Math.Max(0, vh - sh);
                using (var bg = new SolidBrush(Color.FromArgb(212, 8, 12, 26)))
                    g.FillRectangle(bg, 0, sy, vw, sh);
                using (var bar = new SolidBrush(Color.FromArgb(235, 96, 196, 255)))
                    g.FillRectangle(bar, 0, sy, 3, sh);
                TextRenderer.DrawText(g, subtitle, subtitleFont,
                    new Rectangle(pad, sy + 9, maxW, sh - 18), Color.FromArgb(242, 247, 255), flags);
            }
            DrawBar(g, client.Width, vh);
            try { cachedBounds = new Rectangle(form != null ? form.Left : 0, form != null ? form.Top : 0, client.Width, client.Height); hasCachedBounds = true; } catch { }
        }
    }

    /// <summary>四角括号（长度按框大小自适应，最长 18px）。</summary>
    static void DrawCorners(Graphics g, Pen pen, Rectangle r)
    {
        int len = Math.Max(6, Math.Min(18, Math.Min(r.Width, r.Height) / 4));
        // 左上
        g.DrawLine(pen, r.Left, r.Top, r.Left + len, r.Top);
        g.DrawLine(pen, r.Left, r.Top, r.Left, r.Top + len);
        // 右上
        g.DrawLine(pen, r.Right, r.Top, r.Right - len, r.Top);
        g.DrawLine(pen, r.Right, r.Top, r.Right, r.Top + len);
        // 左下
        g.DrawLine(pen, r.Left, r.Bottom, r.Left + len, r.Bottom);
        g.DrawLine(pen, r.Left, r.Bottom, r.Left, r.Bottom - len);
        // 右下
        g.DrawLine(pen, r.Right, r.Bottom, r.Right - len, r.Bottom);
        g.DrawLine(pen, r.Right, r.Bottom, r.Right, r.Bottom - len);
    }

    public static GraphicsPath RoundRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = Math.Max(2, rad * 2);
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static readonly Font noteFont = new Font("Microsoft YaHei UI", 9.5f);
    static readonly Font metaFont = new Font("Microsoft YaHei UI", 9f);
    static readonly Font sayFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);

    /// <summary>底部信息条：左=状态圆点+注意点，右=区域尺寸+帧率（单行省略，互不挤压）。</summary>
    static void DrawBar(Graphics g, int w, int y)
    {
        int pad = 10, dot = 7, gap = 8;
        using (var bg = new SolidBrush(Color.FromArgb(240, 10, 16, 34)))
            g.FillRectangle(bg, 0, y, w, BarH);
        using (var line = new Pen(Color.FromArgb(80, 96, 196, 255), 1f))
            g.DrawLine(line, 0, y, w, y);

        // 状态圆点：绿=在动，琥珀=静止
        bool live = fps > 0.5;
        using (var b = new SolidBrush(live ? Color.FromArgb(95, 220, 130) : Color.FromArgb(225, 190, 95)))
            g.FillEllipse(b, pad, y + (BarH - dot) / 2, dot, dot);

        // 右侧指标（先量，右边优先占位）
        string right = (hasRect ? attRect.Width + "×" + attRect.Height + "   " : "") + fps.ToString("0.0") + " fps";
        var rf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;
        int rightW = TextRenderer.MeasureText(right, metaFont, new Size(w, BarH), rf).Width + 2;
        int leftX = pad + dot + gap;
        int leftMax = Math.Max(30, w - leftX - rightW - pad - gap);
        var lf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, note.Length > 0 ? note : "空闲", noteFont,
            new Rectangle(leftX, y, leftMax, BarH), Color.FromArgb(232, 240, 255), lf);
        TextRenderer.DrawText(g, right, metaFont,
            new Rectangle(w - pad - rightW, y, rightW, BarH), Color.FromArgb(150, 175, 205), rf);
    }

    // 复用同一张位图：480×270@100ms 原来每秒 new 10 张 Bitmap，GC 压力直接表现为卡顿
    static Bitmap _frameBmp;
    static int _frameBmpW, _frameBmpH;
    static Bitmap BgraToBitmap(byte[] bgra, int w, int h)
    {
        if (_frameBmp == null || _frameBmpW != w || _frameBmpH != h)
        {
            try { _frameBmp?.Dispose(); } catch { }
            _frameBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            _frameBmpW = w; _frameBmpH = h;
        }
        var data = _frameBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        _frameBmp.UnlockBits(data);
        return _frameBmp;
    }
}

// ============ 持续采集线程（AI 控频 + 变化检测 + 自动调速） ============
// ============ 单一采集流（FrameHub）：Watch/Interpret 共享一次采集 ============
static class FrameHub
{
    static Thread thread;
    static volatile bool running;
    static volatile int intervalMs = 100;
    // ==== 连续帧指标（60fps × 60s = 3600 帧，要求零空洞）====
    public static long Gaps;              // 超过 1.5×间隔的空洞次数
    public static long MaxGapMs;          // 最长空洞
    public static long StartTicks;
    public static int TargetFps = 100;
    static byte[] hubBufA, hubBufB;
    static bool hubFlip;
    static StreamWriter frameLog;
    static readonly object LogLock = new object();
    static long lastTs;
    static int motionCells;

    public static void OpenFrameLog(string path)
    {
        lock (LogLock)
        {
            try { frameLog?.Flush(); frameLog?.Dispose(); } catch { }
            frameLog = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                frameLog = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), new System.Text.UTF8Encoding(false));
                frameLog.AutoFlush = false;
            }
            catch { frameLog = null; }
        }
    }

    public static void CloseFrameLog()
    {
        lock (LogLock)
        {
            try { frameLog?.Flush(); frameLog?.Dispose(); } catch { }
            frameLog = null;
        }
    }
    public const int ActiveMs = 16;   // 画面一动立刻拉满 60fps（16.6ms/帧）
    public const int IdleMs = 220;

    // 采集间隔自适应：静止时降低全屏 GDI 抓取频率（省 CPU），
    // 但 watch / 注意力小窗需要流畅画面时保持活跃频率。
    static long lastMotionAt;
    public const int SettleMs = 1200;   // 动作结束后保持 60fps 的粘滞时间，避免抖动来回切档

    public static void SetLoad(bool idle)
    {
        try
        {
            long now = Environment.TickCount64;
            if (!idle) lastMotionAt = now;
            if (Watch.IsWatching || Overlay.IsOpen) { intervalMs = ActiveMs; return; }
            // 一旦有动作，立即拉满 60fps；静止后要静置 SettleMs 才降档（滞回）
            intervalMs = (now - lastMotionAt) < SettleMs ? ActiveMs : IdleMs;
        }
        catch { intervalMs = ActiveMs; }
    }
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
    public static void SetInterval(int ms) { intervalMs = Math.Max(4, Math.Min(2000, ms)); }

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

    public static void ResetStats()
    {
        lock (L) { frames = 0; totalCaptureMs = 0; Gaps = 0; MaxGapMs = 0; fpsFrames0 = frames; }
        StartTicks = Environment.TickCount64;
        lastTs = 0;
    }
    static long fpsFrames0;

    public static object Stats()
    {
        lock (L)
        {
            long el = Math.Max(1, Environment.TickCount64 - StartTicks);
            return new
            {
                running,
                targetFps = TargetFps,
                intervalMs,
                frames,
                seq,
                fps = Math.Round(frames * 1000.0 / el, 2),
                elapsedMs = Environment.TickCount64 - StartTicks,
                lastCaptureMs,
                avgCaptureMs = frames > 0 ? Math.Round((double)totalCaptureMs / frames, 2) : 0,
                gaps = Gaps,
                maxGapMs = MaxGapMs,
                dropped = Gaps,
                expectedFrames = (long)Math.Round(el * TargetFps / 1000.0)
            };
        }
    }

    public static void Stop()
    {
        running = false;
        lock (L) { System.Threading.Monitor.PulseAll(L); }
    }

    static void Loop()
    {
        int sw = Gdi.ScreenW(), sh = Gdi.ScreenH();
        long fpsTs = Environment.TickCount64, fpsFrames = 0;
        while (running)
        {
            var t0 = Stopwatch.StartNew();
            long dt = 0; int ms = 0;
            try
            {
                // 高速档（60fps 目标）用半分辨率采集：数据量 1/4，跳过全屏回读瓶颈；
                // 识别帧需要全分辨率时由 Infer 自己补抓。
                var (b, w, h) = intervalMs <= 20
                    ? Gdi.CaptureSmallParallel(sw, sh, 0.4)
                    : Gdi.Capture(0, 0, sw, sh);
                // 双缓冲：Capture 复用内部数组，这里拷贝到轮换缓冲，避免消费者读到撕裂帧且不产生分配
                int needBytes = b.Length;
                if (hubBufA == null || hubBufA.Length != needBytes) { hubBufA = new byte[needBytes]; hubBufB = new byte[needBytes]; }
                var dstBuf = hubFlip ? hubBufA : hubBufB;
                hubFlip = !hubFlip;
                Buffer.BlockCopy(b, 0, dstBuf, 0, needBytes);
                b = dstBuf;
                ms = (int)t0.ElapsedMilliseconds;
                long nowTicks = Environment.TickCount64;
                dt = lastTs == 0 ? 0 : nowTicks - lastTs;
                lastTs = nowTicks;
                // 空洞判定：两帧间隔超过 1.5 倍目标间隔即视为丢帧
                if (dt > 0 && TargetFps > 0)
                {
                    long limit = (long)(1500.0 / TargetFps);
                    if (dt > limit) { Gaps++; if (dt > MaxGapMs) MaxGapMs = dt; }
                }
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
            lock (LogLock)
            {
                if (frameLog != null)
                {
                    try
                    {
                        frameLog.Write("{\"s\":");
                        frameLog.Write(seq);
                        frameLog.Write(",\"t\":");
                        frameLog.Write(Environment.TickCount64 - StartTicks);
                        frameLog.Write(",\"dt\":");
                        frameLog.Write(dt);
                        frameLog.Write(",\"cap\":");
                        frameLog.Write(ms);
                        frameLog.Write("}\n");
                        if (seq % 60 == 0) frameLog.Flush();
                    }
                    catch { }
                }
            }
            int sleep = intervalMs - (int)t0.ElapsedMilliseconds;
            if (sleep > 0) Thread.Sleep(sleep);
            else if (sleep < -2) { /* 落后于目标帧率：记一次欠帧 */ }
        }
    }
}

static class Watch
{
    static Thread thread;
    static volatile bool watching;
    static volatile int curMs = 0;        // 当前实际采样间隔（自适应浮动，不是固定值）
    static volatile int loopFrames = 0;    // 循环帧计数（诊断：确认 Watch.Loop 真的在跑）
    static volatile int changeEmits = 0;   // change 事件发出次数
    static readonly object SyncLock = new object();
    public static bool IsWatching => watching;
    public static int EffectiveMs => curMs > 0 ? curMs : IntervalMs;
    public static int IntervalMs = 1000;
    static readonly int FastMs = 100;
    static volatile bool motion;
    static double motionRatio;
    static Rectangle motionRect;
    static long motionSince;
    static bool everDetected;
    static long lastChangeEventAt;
    public static string LastChangeError;   // 发事件失败留痕
    public static int LoopFrames => loopFrames;
    public static int ChangeEmits => changeEmits;
    static Rectangle focusRect = Rectangle.Empty;   // 焦点区域（空=全屏）。用于"感知对话输出"式定向观察
    static string lastOcrText = "";                 // 上次 OCR 摘要（增量 diff 基准）
    static readonly int[] LastSamples = new int[(3840 / 16 + 1) * (2160 / 16 + 1)];
    static readonly bool[] SampleValid = new bool[(3840 / 16 + 1) * (2160 / 16 + 1)];

    public static bool Motion => motion;
    public static double MotionRatio => motionRatio;
    public static Rectangle MotionRect => motionRect;

    public static void Start(int intervalMs)
    {
        IntervalMs = Math.Max(10, intervalMs);
        curMs = IntervalMs;               // 从基准起步，之后自己浮动
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
                try { Program.Event("change", new { ratio = Math.Round(ratio, 4), rect = new { x = motionRect.X, y = motionRect.Y, w = motionRect.Width, h = motionRect.Height } }); Interlocked.Increment(ref changeEmits); } catch (Exception exEv) { LastChangeError = exEv.GetType().Name + ": " + exEv.Message; }
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
                    Overlay.frameStamp = unchecked(Overlay.frameStamp + 1);   // 换帧计数：驱动按需重绘
                    long now = DateTime.UtcNow.Ticks;
                    if (lastTick > 0)
                    {
                        double gapMs = (now - lastTick) / (double)TimeSpan.TicksPerMillisecond;
                        Overlay.fps = 1000.0 / Math.Max(1, gapMs);
                    }
                    lastTick = now;
                }
                bool before = motion;
                loopFrames++;
                DetectMotion(buf, w, h);
                // 迟滞调速：连续运动→快模式；连续静止→慢模式
                if (motion) { active++; steady = 0; } else { steady++; active = 0; }
                // 自适应间隔（AIMD）：变化→乘性加速，连续静止→乘性减速。
                // 不绑死任何一个值：实测同一场景 120ms 有效、500/900ms 反而 0（内容节拍，
                // 某些间隔正好踩在同一姿态），所以固定间隔必然有"盲区"，让它自己浮动。
                const int MinMs = 120;    // 实测再快只会重复读旧帧
                const int MinMs2 = 200;   // 与 change 事件联动时的下限（防自身界面刷满 CPU）
                const int MaxMs = 1600;   // 再慢就跟不上内容切换
                // 加速判据必须与 change 事件同源，否则会出现「motion=true 却一直减速到 1600ms，
                // 于是 change 事件 4 秒才一个」的矛盾（实测踩过）。
                // motion 本身已包含 windowSum>=12 的细粒度判定，直接用它；
                // 下限提到 200ms 防 CPU（120ms 时全屏观察会被自身界面刷满）。
                if (motion) curMs = (int)Math.Max(MinMs2, curMs * 0.7);
                else if (steady >= 2) curMs = (int)Math.Min(MaxMs, curMs * 1.4);
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
    static long latestSeq;
    // ==== 帧账本：识别速率跟随实际产出帧率，不静默丢帧 ====
    static long lastRecognizeAt;
    static long ledgerStaticSkips;   // 静止时按省电策略主动跳过的帧（已记账）
    static long ledgerBusySkips;     // 识别跟不上时落后的帧（已记账）
    static long recognizeCount;      // 真正执行识别的帧数
    static long recognizeStartTicks;
    public const int RecognizeIntervalStaticMs = 250;   // 静止：4fps 兜底（活跃/全速模式下不节流）
    static volatile bool fullRateMode;                  // true=不做静止节流，每帧都识别（追 30fps）
    public static void SetFullRate(bool on) { fullRateMode = on; }
    static int latestOcrMs, latestBoxes, latestHits, latestMiss;
    static int latestObjMs, latestObjCount;
    static string latestScene; static int latestSceneMs; static List<Dictionary<string, object>> latestSceneLabels;
    static List<Dictionary<string, object>> latestFaces = new List<Dictionary<string, object>>();
    static int latestFaceMs, latestFaceCount;
    static string latestUnderstanding = "", latestFgTitle = "", prevFgTitle = "";
    static List<Dictionary<string, object>> latestObjects = new List<Dictionary<string, object>>();
    static long seen, processed, dropped, staticSkipped, staticHeartbeats, totalOcrMs;
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
                seen = processed = dropped = staticSkipped = staticHeartbeats = totalOcrMs = 0;
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

    // ===== 高速识别：ROI 增量 =====
    // 只在"画面真正变化的矩形"内做 det+rec，未变区域复用上一次的文本框。
    static byte[] _prevFrame; static int _prevW, _prevH;
    static List<Dictionary<string, object>> _prevItems = new List<Dictionary<string, object>>();
    static long roiReuse, roiPartial, roiFull;

    struct Rect { public int X, Y, W, H; }

    // 网格采样帧差 → 变化包围盒（null 表示无变化）
    static Rect? DiffRect(byte[] a, byte[] b, int w, int h)
    {
        if (a == null || _prevW != w || _prevH != h) return null;
        const int step = 16; const int th = 24;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        int stride = w * 4;
        for (int y = 0; y < h; y += step)
        {
            int rowOff = y * stride;
            for (int x = 0; x < w; x += step)
            {
                int off = rowOff + x * 4;
                if (off + 2 >= b.Length || off + 2 >= a.Length) continue;
                int db = Math.Abs(b[off] - a[off]);
                int dg = Math.Abs(b[off + 1] - a[off + 1]);
                int dr = Math.Abs(b[off + 2] - a[off + 2]);
                if (db > th || dg > th || dr > th)
                {
                    if (x < minX) minX = x; if (y < minY) minY = y;
                    if (x > maxX) maxX = x; if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < 0) return new Rect { X = 0, Y = 0, W = 0, H = 0 };  // 无变化
        return new Rect { X = minX, Y = minY, W = Math.Min(w - minX, maxX - minX + step), H = Math.Min(h - minY, maxY - minY + step) };
    }

    static byte[] CropBgra(byte[] src, int sw, int sh, Rect r)
    {
        var dst = new byte[r.W * r.H * 4];
        int sStride = sw * 4, dStride = r.W * 4;
        for (int y = 0; y < r.H; y++)
        {
            int sy = r.Y + y; if (sy < 0 || sy >= sh) continue;
            Buffer.BlockCopy(src, sy * sStride + r.X * 4, dst, y * dStride, dStride);
        }
        return dst;
    }

    // 合并：ROI 外沿用旧框，ROI 内用新框（新框坐标 + ROI 偏移）
    static List<Dictionary<string, object>> MergeItems(List<Dictionary<string, object>> prevItems, List<Dictionary<string, object>> newItems, Rect roi)
    {
        var outList = new List<Dictionary<string, object>>();
        foreach (var it in prevItems)
        {
            var rr = it["rect"] as Dictionary<string, object>;
            if (rr == null) continue;
            int cx = Convert.ToInt32(rr["x"]) + Convert.ToInt32(rr["w"]) / 2;
            int cy = Convert.ToInt32(rr["y"]) + Convert.ToInt32(rr["h"]) / 2;
            bool inside = cx >= roi.X && cx < roi.X + roi.W && cy >= roi.Y && cy < roi.Y + roi.H;
            if (!inside) outList.Add(it);
        }
        foreach (var it in newItems)
        {
            var rr = it["rect"] as Dictionary<string, object>;
            if (rr == null) continue;
            rr["x"] = Convert.ToInt32(rr["x"]) + roi.X;
            rr["y"] = Convert.ToInt32(rr["y"]) + roi.Y;
            outList.Add(it);
        }
        outList.Sort((p, q) =>
        {
            var a1 = p["rect"] as Dictionary<string, object>; var b1 = q["rect"] as Dictionary<string, object>;
            int ay = a1 != null ? Convert.ToInt32(a1["y"]) : 0; int by = b1 != null ? Convert.ToInt32(b1["y"]) : 0;
            if (ay != by) return ay - by;
            int ax = a1 != null ? Convert.ToInt32(a1["x"]) : 0; int bx = b1 != null ? Convert.ToInt32(b1["x"]) : 0;
            return ax - bx;
        });
        return outList;
    }

    // ===== 分块轮转识别：30fps 预算下，每帧只识别一块（优先变化最大的块）=====
    const int TileCols = 8, TileRows = 6;
    static readonly long[] TileScore = new long[TileCols * TileRows];
    static readonly long[] TileLastAt = new long[TileCols * TileRows];
    static int TileCursor;
    static long TileRounds;

    // 统计每块的变化采样点数（16px 网格，开销 ~1-2ms）
    static void UpdateTileScores(byte[] prev, byte[] cur, int w, int h)
    {
        for (int i = 0; i < TileScore.Length; i++) TileScore[i] = 0;
        if (prev == null) return;
        const int step = 16, th = 24;
        int stride = w * 4;
        int tw = Math.Max(1, w / TileCols), thh = Math.Max(1, h / TileRows);
        for (int y = 0; y < h; y += step)
        {
            int rowOff = y * stride;
            int ty = Math.Min(TileRows - 1, y / thh);
            for (int x = 0; x < w; x += step)
            {
                int off = rowOff + x * 4;
                if (off + 2 >= cur.Length || off + 2 >= prev.Length) continue;
                if (Math.Abs(cur[off] - prev[off]) > th || Math.Abs(cur[off + 1] - prev[off + 1]) > th || Math.Abs(cur[off + 2] - prev[off + 2]) > th)
                    TileScore[ty * TileCols + Math.Min(TileCols - 1, x / tw)]++;
            }
        }
    }

    // 选块：变化最大者优先；都无变化则轮转（保证全屏最终覆盖）
    static int PickTile(long now)
    {
        int best = -1; long bestScore = 0;
        for (int i = 0; i < TileScore.Length; i++)
        {
            if (TileScore[i] > bestScore) { bestScore = TileScore[i]; best = i; }
        }
        if (best >= 0 && bestScore > 3) return best;
        TileCursor = (TileCursor + 1) % (TileCols * TileRows);
        return TileCursor;
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
                { "framesSeen", seen }, { "framesProcessed", processed }, { "framesDropped", dropped }, { "staticSkipped", staticSkipped }, { "staticHeartbeats", staticHeartbeats },
                { "ledger", new Dictionary<string, object> {
                    { "framesConsumed", seen }, { "recognized", recognizeCount },
                    { "skippedStatic", ledgerStaticSkips }, { "skippedBusy", ledgerBusySkips },
                    { "staticRecognizeIntervalMs", RecognizeIntervalStaticMs },
                    { "recognizeHz", recognizeStartTicks > 0 ? Math.Round(recognizeCount * 1000.0 / Math.Max(1, Environment.TickCount64 - recognizeStartTicks), 2) : 0 },
                    { "ledgerBalanced", seen == recognizeCount + ledgerStaticSkips + ledgerBusySkips },
                    { "mode", isMotionActive ? "active" : "static" }, { "fullRate", fullRateMode },
                    { "roiReuse", roiReuse }, { "roiPartial", roiPartial }, { "roiFull", roiFull },
                    { "tiles", TileCols * TileRows }, { "tileRounds", TileRounds } } },
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
            long nowTick = Environment.TickCount64;
            if (isStatic)
            {
                staticCount++;
                // 自适应识别节奏：静止时只保证 1fps 识别（能力有限就按实际速度，不硬撑），
                // 但采集继续运行，被跳过的帧全部记账，信息链不断裂。
                if (!fullRateMode && (nowTick - lastRecognizeAt) < RecognizeIntervalStaticMs)
                {
                    FrameHub.SetLoad(true);
                    lock (L)
                    {
                        seen += skipped + 1;
                        staticSkipped++;
                        ledgerStaticSkips += skipped + 1;
                        latestSeq = seq;
                        long nowSkip = Environment.TickCount64;
                        if (nowSkip - fpsTs >= 1000) { fps = fpsCount * 1000.0 / Math.Max(1, nowSkip - fpsTs); fpsTs = nowSkip; fpsCount = 0; }
                    }
                    continue;
                }
            }
            else
            {
                staticCount = 0;      // 活跃：跟随全量帧，不节流
            }
            lastRecognizeAt = nowTick;
            FrameHub.SetLoad(isStatic);

            // 高效利用硬件：走到这里的静止帧是「心跳帧」——只做小预算 OCR 维持最新文本，
            // 跳过 YOLO / CLIP 场景 / 动漫脸等昂贵推理，避免静止画面空烧 GPU。
            bool heartbeat = isStatic;
            if (heartbeat) staticHeartbeats++;
            int ocrBudget = heartbeat ? System.Math.Min(12, maxBoxes > 0 ? maxBoxes : 12) : maxBoxes;

            var t0 = Stopwatch.StartNew();
            try
            {
                // 高速档帧是半分辨率（用于连续性/变化检测）；识别需要全分辨率细节，这里补抓一次并本地化，
                // 避免共享缓冲被采集线程覆盖。
                int fullW = Gdi.ScreenW(), fullH = Gdi.ScreenH();
                if (w != fullW || h != fullH)
                {
                    var (fb, fw2, fh2) = Gdi.Capture(0, 0, fullW, fullH);
                    var local = new byte[fb.Length];
                    Buffer.BlockCopy(fb, 0, local, 0, fb.Length);
                    frame = local; w = fw2; h = fh2;
                }
                // ==== 高速识别：ROI 增量（det 是主要开销，只对变化区域跑）====
                Dictionary<string, object> res = null;
                List<Dictionary<string, object>> items;
                int hits = 0, miss = 0;
                UpdateTileScores(_prevFrame, frame, w, h);
                int tileIdx = PickTile(Environment.TickCount64);
                int tIdx = Math.Min(TileRows - 1, tileIdx / TileCols);
                int tIdxX = tileIdx % TileCols;
                int tw = Math.Max(1, w / TileCols), thh = Math.Max(1, h / TileRows);
                var rr = new Rect { X = tIdxX * tw, Y = tIdx * thh, W = Math.Min(tw, w - tIdxX * tw), H = Math.Min(thh, h - tIdx * thh) };
                const int tpad = 8;
                rr.X = Math.Max(0, rr.X - tpad); rr.Y = Math.Max(0, rr.Y - tpad);
                rr.W = Math.Min(w - rr.X, rr.W + tpad * 2);
                rr.H = Math.Min(h - rr.Y, rr.H + tpad * 2);
                {
                    // 固定 320x320 输入：DirectML 对动态 shape 每帧重建 kernel，是当前最大的固定开销。
                    // 统一尺寸后 kernel 复用，单帧成本显著下降。
                    const int FIXW = 320, FIXH = 320;
                    int cw = Math.Min(rr.W, FIXW), chh = Math.Min(rr.H, FIXH);
                    var padded = new byte[FIXW * FIXH * 4];
                    int srcStride = w * 4, dstStride = FIXW * 4;
                    for (int yy = 0; yy < chh; yy++)
                    {
                        int sy = rr.Y + yy;
                        if (sy < 0 || sy >= h) break;
                        Buffer.BlockCopy(frame, sy * srcStride + rr.X * 4, padded, yy * dstStride, cw * 4);
                    }
                    bool skipDet = (frameNo % 10) != 0;     // 每 10 帧重检一次，其余帧只跑 rec（det 是大头）
                    res = PaddleOcr.RunStreaming(padded, FIXW, FIXH, ocrBudget, skipDet);
                    var subItems = (res.TryGetValue("items", out var iv2) && iv2 is List<Dictionary<string, object>> l2) ? l2 : new List<Dictionary<string, object>>();
                    items = MergeItems(_prevItems, subItems, rr);   // 只替换该块区域内的框，其余沿用
                    TileLastAt[tileIdx] = Environment.TickCount64;
                    roiPartial++;
                }
                if (TileScore[tileIdx] == 0) roiReuse++;
                if (++TileCursor >= TileScore.Length) TileCursor = 0;
                TileRounds++;
                if (res != null)
                {
                    hits = res.TryGetValue("recHits", out var hv) ? Convert.ToInt32(hv) : 0;
                    miss = res.TryGetValue("recMiss", out var mv2) ? Convert.ToInt32(mv2) : 0;
                }
                // 保存本次全屏帧供下次差分（frame 可能是复用缓冲，必须拷贝）
                if (_prevFrame == null || _prevW != w || _prevH != h) { _prevFrame = new byte[w * h * 4]; _prevW = w; _prevH = h; }
                Buffer.BlockCopy(frame, 0, _prevFrame, 0, Math.Min(frame.Length, _prevFrame.Length));
                _prevItems = items;
                int ocrMs = (int)t0.ElapsedMilliseconds;
                var texts = items.Select(t => t.TryGetValue("text", out var tv) ? Convert.ToString(tv) : "").Where(t => !string.IsNullOrEmpty(t)).ToList();
                string joined = string.Join(" | ", texts);
                if (joined.Length > 1200) joined = joined.Substring(0, 1200);
                frameNo++;
                string sceneTop = null; int sceneMs = 0; List<Dictionary<string, object>> sceneLabels = null;
                bool ranScene = false;
                if ((!heartbeat || string.IsNullOrEmpty(latestScene)) && sceneMode && ClipScene.IsReady && (frameNo == 1 || frameNo % sceneEvery == 0 || string.IsNullOrEmpty(latestScene)))
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
                if (!heartbeat && facesMode && (frameNo == 1 || frameNo % faceEvery == 0))
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
                if (objects && !heartbeat)
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
                    ledgerBusySkips += Math.Max(0, skipped); recognizeCount++;
                    if (recognizeStartTicks == 0) recognizeStartTicks = Environment.TickCount64;
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

// ============ 预热状态（供 health 观测） ============
static class WarmState
{
    public static volatile bool Warming;
    public static volatile bool Done;
    public static long OcrMs, YoloMs, FaceMs, ClipMs, CommandMs;
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

    static void WriteLine(string json)
    {
        lock (Wl)
        {
            // stdout 同样可能被父进程关掉（10054）——写失败只说明没人接了，
            // 绝不能让它变成未捕获异常把进程带走。
            try { Console.Out.WriteLine(json); Console.Out.Flush(); }
            catch { }
        }
    }
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
            case "window_capture":
                {
                    long hwndL = req.TryGetProperty("hwnd", out var pwh) ? pwh.GetInt64() : 0;
                    string tp = req.TryGetProperty("title", out var pwt) ? (pwt.GetString() ?? "") : "";
                    int maxW = req.TryGetProperty("maxW", out var pmw) ? pmw.GetInt32() : 1260;
                    IntPtr hw = hwndL > 0 ? new IntPtr(hwndL) : (tp.Length > 0 ? new IntPtr(Win.FindWindowByTitle(tp)) : IntPtr.Zero);
                    if (hw == IntPtr.Zero) return new { ok = false, error = "找不到窗口" };

                    var sbT = new System.Text.StringBuilder(512); Win.GetWindowText(hw, sbT, 512);
                    bool wasMin = Win.IsIconic(hw);
                    // 最小化窗口 PrintWindow 拿不到画面：先"显示但不激活"，抓完再最小化回去
                    if (wasMin) { Win.ShowWindow(hw, 4 /*SW_SHOWNOACTIVATE*/); Thread.Sleep(160); }

                    var (buf, w, hh, okg) = Gdi.PrintWindowCapture(hw);
                    if (wasMin) { Win.ShowWindow(hw, 6 /*SW_MINIMIZE*/); }

                    if (!okg || buf == null) return new { ok = false, error = "PrintWindow 失败（该窗口可能禁止捕获）", title = sbT.ToString() };

                    // 全黑检测：最小化的 DWM 窗口或受保护内容会返回全黑
                    int sum = 0;
                    for (int i = 0; i < buf.Length; i += 4 * 97) sum += buf[i];
                    bool blank = sum / Math.Max(1, (buf.Length / (4 * 97))) < 3;

                    int tw = Math.Min(w, maxW);
                    int th = Math.Max(1, (int)Math.Round(hh * (double)tw / w));
                    byte[] use = buf;
                    if (tw < w) use = Gdi.ScaleBuffer(buf, w, hh, tw, th);
                    string tmp = Path.Combine(Path.GetTempPath(), "sa-win-" + Guid.NewGuid().ToString("N") + ".jpg");
                    var jpg = GdiPlus.EncodeJpeg(use, tw, th, tmp);
                    return new
                    {
                        ok = true, title = sbT.ToString(), hwnd = (long)hw.ToInt64(),
                        w = tw, h = th, srcW = w, srcH = hh,
                        wasMinimized = wasMin, blank,
                        blankHint = blank ? "抓到全黑：窗口最小化或属于受保护内容，先恢复窗口再抓" : null,
                        jpegB64 = Convert.ToBase64String(jpg), bytes = jpg.Length,
                    };
                }
            case "window_rect":
                {
                    long hwndL = req.TryGetProperty("hwnd", out var phw) ? phw.GetInt64() : 0;
                    string titlePart = req.TryGetProperty("title", out var ptt2) ? (ptt2.GetString() ?? "") : "";
                    IntPtr hw = hwndL > 0 ? new IntPtr(hwndL) : IntPtr.Zero;
                    if (hw == IntPtr.Zero)
                    {
                        if (titlePart.Length == 0) return new { ok = false, error = "需要 hwnd 或 title" };
                        hw = new IntPtr(Win.FindWindowByTitle(titlePart));
                        if (hw == IntPtr.Zero) return new { ok = false, error = "找不到标题包含「" + titlePart + "」的窗口" };
                    }
                    bool minimized = Win.IsIconic(hw);
                    var cr = Win.ClientRect(hw);
                    Win.GetWindowRect(hw, out var wr);
                    var sbT = new System.Text.StringBuilder(512); Win.GetWindowText(hw, sbT, 512);
                    if (cr == null)
                        return new { ok = false, hwnd = (long)hw.ToInt64(), title = sbT.ToString(), minimized,
                                     error = minimized ? "窗口已最小化，没有可见客户区" : "取不到客户区矩形" };
                    var c = cr.Value;
                    return new
                    {
                        ok = true, hwnd = (long)hw.ToInt64(), title = sbT.ToString(), minimized,
                        client = new { x = c.x, y = c.y, w = c.w, h = c.h },
                        window = new { x = wr.L, y = wr.T, w = wr.R - wr.L, h = wr.B - wr.T },
                    };
                }
            case "say":
                {
                    string text = req.TryGetProperty("text", out var ptx) ? (ptx.GetString() ?? "") : "";
                    if (text.Trim().Length == 0) return new { ok = false, error = "text 为空" };
                    bool speak = !(req.TryGetProperty("speak", out var psk) && psk.GetBoolean() == false);
                    int ms = Math.Clamp(req.TryGetProperty("ms", out var pms3) ? pms3.GetInt32() : 5000, 800, 60000);
                    int rate = req.TryGetProperty("rate", out var prt) ? prt.GetInt32() : 0;
                    Overlay.Say(text, ms);
                    var (ok2, err2) = (true, (string)null);
                    if (speak) (ok2, err2) = Tts.Speak(text, rate);
                    return new
                    {
                        ok = true,
                        imageOpen = Overlay.IsOpen,
                        sayOpen = SayWindow.IsOpen,
                        sayVisible = SayWindow.VisibleNow,
                        sayRealBounds = SayWindow.Bounds,
                        sayError = SayWindow.LastError,
                        sayTarget = SayWindow.LastTarget,
                        sayBounds = SayWindow.Bounds,
                        anchor = Overlay.CurrentBounds(),
                        spoke = speak, spokeOk = ok2, spokeError = err2, ms,
                    };
                }
            case "speech_voices": return new { voices = Tts.Voices() };
            case "audio_level":
                {
                    int ms = Math.Clamp(req.TryGetProperty("ms", out var pms2) ? pms2.GetInt32() : 600, 80, 5000);
                    string src = req.TryGetProperty("source", out var psrc) ? (psrc.GetString() ?? "device") : "device";
                    object Pack(AudioCapture.Level lv) => new
                    {
                        source = lv.Source,
                        ok = lv.Ok, error = lv.Error,
                        deviceName = lv.DeviceName,
                        rms = Math.Round(lv.Rms, 4), peak = Math.Round(lv.Peak, 4),
                        db = Math.Round(lv.Db, 1), zcr = Math.Round(lv.Zcr, 4),
                        silent = lv.Silent, voice = lv.SoundsLikeVoice,
                        samples = lv.Samples, channels = lv.Channels, sampleRate = lv.SampleRate,
                    };
                    if (src == "both")
                    {
                        // 两路是不同设备，并行采集互不干扰
                        AudioCapture.Level dev = null, mic = null;
                        var t1 = System.Threading.Tasks.Task.Run(() => dev = AudioCapture.Measure(ms, true));
                        var t2 = System.Threading.Tasks.Task.Run(() => mic = AudioCapture.Measure(ms, false));
                        System.Threading.Tasks.Task.WaitAll(t1, t2);
                        bool devAudible = dev != null && dev.Ok && !dev.Silent;
                        bool micAudible = mic != null && mic.Ok && !mic.Silent;
                        string verdict = !devAudible && !micAudible ? "两路都安静"
                            : devAudible && micAudible ? "设备在放，麦克风也收到声音"
                            : devAudible ? "只有设备在放（对面/视频的声音）"
                            : "只有麦克风有声音（你这边在说话）";
                        return new { mode = "both", verdict, device = Pack(dev), mic = Pack(mic) };
                    }
                    var lv = AudioCapture.Measure(ms, src != "mic");
                    return Pack(lv);
                }
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

            case "calibrate":
                {
                    // 自动找"变化最优"的采样间隔：屏幕合成有更新粒度，抓太快只是重复读旧内容。
                    // 做法：每个候选间隔采 N 帧算 score，用「平均分 × 变化率」作为有效度，取最高。
                    int vx = Gdi.GetSystemMetrics(76), vy = Gdi.GetSystemMetrics(77);
                    int vw = Gdi.GetSystemMetrics(78), vhh = Gdi.GetSystemMetrics(79);
                    int cx = req.TryGetProperty("x", out var pcx) ? pcx.GetInt32() : vx;
                    int cy = req.TryGetProperty("y", out var pcy) ? pcy.GetInt32() : vy;
                    int cw = req.TryGetProperty("w", out var pcw) ? pcw.GetInt32() : vw;
                    int chh = req.TryGetProperty("h", out var pch) ? pch.GetInt32() : vhh;
                    int per = Math.Clamp(req.TryGetProperty("frames", out var pcf) ? pcf.GetInt32() : 4, 2, 10);
                    int[] cand = { 120, 250, 500, 900, 1500, 2500 };
                    int maxCand = Math.Clamp(req.TryGetProperty("candidates", out var pcc) ? pcc.GetInt32() : 6, 2, 6);
                    double scale = cw > 900 ? 900.0 / cw : 1.0;

                    var rows = new List<object>();
                    int bestIv = 900; double bestEff = -1;
                    int fastIv = 0;   // 变化率够用的最小间隔（响应优先）
                    int fastRate = 0;
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    for (int ci = 0; ci < maxCand; ci++)
                    {
                        int iv = cand[ci];
                        var scorer = new Cuts.Scorer();
                        double sum = 0; int scored = 0, changes = 0; double maxS = 0;
                        for (int i = 0; i < per; i++)
                        {
                            var (b, bw, bh) = Gdi.CaptureScaled(cx, cy, cw, chh, scale);
                            var (sc, mc) = scorer.Push(b, bw, bh);
                            if (sc >= 0) { sum += sc; scored++; if (sc >= 6) changes++; if (sc > maxS) maxS = sc; }
                            if (i < per - 1) Thread.Sleep(iv);
                        }
                        double avg = scored > 0 ? sum / scored : 0;
                        double rate = scored > 0 ? (double)changes / scored : 0;
                        // 有效度：既要持续有变化（rate），又要有足够幅度（avg）；两者相乘，任一为 0 则无效
                        double eff = avg * rate;
                        rows.Add(new
                        {
                            intervalMs = iv, avgScore = Math.Round(avg, 2), maxScore = Math.Round(maxS, 2),
                            changeRate = Math.Round(rate, 2), effective = Math.Round(eff, 2),
                        });
                        if (eff > bestEff) { bestEff = eff; bestIv = iv; }
                        // 响应优先：变化率 ≥0.34 就算"抓得住"，取其中最小的间隔（跟手且有效）
                        if (rate >= 0.34 && fastIv == 0) { fastIv = iv; fastRate = (int)Math.Round(rate * 100); }
                    }
                    // 注意：间隔与变化率不是单调关系——实测 900ms 变化率 0 而 500ms 有 0.33
                    // （内容节拍不均匀，某些间隔正好踩在同一姿态）。所以要么响应优先取最小有效间隔，
                    // 要么退回有效度最高。单次标定有噪声，要稳就多跑几轮取众数。
                    return new
                    {
                        bestIntervalMs = fastIv > 0 ? fastIv : bestIv,
                        bestEffective = Math.Round(bestEff, 2),
                        responsiveIntervalMs = fastIv > 0 ? fastIv : 0,
                        responsiveRate = fastRate / 100.0,
                        maxEffectiveIntervalMs = bestIv,
                        rect = new { x = cx, y = cy, w = cw, h = chh },
                        framesPerCandidate = per,
                        elapsedMs = sw2.ElapsedMilliseconds,
                        candidates = rows,
                        hint = bestEff <= 0
                            ? "所有候选间隔都没测到变化：目标区域可能静止，或被别的窗口盖住（先用 saperm_view 确认抓到的内容）"
                            : "推荐用 bestIntervalMs；effective=平均分×变化率，0 表示该间隔下根本抓不到变化",
                    };
                }
            case "sheet":
                {
                    // 多帧拼图：**按画面变化量选帧**，不是等间隔硬抓。
                    // 判据两条（缺一不可）：
                    //   切点 = 分够大 且 是尖峰 且 运动补偿后确实变了 且 距上一刀够久
                    //   积分 = 分不高但一直在动，攒够量才留（一镜到底没有尖峰，只能靠这个）
                    // 静止画面天然只出一张 —— 这就是"静止几乎不花钱"的来源。
                    int frames = Math.Clamp(req.TryGetProperty("frames", out var pf) ? pf.GetInt32() : 4, 1, 12);
                    int maxW = req.TryGetProperty("maxW", out var pmw) ? pmw.GetInt32() : 1260;
                    int intervalMs = Math.Clamp(req.TryGetProperty("intervalMs", out var pim) ? pim.GetInt32() : 120, 40, 5000);
                    int gap = Math.Clamp(req.TryGetProperty("gap", out var pg) ? pg.GetInt32() : 2, 0, 12);
                    bool pick = !(req.TryGetProperty("select", out var psel) && psel.GetBoolean() == false);
                    int budgetMs = Math.Clamp(req.TryGetProperty("maxMs", out var pms) ? pms.GetInt32() : Math.Max(1200, frames * intervalMs * 3), 300, 20000);

                    int vx2 = Gdi.GetSystemMetrics(76), vy2 = Gdi.GetSystemMetrics(77);
                    int vw2 = Gdi.GetSystemMetrics(78), vh2 = Gdi.GetSystemMetrics(79);
                    int sx = req.TryGetProperty("x", out var psx) ? psx.GetInt32() : vx2;
                    int sy = req.TryGetProperty("y", out var psy) ? psy.GetInt32() : vy2;
                    int sw2 = req.TryGetProperty("w", out var psw) ? psw.GetInt32() : vw2;
                    int sh2 = req.TryGetProperty("h", out var psh) ? psh.GetInt32() : vh2;
                    if (sw2 <= 0) sw2 = 1;
                    if (sh2 <= 0) sh2 = 1;

                    // 先按目标帧数算格子尺寸（用最大帧数定格子，实际帧少时格子不会被撑大）
                    int cols = frames <= 1 ? 1 : frames == 2 ? 2 : frames <= 4 ? 2 : frames <= 6 ? 3 : 4;
                    // 手机适配：竖屏画面（9:16）按横屏排法会把画布撑成好几千像素高，字反而更糊。
                    // 改成一行并肩铺开 —— 总高就是单格高度，对比也更直观。
                    bool portrait = (double)sh2 / sw2 > 1.2;
                    if (portrait && frames >= 3) cols = frames;
                    int rowsMax = (int)Math.Ceiling((double)frames / cols);
                    int cellW = Math.Max(1, (maxW - gap * (cols - 1)) / cols);
                    int cellH = Math.Max(1, (int)Math.Round(cellW * (double)sh2 / sw2));
                    // 兜底：总高超过 maxH 就缩格子（竖屏极端比例保护）
                    int maxH = Math.Clamp(req.TryGetProperty("maxH", out var pmh2) ? pmh2.GetInt32() : 1600, 200, 4000);
                    if (cellH * rowsMax > maxH)
                    {
                        cellH = Math.Max(1, maxH / rowsMax);
                        cellW = Math.Max(1, (int)Math.Round(cellH * (double)sw2 / sh2));
                    }
                    double fscale = (double)cellW / sw2;
                    // 打分必须比格子更细：按 cellW 尺度抓会把视频里的角色动作平均掉
                    // （实测全屏 2560 抓 418 宽 → 视频动作 score 恒为 0；提到 900 宽后正常）
                    double pickScale = Math.Min(1.0, Math.Max(900.0, cellW) / sw2);

                    var picked = new List<(byte[] buf, int w, int h, int at, double score)>();
                    var swatch = System.Diagnostics.Stopwatch.StartNew();
                    var scorer = new Cuts.Scorer();

                    if (pick)
                    {
                        // 积分门槛：4 分 × fps × 3 秒
                        double fps = 1000.0 / intervalMs;
                        double need = 4.0 * fps * 3.0;
                        double accum = 0, prevScore = -1, lastCutAt = -999;
                        long deadline = budgetMs;
                        while (picked.Count < frames && swatch.ElapsedMilliseconds < deadline)
                        {
                            long tStart = swatch.ElapsedMilliseconds;
                            var (fb, fw, fh) = Gdi.CaptureScaled(sx, sy, sw2, sh2, pickScale);
                            var (score, mc) = scorer.Push(fb, fw, fh);
                            double t = swatch.ElapsedMilliseconds / 1000.0;

                            if (score < 0)   // 首帧必留
                            {
                                picked.Add((Gdi.ScaleBuffer(fb, fw, fh, cellW, cellH), cellW, cellH, (int)swatch.ElapsedMilliseconds, 0));
                            }
                            else
                            {
                                bool isCut = score >= 12 && score > 2.5 * Math.Max(1.0, prevScore)
                                             && mc >= 20 && (t - lastCutAt) >= 0.4;
                                accum += Math.Max(0, score);
                                bool isBurst = accum >= need;
                                if (isCut || isBurst)
                                {
                                    picked.Add((Gdi.ScaleBuffer(fb, fw, fh, cellW, cellH), cellW, cellH, (int)swatch.ElapsedMilliseconds, Math.Round(score, 1)));
                                    if (isCut) lastCutAt = t;
                                    accum = 0;
                                }
                                prevScore = score;
                            }
                            int remain = intervalMs - (int)(swatch.ElapsedMilliseconds - tStart);
                            if (remain > 0) Thread.Sleep(remain);
                        }
                        // 静止兜底：一张都没攒到（或只有首帧）也返回，标记 static
                    }
                    else
                    {
                        for (int i = 0; i < frames; i++)
                        {
                            long tStart = swatch.ElapsedMilliseconds;
                            var (fb, fw, fh) = Gdi.CaptureScaled(sx, sy, sw2, sh2, fscale);
                            scorer.Push(fb, fw, fh);
                            picked.Add((fb, fw, fh, (int)swatch.ElapsedMilliseconds, 0));
                            int remain = intervalMs - (int)(swatch.ElapsedMilliseconds - tStart);
                            if (remain > 0) Thread.Sleep(remain);
                        }
                    }

                    int n = Math.Max(1, picked.Count);
                    // 实际帧数决定行数（静止时可能只有 1 张）
                    int useCols = Math.Min(cols, n);
                    int useRows = (int)Math.Ceiling((double)n / useCols);
                    int canvasW = cellW * useCols + gap * (useCols - 1);
                    int canvasH = cellH * useRows + gap * (useRows - 1);
                    byte[] canvas = new byte[canvasW * canvasH * 4];
                    for (int i = 0; i < canvas.Length; i += 4) { canvas[i] = 40; canvas[i + 1] = 40; canvas[i + 2] = 40; canvas[i + 3] = 255; }

                    var stamps = new List<object>();
                    for (int i = 0; i < n; i++)
                    {
                        int col = i % useCols, row = i / useCols;
                        int ox = col * (cellW + gap), oy = row * (cellH + gap);
                        var (fb, fw, fh, atMs, sc) = picked[i];
                        int copyW = Math.Min(fw, cellW), copyH = Math.Min(fh, cellH);
                        for (int ry = 0; ry < copyH; ry++)
                        {
                            int srcOff = ry * fw * 4;
                            int dstOff = ((oy + ry) * canvasW + ox) * 4;
                            Buffer.BlockCopy(fb, srcOff, canvas, dstOff, copyW * 4);
                        }
                        DrawIndex(canvas, canvasW, canvasH, ox + 6, oy + 6, i + 1, 2);
                        stamps.Add(new { i, row, col, atMs, score = sc });
                    }

                    string tmpDir2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
                    Directory.CreateDirectory(tmpDir2);
                    string tmp2 = Path.Combine(tmpDir2, "sa-sheet-" + Guid.NewGuid().ToString("N") + ".jpg");
                    var jpg2 = GdiPlus.EncodeJpeg(canvas, canvasW, canvasH, tmp2);
                    return new
                    {
                        frames = n, requested = frames, cols = useCols, rows = useRows,
                        cellW, cellH, canvasW, canvasH, gap,
                        selected = pick, elapsedMs = swatch.ElapsedMilliseconds,
                        isStatic = pick && n <= 1,
                        rect = new { x = sx, y = sy, w = sw2, h = sh2 },
                        cells = stamps,
                        jpegB64 = Convert.ToBase64String(jpg2),
                        bytes = jpg2.Length,
                    };
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
                    int holdMs = req.TryGetProperty("holdMs", out var phm) ? phm.GetInt32() : 0;
                    var swc = System.Diagnostics.Stopwatch.StartNew();
                    Win.MouseButton(down, up, 1, holdMs);
                    long clickMs = swc.ElapsedMilliseconds;
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
                    return new { done = true, x, y, holdMs = holdMs > 0 ? holdMs : 25, clickMs, verified = diffRatio >= 0.005, diffRatio = Math.Round(diffRatio, 4) };
                }
            case "press":
                {
                    // 长按：target=mouse（默认）或 key。按下 → 保持 holdMs → 抬起。
                    GuardInject();
                    string target = req.TryGetProperty("target", out var ptg) ? ptg.GetString() : "mouse";
                    int hold = req.TryGetProperty("holdMs", out var ph) ? ph.GetInt32() : 500;
                    hold = Math.Clamp(hold, 10, 10000);
                    var swp = System.Diagnostics.Stopwatch.StartNew();
                    if (target == "key")
                    {
                        string kname = req.GetProperty("key").GetString();
                        ushort vk = Win.VkOf(kname);
                        Win.KeyDown(vk);
                        Thread.Sleep(hold);
                        Win.KeyUp(vk);
                        swp.Stop();
                        return new { done = true, target, key = kname, holdMs = hold, elapsedMs = swp.ElapsedMilliseconds };
                    }
                    int mx = req.GetProperty("x").GetInt32(), my = req.GetProperty("y").GetInt32();
                    string btn = req.TryGetProperty("button", out var pb) ? pb.GetString() : "left";
                    int sw2 = Gdi.ScreenW(), sh2 = Gdi.ScreenH();
                    mx = Math.Max(0, Math.Min(sw2 - 1, mx)); my = Math.Max(0, Math.Min(sh2 - 1, my));
                    Win.SetCursorPos(mx, my);
                    Thread.Sleep(30);
                    uint mdown = btn == "right" ? 0x0008u : btn == "middle" ? 0x0020u : 0x0002u;
                    uint mup = btn == "right" ? 0x0010u : btn == "middle" ? 0x0040u : 0x0004u;
                    Win.MouseDown(mdown);
                    Thread.Sleep(hold);
                    Win.MouseUp(mup);
                    swp.Stop();
                    return new { done = true, target = "mouse", x = mx, y = my, button = btn, holdMs = hold, elapsedMs = swp.ElapsedMilliseconds };
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
            case "topmost":
                {
                    // 浏览器被最大化窗口/置顶窗口遮挡时，临时把目标窗口抬到最上层，
                    // 保证截图截到的是它，而不是盖在上面的其它窗口。
                    long th = req.GetProperty("hwnd").GetInt64();
                    bool on = !req.TryGetProperty("on", out var pon) || pon.GetBoolean();
                    var hw = new IntPtr(th);
                    bool ok = Win.SetWindowPos(hw, on ? Win.HWND_TOPMOST : Win.HWND_NOTOPMOST, 0, 0, 0, 0,
                        Win.SWP_NOMOVE | Win.SWP_NOSIZE | Win.SWP_SHOWWINDOW);
                    if (on)
                    {
                        Win.FocusWindow(th);
                        Thread.Sleep(220);
                    }
                    return new { done = true, topmost = on, ok, hwnd = th };
                }
            case "frames_start":
                {
                    int fps = req.TryGetProperty("fps", out var pf) ? pf.GetInt32() : 60;
                    if (fps < 1) fps = 1;
                    if (fps > 240) fps = 240;
                    int iv = Math.Max(4, (int)Math.Round(1000.0 / fps));
                    FrameHub.TargetFps = fps;
                    string lp = req.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                    if (!string.IsNullOrEmpty(lp)) FrameHub.OpenFrameLog(lp);
                    FrameHub.ResetStats();
                    FrameHub.SetInterval(iv);
                    FrameHub.Acquire();
                    return new { running = true, targetFps = fps, intervalMs = iv, logPath = lp ?? "" };
                }
            case "frames_stats":
                return FrameHub.Stats();
            case "frames_stop":
                {
                    var st = FrameHub.Stats();
                    FrameHub.CloseFrameLog();
                    FrameHub.Stop();
                    return st;
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
            case "overlay_resize":
                {
                    int nw = Math.Clamp(req.TryGetProperty("w", out var pw2) ? pw2.GetInt32() : 480, 120, 2000);
                    int nh = Math.Clamp(req.TryGetProperty("h", out var ph2) ? ph2.GetInt32() : 270, 120, 2000);
                    int force = req.TryGetProperty("aspect", out var pas) ? pas.GetInt32() : 0;  // 1=横 2=竖 3=方
                    bool ok2 = Overlay.Resize(nw, nh, force);
                    return new { resized = ok2, w = nw, h = nh, aspect = force == 1 ? "landscape" : force == 2 ? "portrait" : force == 3 ? "square" : "custom" };
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
                        if (req.TryGetProperty("subtitle", out var sb))
                        {
                            Overlay.subtitle = sb.GetString() ?? "";
                            Overlay.subtitleAt = DateTime.UtcNow.Ticks;
                            try { Overlay.form?.Invalidate(); } catch { }
                        }
                    }
                    return new { hasRect = Overlay.hasRect, hasGaze = Overlay.hasGaze, note = Overlay.note, subtitle = Overlay.subtitle, subtitleLen = (Overlay.subtitle ?? "").Length };
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
                        effectiveMs = Watch.EffectiveMs,   // 自适应后的实际间隔（自己浮动，不是固定值）
                        loopFrames = Watch.LoopFrames,
                        changeEmits = Watch.ChangeEmits,
                        lastChangeError = Watch.LastChangeError,
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
                        warming = WarmState.Warming,
                        warmDone = WarmState.Done,
                        warmMs = new { ocr = WarmState.OcrMs, yolo = WarmState.YoloMs, face = WarmState.FaceMs, clip = WarmState.ClipMs, cmd = WarmState.CommandMs },
                        sessionId = Process.GetCurrentProcess().SessionId,
                    };
                }
            case "profile":
                {
                    // 环境探测：决定「本地识别」与「视觉直传」两条通道各自能否跑、推荐哪条。
                    // 决策只看三件真事：可用执行器、模型文件、CPU 规模。
                    string md = PaddleOcr.ModelDir();
                    var eps = Providers.Available;
                    bool hasDml = eps.Any(e => e.IndexOf("Dml", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool hasCuda = eps.Any(e => e.IndexOf("Cuda", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool hasTrt = eps.Any(e => e.IndexOf("Tensor", StringComparison.OrdinalIgnoreCase) >= 0);
                    bool ocrOk = File.Exists(Path.Combine(md, "ch_PP-OCRv4_det_infer.onnx")) && File.Exists(Path.Combine(md, "ch_PP-OCRv4_rec_infer.onnx"));
                    bool detOk = File.Exists(Path.Combine(md, "yolov8m.onnx")) || File.Exists(Path.Combine(md, "yolov8n.onnx"));
                    bool faceOk = File.Exists(Path.Combine(md, "animeface", "model.onnx"));
                    string clipLarge = Path.Combine(md, "chinese-clip-large", "image_encoder.onnx");
                    string clipBase = Path.Combine(md, "chinese-clip", "image_encoder.onnx");
                    string clip = File.Exists(clipLarge) ? "chinese-clip-large" : (File.Exists(clipBase) ? "chinese-clip" : null);
                    int cores = Environment.ProcessorCount;
                    long avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                    long availMb = avail > 0 ? avail / 1048576 : 0;
                    bool gpu = hasDml || hasCuda || hasTrt;
                    string epName = hasTrt ? "TensorRT" : (hasCuda ? "CUDA" : (hasDml ? "DirectML" : "CPU"));
                    // 本地识别可行性：OCR+检测必须有；没有 GPU 时至少要 8 线程才跑得动
                    bool localOk = ocrOk && detOk && (gpu || cores >= 8);
                    var reasons = new List<string>();
                    reasons.Add(gpu
                        ? epName + " 执行器可用 → 本地推理走 GPU（零 token）"
                        : "无 GPU 执行器，本地推理只走 CPU（" + cores + " 线程）");
                    if (!ocrOk) reasons.Add("OCR 模型缺失（ch_PP-OCRv4 det/rec）→ 文字识别不可用");
                    if (!detOk) reasons.Add("检测模型缺失（yolov8m / yolov8n）→ 物体检测不可用");
                    if (clip == null) reasons.Add("CLIP 模型缺失 → 场景与角色识别不可用");
                    if (!gpu && cores < 8) reasons.Add("CPU 线程数 < 8 且无 GPU → 本地逐帧识别过慢，建议走视觉直传");
                    if (localOk) reasons.Add("视觉直传每张图约 590 token（1260×788，42px 对齐后）");
                    return new
                    {
                        recommended = localOk ? (gpu ? "hybrid" : "local") : "vision",
                        modes = new
                        {
                            local = new { ok = localOk, cost = "0 token", speed = gpu ? "GPU 16Hz 级" : "CPU 低帧" },
                            vision = new { ok = true, cost = "~590 token/张", speed = "取决于模型服务" },
                            hybrid = new { ok = localOk, cost = "~590 token/张 + 0", speed = "识别本地 + 原貌直传" },
                        },
                        env = new { providers = eps, gpu, backend = PaddleOcr.Backend, cores, availMemMB = availMb, session = Process.GetCurrentProcess().SessionId },
                        models = new { ocr = ocrOk, detect = detOk, face = faceOk, clip },
                        reasons,
                    };
                }
            case "region_pick":
                {
                    int timeout = req.TryGetProperty("timeoutMs", out var ptm) ? ptm.GetInt32() : 120000;
                    var r = RegionPicker.Pick(timeout);
                    return new { ok = r.Ok, cancelled = r.Cancelled, x = r.X, y = r.Y, w = r.W, h = r.H };
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
                    bool fullRate = req.TryGetProperty("fullRate", out var pfr) && pfr.GetBoolean();
                    Infer.SetFullRate(fullRate);
                    Infer.Start(boxes, withObjects, withScene, accurate, withFaces, sceneEvery, faceEvery);
                    return new { running = true, maxBoxes = boxes, objects = withObjects, scene = withScene, accurate = accurate, animeface = withFaces, sceneEvery, faceEvery, fullRate };
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


    /// <summary>在画布上烧帧号（3×5 点阵，白底黑字），让 AI 看图能对上 cells 时间戳。</summary>
    static void DrawIndex(byte[] canvas, int cw, int ch, int x0, int y0, int num, int scale)
    {
        // 3×5 点阵字模：bit0..bit14，行优先
        int[] glyph = new int[10] {
            0x7B6F, 0x2492, 0x79E7, 0x79E7, 0x4F5B, 0x79CF, 0x79EF, 0x2492, 0x79E7, 0x79E7
        };
        int g = num >= 0 && num <= 9 ? glyph[num] : 0;
        int w = 3 * scale, ht = 5 * scale, pad = scale * 2;
        // 白底
        for (int y = y0 - pad; y < y0 + ht + pad; y++)
        {
            if (y < 0 || y >= ch) continue;
            for (int x = x0 - pad; x < x0 + w + pad; x++)
            {
                if (x < 0 || x >= cw) continue;
                int o = (y * cw + x) * 4;
                canvas[o] = 245; canvas[o + 1] = 245; canvas[o + 2] = 245; canvas[o + 3] = 255;
            }
        }
        // 黑点
        for (int gy = 0; gy < 5; gy++)
        {
            for (int gx = 0; gx < 3; gx++)
            {
                int bit = gy * 3 + gx;
                if (((g >> bit) & 1) == 0) continue;
                for (int sy = 0; sy < scale; sy++)
                {
                    int y = y0 + gy * scale + sy;
                    if (y < 0 || y >= ch) continue;
                    for (int sx = 0; sx < scale; sx++)
                    {
                        int x = x0 + gx * scale + sx;
                        if (x < 0 || x >= cw) continue;
                        int o = (y * cw + x) * 4;
                        canvas[o] = 20; canvas[o + 1] = 20; canvas[o + 2] = 20; canvas[o + 3] = 255;
                    }
                }
            }
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
        // 全模型后台预热：让「第一次识别」也落在 2s 预算内（否则首次 scan 会撞冷启动 6~8s）。
        try { Win.timeBeginPeriod(1); } catch { }   // 1ms 定时精度：60fps 采集的前提
        var warm = new Thread(() =>
        {
            // Session0 说明没能桥接到用户桌面：这时跑全模型既看不见画面，又白占 ~2GB 内存，直接跳过。
            if (SessionBridge.DegradedToSession0)
            {
                WarmState.Done = true;
                WarmState.Warming = false;
                return;
            }
            WarmState.Warming = true;
            var sw = Stopwatch.StartNew();
            try { PaddleOcr.Warmup(); } catch { }
            WarmState.OcrMs = sw.ElapsedMilliseconds; sw.Restart();
            try { YoloDetect.Warmup(); } catch { }
            WarmState.YoloMs = sw.ElapsedMilliseconds; sw.Restart();
            try { AnimeFaceDetect.Warmup(); } catch { }
            WarmState.FaceMs = sw.ElapsedMilliseconds; sw.Restart();
            try { ClipScene.Warmup(); } catch { }
            WarmState.ClipMs = sw.ElapsedMilliseconds;
            sw.Restart();
            // 命令级预热：把每条识别路径的首次 JIT / 输入尺寸重建 / 内存分配都提前吃掉，
            // 否则"第一次 scan / locate"仍会超 2s（实测 1.7~5.5s），第二次才 100~400ms。
            try
            {
                int sw2 = Gdi.ScreenW(), sh2 = Gdi.ScreenH();
                var (buf, bw, bh) = Gdi.Capture(0, 0, sw2, sh2);
                try { Ocr.Run(buf, bw, bh, 1.0); } catch { }
                try { Ocr.Run(buf, bw, bh, 0.7); } catch { }
                try { YoloDetect.Run(buf, bw, bh, false); } catch { }
                try { AnimeFaceDetect.Run(buf, bw, bh); } catch { }
                try { ClipScene.Run(buf, bw, bh, 3); } catch { }
                var (buf2, bw2, bh2) = Gdi.Capture(0, 0, Math.Min(1200, sw2), Math.Min(800, sh2));
                try { Ocr.Run(buf2, bw2, bh2, 1.0); } catch { }
                try { Uia.Run(0); } catch { }
            }
            catch { }
            WarmState.CommandMs = sw.ElapsedMilliseconds;
            WarmState.Warming = false;
            WarmState.Done = true;
        }) { IsBackground = true };
        warm.Start();
        Event("ready");

        string line;
        long lastNumericId = 0;
        while (true)
        {
            // ReadLine 必须在 try 里：父进程（DSH）关闭 stdin 时它会抛
            // IOException 包装的 SocketException(10054「远程主机强迫关闭了一个现有的连接」)。
            // 之前这行在 try 之外，管道一断就变成未捕获异常 → 0xe0434352 崩溃
            // （2026-11-06 事件日志 22:02:41 / 22:09:03 / 22:15:44 连续三次）。
            // 管道关闭是正常收场信号，安静退出即可。
            try
            {
                line = Console.In.ReadLine();
            }
            catch (Exception)
            {
                break;
            }

            if (line == null) break;   // stdin 正常 EOF
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
