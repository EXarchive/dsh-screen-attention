using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// ============ PaddleOCR PP-OCRv4 det+rec 推理（onnxruntime DirectML / CPU 降级） ============
//
// 替换 Windows.Media.Ocr：
//   det  — ch_PP-OCRv4_det_infer.onnx  输入的 BGR 图 → sigmoid 概率图 [1,1,H,W] → DB 后处理
//   rec  — ch_PP-OCRv4_rec_infer.onnx  裁剪的文本框 → CTC softmax [1,T,6625] → 贪心解码
//
// 关键实现约束（与 RapidOCR / PaddleOCR 对齐）：
//   * det 归一化：mean=[0.485,0.456,0.406] std=[0.229,0.224,0.225]，BGR 顺序（与 GDI 的 BGRA 前3字节一致）
//   * rec 归一化：mean=0.5 std=0.5，输入固定 [1,3,48,320]
//   * CTCLabelDecode：id 0=blank 跳过；与相邻"原始"时序帧相同才去重；id 1..6623 → 字典，id 6624 → 空格
//   * 坐标统一为轴对齐 rect（协议 rect{x,y,w,h} 本来就是轴对齐），不做旋转矩形
//      ——屏幕文字基本水平，旋转矩形不必要；轴对齐 bbox 足够完整覆盖文本行
static class PaddleOcr
{
    // ---- det 参数（RapidOCR 默认，short-side 放大到 736 的 32 倍数）----
    const int DetLimitSide = 320;
    const float DetThresh = 0.3f;
    const float DetBoxThresh = 0.5f;
    const float DetUnclipRatio = 2.0f;

    static readonly float[] DetMean = { 0.485f, 0.456f, 0.406f };
    static readonly float[] DetStd = { 0.229f, 0.224f, 0.225f };

    // ---- rec 固定形状 ----
    const int RecImgH = 48;
    const int RecImgW = 160;   // 320 → 160：单框 rec 时间约减半（屏幕 UI 多为短文本）
    const int NumClasses = 6625;

    static InferenceSession _det;
    static InferenceSession _rec;
    static bool _detDml, _recDml;
    static string[] _dict;
    static readonly object Gate = new object();
    static bool _init;
    static string _initError;

    // 上次成功用到的后端描述（供 handshake / 观测）
    public static string Backend => (_detDml || _recDml) ? "directml" : "cpu";

    // 后台预热：提前加载 det/rec 模型（失败静默，首次 ocr 调用会再次尝试并报错）
    public static void Warmup()
    {
        try { EnsureInit(); } catch { }
    }

