using System;
using System.Runtime.InteropServices;
using System.Threading;

// ============ WASAPI 回环采集（抓"电脑正在放什么"，不是麦克风） ============
// 纯 P/Invoke，不依赖任何第三方包，也不需要虚拟声卡。
// 用途：判断"视频/会议里有没有人在说话"，给画面配上声音信息。
// 注意：抓的是整个系统输出（BGM、提示音、游戏音效都会进来）。
static class AudioCapture
{
    // ---- COM 接口 ----
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iUnknown);
        int OpenPropertyStore(int access, out IntPtr props);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr sessionGuid);
        int GetBufferSize(out int frames);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out int padding);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr h);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePos, out long qpcPos);
        int ReleaseBuffer(int frames);
        int GetNextPacketSize(out int frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    const int AUDCLNT_SHAREMODE_SHARED = 0;
    const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    public sealed class Level
    {
        public bool Ok;
        public string Error;
        public double Rms;        // 0~1
        public double Peak;       // 0~1
        public double Db;         // -100~0
        public double Zcr;        // 过零率 0~1（高=像人声/噪声，低=纯音）
        public bool Silent;       // 整段都安静
        public int Samples;       // 采到的样本数
        public int Channels;
        public int SampleRate;
        public string DeviceId;

        public bool SoundsLikeVoice => Ok && !Silent && Rms > 0.004 && Zcr > 0.02;
    }

    /// <summary>采 ms 毫秒的系统输出电平均值。</summary>
    public static Level Measure(int ms)
    {
        var res = new Level();
        var th = new Thread(() =>
        {
            IMMDeviceEnumerator en = null;
            IMMDevice dev = null;
            IAudioClient client = null;
            IAudioCaptureClient cap = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                int hr = en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out dev);
                if (hr != 0 || dev == null) { res.Error = "no render device hr=" + hr; return; }
                dev.GetId(out res.DeviceId);

                Guid iid = typeof(IAudioClient).GUID;
                object o;
                hr = dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out o);
                if (hr != 0 || o == null) { res.Error = "activate failed hr=" + hr; return; }
                client = (IAudioClient)o;

                IntPtr fmtPtr;
                hr = client.GetMixFormat(out fmtPtr);
                if (hr != 0) { res.Error = "GetMixFormat hr=" + hr; return; }
                var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(fmtPtr);
                res.Channels = fmt.nChannels;
                res.SampleRate = (int)fmt.nSamplesPerSec;

                long dur = 10_000_000L; // 1s buffer
                hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                    dur, 0, fmtPtr, IntPtr.Zero);
                Marshal.FreeCoTaskMem(fmtPtr);
                if (hr != 0) { res.Error = "Initialize hr=" + hr; return; }

                Guid capIid = typeof(IAudioCaptureClient).GUID;
                object co;
                hr = client.GetService(ref capIid, out co);
                if (hr != 0 || co == null) { res.Error = "GetService hr=" + hr; return; }
                cap = (IAudioCaptureClient)co;

                hr = client.Start();
                if (hr != 0) { res.Error = "Start hr=" + hr; return; }

                int bits = fmt.wBitsPerSample;
                int block = fmt.nBlockAlign;
                bool isFloat = fmt.wFormatTag == 3 || bits == 32;
                double sumSq = 0, peak = 0, prevSign = 0; long n = 0, zeroCross = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int silentFrames = 0, totalFrames = 0;

                while (sw.ElapsedMilliseconds < Math.Max(80, ms))
                {
                    int pkt;
                    cap.GetNextPacketSize(out pkt);
                    if (pkt == 0) { Thread.Sleep(8); continue; }
                    while (pkt > 0)
                    {
                        IntPtr data; int frames, flags; long dpos, qpos;
                        if (cap.GetBuffer(out data, out frames, out flags, out dpos, out qpos) != 0) break;
                        totalFrames += frames;
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                        {
                            silentFrames += frames;
                        }
                        else
                        {
                            int count = frames * fmt.nChannels;
                            if (isFloat && bits == 32)
                            {
                                var arr = new float[count];
                                Marshal.Copy(data, arr, 0, count);
                                for (int i = 0; i < count; i++)
                                {
                                    double v = arr[i];
                                    sumSq += v * v; if (Math.Abs(v) > peak) peak = Math.Abs(v); n++;
                                    double sign = v > 0 ? 1 : (v < 0 ? -1 : 0);
                                    if (prevSign != 0 && sign != 0 && sign != prevSign) zeroCross++;
                                    if (sign != 0) prevSign = sign;
                                }
                            }
                            else if (bits == 16)
                            {
                                var arr = new short[count];
                                Marshal.Copy(data, arr, 0, count);
                                for (int i = 0; i < count; i++)
                                {
                                    double v = arr[i] / 32768.0;
                                    sumSq += v * v; if (Math.Abs(v) > peak) peak = Math.Abs(v); n++;
                                    double sign = v > 0 ? 1 : (v < 0 ? -1 : 0);
                                    if (prevSign != 0 && sign != 0 && sign != prevSign) zeroCross++;
                                    if (sign != 0) prevSign = sign;
                                }
                            }
                        }
                        cap.ReleaseBuffer(frames);
                        cap.GetNextPacketSize(out pkt);
                    }
                }
                try { client.Stop(); } catch { }

                res.Samples = (int)n;
                if (n > 0)
                {
                    res.Rms = Math.Sqrt(sumSq / n);
                    res.Peak = peak;
                    res.Db = res.Rms <= 1e-9 ? -100 : Math.Max(-100, 20 * Math.Log10(res.Rms));
                    res.Zcr = (double)zeroCross / n;
                }
                res.Silent = n == 0 || res.Rms < 0.0015;
                res.Ok = true;
            }
            catch (Exception e) { res.Error = e.GetType().Name + ": " + e.Message; res.Ok = false; }
            finally
            {
                try { if (client != null) Marshal.ReleaseComObject(client); } catch { }
                try { if (cap != null) Marshal.ReleaseComObject(cap); } catch { }
                try { if (dev != null) Marshal.ReleaseComObject(dev); } catch { }
                try { if (en != null) Marshal.ReleaseComObject(en); } catch { }
            }
        });
        th.SetApartmentState(ApartmentState.MTA);
        th.IsBackground = true;
        th.Start();
        th.Join(Math.Max(3000, ms + 2500));
        if (!res.Ok && res.Error == null) res.Error = "timeout";
        return res;
    }
}