    public static string ModelDir()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string cand = Path.Combine(dir, "models");
            if (File.Exists(Path.Combine(cand, "ch_PP-OCRv4_det_infer.onnx"))) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new Exception("找不到 models 目录（ch_PP-OCRv4_det_infer.onnx）");
    }

    static void EnsureInit()
    {
        if (_init) { if (_initError != null) throw new Exception("PaddleOCR 初始化失败: " + _initError); return; }
        lock (Gate)
        {
            if (_init) { if (_initError != null) throw new Exception("PaddleOCR 初始化失败: " + _initError); return; }
            try
            {
                string dir = ModelDir();

                // det/rec session：优先 fp16 模型（DirectML 对 fp16 有硬件加速），缺失则回退 fp32
                string Pick(string name)
                {
                    // fp16 在 DirectML 上实测推理失败（0 次成功），回退 fp32；保留 fp16 文件供 CPU EP 使用
                    return Path.Combine(dir, name);
                }
                string detPath = Pick("ch_PP-OCRv4_det_infer.onnx");
                (_det, _detDml) = CreateSession(detPath);

                string recPath = Pick("ch_PP-OCRv4_rec_infer.onnx");
                (_rec, _recDml) = CreateSession(recPath);

                // 字典 6623 字符（UTF-8，无 BOM）
                var lines = File.ReadAllLines(Path.Combine(dir, "ppocr_keys_v1.txt"), Encoding.UTF8);
                var chars = new List<string>(NumClasses);
                for (int i = 0; i < NumClasses; i++) chars.Add("");
                int n = 0;
                foreach (var raw in lines)
                {
                    string s = raw.TrimEnd('\r');
                    if (n < 6623) chars[n + 1] = s;   // id 1..6623 → 字典
                    n++;
                }
                chars[6624] = " ";                     // id 6624 → space（PaddleOCR use_space_char）
                _dict = chars.ToArray();
                if (n != 6623) throw new Exception($"字典行数异常: 期望 6623, 实际 {n}");

                _init = true;
            }
            catch (Exception e)
            {
                _initError = e.Message;
                _init = true;
                throw;
            }
        }
    }

    static (InferenceSession, bool) CreateSession(string path)
    {
        // 统一后端选择：TensorRT/CUDA（独显+显存）→ DirectML → CPU
        try
        {
            var (s, backend) = Providers.CreateSession(path);

            return (s, backend != "cpu");
        }
        catch
        {
            var s = new InferenceSession(path);
            return (s, false);
        }
    }

    // ---- run 封装：DML 运行期异常 → 当场降级 CPU 重跑一次 ----
    static float[] RunDet(DenseTensor<float> input)
    {
        try { return RunOnce(_det, input); }
        catch
        {
            if (!_detDml) throw;
            lock (Gate)
            {
                if (_detDml) { _det?.Dispose(); _det = new InferenceSession(ModelDir() + "\\ch_PP-OCRv4_det_infer.onnx"); _detDml = false; }
            }
            return RunOnce(_det, input);
        }
    }

    static float[] RunRec(DenseTensor<float> input)
    {
        try { return RunOnce(_rec, input); }
        catch
        {
            if (!_recDml) throw;
            lock (Gate)
            {
                if (_recDml) { _rec?.Dispose(); _rec = new InferenceSession(ModelDir() + "\\ch_PP-OCRv4_rec_infer.onnx"); _recDml = false; }
            }
            return RunOnce(_rec, input);
        }
    }

    // 批量 rec：N 个 [1,3,48,320] 拼成 [N,3,48,320] 一次推理，
    // 把 N 次 DirectML dispatch 压成 1 次（每次 dispatch 约 5ms 固定开销）。
    static float[][] RunRecBatch(List<DenseTensor<float>> items)
    {
        int n = items.Count;
        if (n == 0) return Array.Empty<float[]>();
        int per = 3 * RecImgH * RecImgW;
        var data = new float[n * per];
        for (int i = 0; i < n; i++)
        {
            var arr = items[i].ToArray();
            Array.Copy(arr, 0, data, i * per, Math.Min(arr.Length, per));
        }
        var t = new DenseTensor<float>(new[] { n, 3, RecImgH, RecImgW });
        data.CopyTo(t.Buffer.Span);
        var flat = RunRec(t);
        int each = flat.Length / n;
        var res = new float[n][];
        for (int i = 0; i < n; i++)
        {
            res[i] = new float[each];
            Array.Copy(flat, i * each, res[i], 0, each);
        }
        return res;
    }

    static float[] RunOnce(InferenceSession s, DenseTensor<float> input)
    {
        lock (Providers.Gate)
        {
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", input) };
            using var results = s.Run(inputs);
            var outTensor = results.First().AsTensor<float>();
            return outTensor.ToArray<float>();
        }
    }

    // ---- 通用双线性缩放：BGRA 全通道 ----
    static byte[] ResizeBgra(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        float sx = sw / (float)dw, sy = sh / (float)dh;
        for (int y = 0; y < dh; y++)
        {
            float fy0 = (y + 0.5f) * sy - 0.5f;
            int y0 = Math.Clamp((int)Math.Floor(fy0), 0, sh - 1);
            int y1 = Math.Min(y0 + 1, sh - 1);
            float fy = fy0 - y0;
            for (int x = 0; x < dw; x++)
            {
                float fx0 = (x + 0.5f) * sx - 0.5f;
                int x0 = Math.Clamp((int)Math.Floor(fx0), 0, sw - 1);
                int x1 = Math.Min(x0 + 1, sw - 1);
                float fx = fx0 - x0;
                int i00 = (y0 * sw + x0) * 4, i01 = (y0 * sw + x1) * 4, i10 = (y1 * sw + x0) * 4, i11 = (y1 * sw + x1) * 4;
                int o = (y * dw + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float top = src[i00 + c] + (src[i01 + c] - src[i00 + c]) * fx;
                    float bot = src[i10 + c] + (src[i11 + c] - src[i10 + c]) * fx;
                    dst[o + c] = (byte)Math.Clamp(top + (bot - top) * fy, 0, 255);
                }
            }
        }
        return dst;
    }

    // ---- det 预处理：BGRA → [1,3,dh,dw] NCHW BGR + normalize ----
    static (DenseTensor<float> tensor, int dw, int dh) DetPreprocess(byte[] src, int sw, int sh)
    {
        double ratio;
        int m = Math.Min(sw, sh);
        // 标准 limit_type=max：任意一边超过阈值就整体缩小；否则放大到阈值。
        // 原实现写成 m < limit ? limit/m : 1.0，导致 1600px 高的屏永远按 1.0 全尺寸跑 det（4M 像素），
        // 单帧 det 就要 200ms+，识别速率被压到 4Hz。
        ratio = m > DetLimitSide ? (double)DetLimitSide / m : 1.0;   // 只缩不放：小 ROI 保持原尺寸
        int dw = Math.Max(32, (int)(Math.Round(sw * ratio / 32.0) * 32));
        int dh = Math.Max(32, (int)(Math.Round(sh * ratio / 32.0) * 32));

        var data = new float[3 * dw * dh];
        float sx = sw / (float)dw, sy = sh / (float)dh;
        for (int y = 0; y < dh; y++)
        {
            float fy0 = (y + 0.5f) * sy - 0.5f;
            int y0 = Math.Clamp((int)Math.Floor(fy0), 0, sh - 1);
            int y1 = Math.Min(y0 + 1, sh - 1);
            float fy = fy0 - y0;
            for (int x = 0; x < dw; x++)
            {
                float fx0 = (x + 0.5f) * sx - 0.5f;
                int x0 = Math.Clamp((int)Math.Floor(fx0), 0, sw - 1);
                int x1 = Math.Min(x0 + 1, sw - 1);
                float fx = fx0 - x0;
                int i00 = (y0 * sw + x0) * 4, i01 = (y0 * sw + x1) * 4, i10 = (y1 * sw + x0) * 4, i11 = (y1 * sw + x1) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float top = src[i00 + c] + (src[i01 + c] - src[i00 + c]) * fx;
                    float bot = src[i10 + c] + (src[i11 + c] - src[i10 + c]) * fx;
                    float v = top + (bot - top) * fy;
                    v = (v / 255f - DetMean[c]) / DetStd[c];
                    data[(c * dh + y) * dw + x] = v;   // NCHW
                }
            }
        }
        var t = new DenseTensor<float>(new[] { 1, 3, dh, dw });
        data.AsSpan().CopyTo(t.Buffer.Span);
        return (t, dw, dh);
    }

    sealed class Box { public double X, Y, W, H, Score; }

    // ---- det 后处理（DB，轴对齐简化）：二值化 → 连通域 → bbox → score 过滤 → unclip 膨胀 ----
    static List<Box> DetPostprocess(float[] prob, int pw, int ph, int fw, int fh)
    {
        int n = pw * ph;
        var mask = new byte[n];
        for (int i = 0; i < n; i++) mask[i] = prob[i] > DetThresh ? (byte)1 : (byte)0;

        var seen = new byte[n];
        var stack = new int[n];
        var boxes = new List<Box>();

        for (int start = 0; start < n; start++)
        {
            if (mask[start] == 0 || seen[start] != 0) continue;
            int sp = 0; stack[sp++] = start; seen[start] = 1;
            int minX = pw, maxX = -1, minY = ph, maxY = -1;
            long area = 0; double sum = 0;
            while (sp > 0)
            {
                int q = stack[--sp];
                int qx = q % pw, qy = q / pw;
                area++; sum += prob[q];
                if (qx < minX) minX = qx; if (qx > maxX) maxX = qx;
                if (qy < minY) minY = qy; if (qy > maxY) maxY = qy;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = qy + dy;
                    if (ny < 0 || ny >= ph) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = qx + dx;
                        if (nx < 0 || nx >= pw) continue;
                        int ni = ny * pw + nx;
                        if (mask[ni] != 0 && seen[ni] == 0) { seen[ni] = 1; stack[sp++] = ni; }
                    }
                }
            }

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bw < 3 || bh < 3) continue;
            double score = sum / area;
            if (score < DetBoxThresh) continue;

            // unclip：按面积/周长外扩（RapidOCR 用 pyclipper offset 的矩形等价形式）
            double perim = 2.0 * (bw + bh);
            double d = perim > 0 ? (bw * bh) * DetUnclipRatio / perim : 0;
            double x0 = minX - d, x1 = maxX + d, y0 = minY - d, y1 = maxY + d;
            x0 = Math.Clamp(x0, 0, pw); x1 = Math.Clamp(x1, 0, pw);
            y0 = Math.Clamp(y0, 0, ph); y1 = Math.Clamp(y1, 0, ph);
            if (x1 - x0 < 3 || y1 - y0 < 3) continue;

            // 映射回（scale 后的）帧坐标
            double rw = fw / (double)pw, rh = fh / (double)ph;
            boxes.Add(new Box
            {
                X = x0 * rw, Y = y0 * rh,
                W = (x1 - x0) * rw, H = (y1 - y0) * rh,
                Score = score,
            });
        }
        // 阅读顺序：从上到下，同行从左到右
        boxes.Sort((a, b) =>
        {
            double cy = (a.Y + a.H / 2) - (b.Y + b.H / 2);
            if (Math.Abs(cy) > (a.H + b.H) * 0.5) return a.Y.CompareTo(b.Y);
            return a.X.CompareTo(b.X);
        });
        return boxes;
    }

    // ---- rec 预处理：裁剪 box → [1,3,48,320] NCHW BGR + normalize + pad ----
    static DenseTensor<float> RecPreprocess(byte[] frame, int fw, int fh, Box box)
    {
        int bx = Math.Max(0, (int)Math.Floor(box.X));
        int by = Math.Max(0, (int)Math.Floor(box.Y));
        int bw = Math.Min(fw - bx, (int)Math.Ceiling(box.X + box.W) - bx);
        int bh = Math.Min(fh - by, (int)Math.Ceiling(box.Y + box.H) - by);
        if (bw < 2) bw = 2;
        if (bh < 2) bh = 2;

        int rw = (int)Math.Ceiling(RecImgH * (double)bw / bh);
        if (rw > RecImgW) rw = RecImgW;
        if (rw < 1) rw = 1;

        var data = new float[3 * RecImgH * RecImgW];   // 已全 0（pad 区）
        float sx = bw / (float)rw, sy = bh / (float)RecImgH;
        for (int y = 0; y < RecImgH; y++)
        {
            float fy0 = (y + 0.5f) * sy - 0.5f;
            int y0 = by + Math.Clamp((int)Math.Floor(fy0), 0, bh - 1);
            int y1 = by + Math.Min(Math.Clamp((int)Math.Floor(fy0), 0, bh - 1) + 1, bh - 1);
            float fy = fy0 - Math.Clamp((int)Math.Floor(fy0), 0, bh - 1);
            for (int x = 0; x < rw; x++)
            {
                float fx0 = (x + 0.5f) * sx - 0.5f;
                int cx0 = Math.Clamp((int)Math.Floor(fx0), 0, bw - 1);
                int cx1 = Math.Min(cx0 + 1, bw - 1);
                float fx = fx0 - cx0;
                int x0 = bx + cx0, x1 = bx + cx1;
                int i00 = (y0 * fw + x0) * 4, i01 = (y0 * fw + x1) * 4, i10 = (y1 * fw + x0) * 4, i11 = (y1 * fw + x1) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float top = frame[i00 + c] + (frame[i01 + c] - frame[i00 + c]) * fx;
                    float bot = frame[i10 + c] + (frame[i11 + c] - frame[i10 + c]) * fx;
                    float v = top + (bot - top) * fy;
                    v = (v / 255f - 0.5f) / 0.5f;
                    data[(c * RecImgH + y) * RecImgW + x] = v;
                }
            }
        }
        var t = new DenseTensor<float>(new[] { 1, 3, RecImgH, RecImgW });
        data.AsSpan().CopyTo(t.Buffer.Span);
        return t;
    }

    // ---- CTC 贪心解码（PaddleOCR CTCLabelDecode 语义）----
    static string CtcDecode(float[] logits, int tSteps)
    {
        var sb = new StringBuilder();
        int prevRaw = -1;
        for (int t = 0; t < tSteps; t++)
        {
            int off = t * NumClasses;
            int best = 0; float bestV = logits[off];
            for (int c = 1; c < NumClasses; c++)
            {
                float v = logits[off + c];
                if (v > bestV) { bestV = v; best = c; }
            }
            if (best == 0) { prevRaw = 0; continue; }        // blank 忽略
            if (best == prevRaw) continue;                    // 与相邻原始帧相同才去重
            sb.Append(_dict[best]);
            prevRaw = best;
        }
        return sb.ToString();
    }

    // ---- 主入口：与旧 Ocr.Run 同签名，返回协议结构 ----
    // ===== 流式逐帧识别：每帧都做 det；rec 仅对"内容签名变化"的框执行（缓存复用）=====
    static readonly Dictionary<string, string> _recCache = new Dictionary<string, string>();
    static readonly object _recLock = new object();
    static long _recCacheHits, _recCacheMiss;

    static string BoxSignature(byte[] frame, int fw, int fh, Box box)
    {
        int x0 = Math.Max(0, (int)box.X), y0 = Math.Max(0, (int)box.Y);
        int x1 = Math.Min(fw, (int)(box.X + box.W)), y1 = Math.Min(fh, (int)(box.Y + box.H));
        if (x1 <= x0 || y1 <= y0) return "empty";
        int stepX = Math.Max(1, (x1 - x0) / 24), stepY = Math.Max(1, (y1 - y0) / 8);
        unchecked
        {
            ulong hsh = 14695981039346656037UL;
            for (int y = y0; y < y1; y += stepY)
                for (int x = x0; x < x1; x += stepX)
                {
                    int off = (y * fw + x) * 4;
                    hsh = (hsh ^ frame[off]) * 1099511628211UL;
                    hsh = (hsh ^ frame[off + 1]) * 1099511628211UL;
                    hsh = (hsh ^ frame[off + 2]) * 1099511628211UL;
                }
            return hsh.ToString("x16");
        }
    }

    static List<Box> _lastBoxes = new List<Box>();
    static long _detRuns, _detSkips;

    // skipDet=true 时复用上次检测框，只做 rec —— det 是最大固定开销（~35ms），
    // 屏幕文字布局帧间几乎不变，没必要每帧重检。
    public static Dictionary<string, object> RunStreaming(byte[] frame, int fw, int fh, int maxBoxes = 0, bool skipDet = false)
    {
        EnsureInit();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<Box> boxes;
        if (skipDet && _lastBoxes.Count > 0) { boxes = _lastBoxes; _detSkips++; }
        else
        {
            var (detIn, dw, dh) = DetPreprocess(frame, fw, fh);
            float[] prob = RunDet(detIn);
            boxes = DetPostprocess(prob, dw, dh, fw, fh);
            _lastBoxes = boxes;
            _detRuns++;
        }
        var items = new List<Dictionary<string, object>>();
        int chars = 0, hits = 0, miss = 0;
        foreach (var box in boxes)
        {
            if (maxBoxes > 0 && items.Count >= maxBoxes) break;
            string sig = BoxSignature(frame, fw, fh, box);
            string key = ((int)box.X) + "," + ((int)box.Y) + "," + ((int)box.W) + "," + ((int)box.H) + ":" + sig;
            string text = null;
            lock (_recLock) { _recCache.TryGetValue(key, out text); }
            if (text == null)
            {
                try
                {
                    var recIn = RecPreprocess(frame, fw, fh, box);
                    float[] logits = RunRec(recIn);
                    int tSteps = logits.Length / NumClasses;
                    text = CtcDecode(logits, tSteps);
                }
                catch { text = ""; }
                miss++;
                lock (_recLock) { if (text.Length > 0) _recCache[key] = text; if (_recCache.Count > 2000) _recCache.Clear(); }
            }
            else hits++;
            if (string.IsNullOrEmpty(text)) continue;
            var rect = new Dictionary<string, object> {
                { "x", (int)Math.Round(box.X) }, { "y", (int)Math.Round(box.Y) },
                { "w", (int)Math.Round(box.W) }, { "h", (int)Math.Round(box.H) },
            };
            items.Add(new Dictionary<string, object> {
                { "source", "ocr" }, { "text", text }, { "rect", rect }, { "line", 0 },
            });
            chars += text.Length;
        }
        sw.Stop();
        lock (_recLock) { _recCacheHits += hits; _recCacheMiss += miss; }
        return new Dictionary<string, object> {
            { "items", items }, { "chars", chars }, { "lines", items.Count },
            { "ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1) },
            { "stripes", 1 }, { "scale", 1.0 }, { "engine", "paddleocr-stream-" + Backend },
            { "recHits", hits }, { "recMiss", miss },
            { "detRuns", _detRuns }, { "detSkips", _detSkips },
        };
    }

    public static Dictionary<string, object> Run(byte[] buf, int w, int h, double scale, int maxBoxes = 0)
    {
        EnsureInit();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // scale>1：内存双线性放大（小字识别更稳），坐标最后 /scale 还原
        byte[] frame = buf; int fw = w, fh = h;
        if (scale > 1.0 + 1e-6)
        {
            fw = Math.Max(1, (int)Math.Round(w * scale));
            fh = Math.Max(1, (int)Math.Round(h * scale));
            frame = ResizeBgra(buf, w, h, fw, fh);
        }

        // det
        var (detIn, dw, dh) = DetPreprocess(frame, fw, fh);
        float[] prob = RunDet(detIn);
        int pw = dw, ph = dh;   // PP-OCRv4 det 1:1 输出；若不一致以上下文为准
        // 用会话输出的真实维度（det 1:1，这里与输入一致）
        var boxes = DetPostprocess(prob, pw, ph, fw, fh);

        // rec
        var items = new List<Dictionary<string, object>>();
        int chars = 0;
        foreach (var box in boxes)
        {
            if (maxBoxes > 0 && items.Count >= maxBoxes) break;
            string text = "";
            try
            {
                var recIn = RecPreprocess(frame, fw, fh, box);
                float[] logits = RunRec(recIn);
                int tSteps = logits.Length / NumClasses;
                text = CtcDecode(logits, tSteps);
            }
            catch { text = ""; }
            if (string.IsNullOrEmpty(text)) continue;

            var rect = new Dictionary<string, object> {
                { "x", (int)Math.Round(box.X / scale) },
                { "y", (int)Math.Round(box.Y / scale) },
                { "w", (int)Math.Round(box.W / scale) },
                { "h", (int)Math.Round(box.H / scale) },
            };
            items.Add(new Dictionary<string, object> {
                { "source", "ocr" }, { "text", text }, { "rect", rect }, { "line", 0 },
            });
            chars += text.Length;
        }

        sw.Stop();
        return new Dictionary<string, object> {
            { "items", items },
            { "chars", chars },
            { "lines", items.Count },
            { "ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1) },
            { "stripes", 1 },
            { "scale", scale },
            { "engine", "paddleocr-" + Backend },
        };
    }
}