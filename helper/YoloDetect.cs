using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// ============ 目标检测（YOLOv8n ONNX）：每帧物体描述 ============
static class YoloDetect
{
    const int InputSize = 640;
    const float ConfTh = 0.35f;
    const float IouTh = 0.45f;
    static InferenceSession _sess;
    static InferenceSession _sessM;
    static bool _init;
    static string _err;
    static readonly object Gate = new object();
    public static string Backend = "cpu";

    static readonly string[] Names = { "person","bicycle","car","motorcycle","airplane","bus","train","truck","boat","traffic light","fire hydrant","stop sign","parking meter","bench","cat","dog","horse","sheep","cow","elephant","bear","zebra","giraffe","backpack","umbrella","handbag","tie","suitcase","frisbee","skis","snowboard","sports ball","kite","baseball bat","baseball glove","skateboard","surfboard","tennis racket","bottle","wine glass","cup","fork","knife","spoon","bowl","banana","apple","sandwich","orange","broccoli","carrot","hot dog","pizza","donut","cake","chair","couch","potted plant","bed","dining table","toilet","tv","laptop","mouse","remote","keyboard","cell phone","microwave","oven","toaster","sink","refrigerator","book","clock","vase","scissors","teddy bear","hair drier","toothbrush" };
    static readonly string[] Zh = { "人","自行车","汽车","摩托车","飞机","公交车","火车","卡车","船","红绿灯","消防栓","停止标志","停车计时器","长椅","猫","狗","马","羊","牛","大象","熊","斑马","长颈鹿","背包","雨伞","手提包","领带","行李箱","飞盘","滑雪板","单板滑雪","球","风筝","棒球棒","棒球手套","滑板","冲浪板","网球拍","瓶子","酒杯","杯子","叉子","刀","勺子","碗","香蕉","苹果","三明治","橙子","西兰花","胡萝卜","热狗","披萨","甜甜圈","蛋糕","椅子","沙发","盆栽","床","餐桌","马桶","电视","笔记本电脑","鼠标","遥控器","键盘","手机","微波炉","烤箱","烤面包机","水槽","冰箱","书","时钟","花瓶","剪刀","泰迪熊","吹风机","牙刷" };

    public static void Warmup() { try { EnsureInit(); } catch { } }

    static void EnsureInit()
    {
        if (_init) { if (_err != null) throw new Exception(_err); return; }
        lock (Gate)
        {
            if (_init) { if (_err != null) throw new Exception(_err); return; }
            try
            {
                string dir = PaddleOcr.ModelDir();
                string path = Path.Combine(dir, "yolov8n.onnx");
                if (!File.Exists(path)) throw new Exception("找不到 yolov8n.onnx");
                var (sess, backend) = Providers.CreateSession(path);
                _sess = sess;
                Backend = backend;
            }
            catch (Exception e) { _err = e.Message; }
            _init = true;
        }
    }

    static string ColorName(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max < 45) return "黑";
        if (min > 205) return "白";
        if (max - min < 28) return "灰";
        if (r >= g && r >= b)
        {
            if (g > b * 1.25 && g > r * 0.75) return g > r * 0.9 ? "黄" : "橙";
            if (b > g * 1.15 && b > r * 0.65) return "紫";
            return "红";
        }
        if (g >= r && g >= b)
        {
            if (r > b * 1.25) return "黄";
            if (b > r * 1.25) return "青";
            return "绿";
        }
        if (r > g * 1.2) return "紫";
        if (g > r * 1.2) return "青";
        return "蓝";
    }

    static string DominantColor(byte[] bgra, int w, int h, double bx, double by, double bw, double bh)
    {
        int x0 = Math.Max(0, (int)bx), y0 = Math.Max(0, (int)by);
        int x1 = Math.Min(w, (int)(bx + bw)), y1 = Math.Min(h, (int)(by + bh));
        if (x1 <= x0 || y1 <= y0) return "";
        int sx = Math.Max(1, (x1 - x0) / 12), sy = Math.Max(1, (y1 - y0) / 12);
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = y0; y < y1; y += sy)
            for (int x = x0; x < x1; x += sx)
            {
                int off = (y * w + x) * 4;
                b += bgra[off]; g += bgra[off + 1]; r += bgra[off + 2]; n++;
            }
        if (n == 0) return "";
        return ColorName((int)(r / n), (int)(g / n), (int)(b / n));
    }

    static (InferenceSession, string) EnsureAccurate()
    {
        if (_sessM != null) return (_sessM, BackendM);
        lock (Gate)
        {
            if (_sessM != null) return (_sessM, BackendM);
            string dir = PaddleOcr.ModelDir();
            string path = Path.Combine(dir, "yolov8m.onnx");
            if (!File.Exists(path)) throw new Exception("找不到 yolov8m.onnx");
            var (sess, backend) = Providers.CreateSession(path);
            _sessM = sess;
            BackendM = backend;
            return (_sessM, BackendM);
        }
    }
    static string BackendM = "cpu";

    static float Iou((float conf, int cls, float cx, float cy, float w, float h) a, (float conf, int cls, float cx, float cy, float w, float h) b)
    {
        float ax1 = a.cx - a.w / 2, ay1 = a.cy - a.h / 2, ax2 = a.cx + a.w / 2, ay2 = a.cy + a.h / 2;
        float bx1 = b.cx - b.w / 2, by1 = b.cy - b.h / 2, bx2 = b.cx + b.w / 2, by2 = b.cy + b.h / 2;
        float ix1 = Math.Max(ax1, bx1), iy1 = Math.Max(ay1, by1), ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        float inter = iw * ih;
        float ua = (ax2 - ax1) * (ay2 - ay1) + (bx2 - bx1) * (by2 - by1) - inter;
        return ua <= 0 ? 0 : inter / ua;
    }

    // 输入 BGRA 帧 → 物体列表
    public static Dictionary<string, object> Run(byte[] bgra, int w, int h, bool accurate = false)
    {
        InferenceSession sess = null; string backend = "cpu"; string engine = "yolov8n";
        if (accurate)
        {
            try { (sess, backend) = EnsureAccurate(); engine = "yolov8m"; }
            catch { sess = null; }
        }
        if (sess == null)
        {
            EnsureInit();
            if (_err != null) throw new Exception(_err);
            sess = _sess; backend = Backend;
        }
        var sw = Stopwatch.StartNew();
        float scale = Math.Min((float)InputSize / Math.Max(1, w), (float)InputSize / Math.Max(1, h));
        int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));
        int px = (InputSize - nw) / 2, py = (InputSize - nh) / 2;
        var inp = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < InputSize; y++)
                for (int x = 0; x < InputSize; x++)
                    inp[0, c, y, x] = 114f / 255f;
        for (int y = 0; y < nh; y++)
        {
            int sy = Math.Min(h - 1, (int)(y / scale));
            for (int x = 0; x < nw; x++)
            {
                int sx = Math.Min(w - 1, (int)(x / scale));
                int off = (sy * w + sx) * 4;
                inp[0, 0, py + y, px + x] = bgra[off + 2] / 255f;
                inp[0, 1, py + y, px + x] = bgra[off + 1] / 255f;
                inp[0, 2, py + y, px + x] = bgra[off] / 255f;
            }
        }
        var cand = new List<(float conf, int cls, float cx, float cy, float w, float h)>();
        lock (Providers.Gate)
        {
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(sess.InputMetadata.Keys.First(), inp) };
            using var results = sess.Run(inputs);
            var outT = results.First().AsTensor<float>();
            int dims = outT.Dimensions.Length;
            int n = outT.Dimensions[dims - 1];
            int channels = outT.Dimensions[dims - 2];
            for (int i = 0; i < n; i++)
            {
                float best = 0; int bc = 0;
                for (int c = 4; c < channels; c++)
                {
                    float v = outT[0, c, i];
                    if (v > best) { best = v; bc = c - 4; }
                }
                if (best < ConfTh) continue;
                cand.Add((best, bc, outT[0, 0, i], outT[0, 1, i], outT[0, 2, i], outT[0, 3, i]));
            }
        }
        var kept = new List<Dictionary<string, object>>();
        var sorted = cand.OrderByDescending(c => c.conf).ToList();
        var used = new bool[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            if (used[i]) continue;
            var a = sorted[i];
            used[i] = true;
            kept.Add(new Dictionary<string, object> {
                { "label", Names[a.cls] }, { "labelZh", Zh[a.cls] },
                { "conf", Math.Round(a.conf, 3) },
                { "x", Math.Round((a.cx - a.w / 2 - px) / scale) },
                { "y", Math.Round((a.cy - a.h / 2 - py) / scale) },
                { "w", Math.Round(a.w / scale) }, { "h", Math.Round(a.h / scale) },
                { "color", DominantColor(bgra, w, h, (a.cx - a.w / 2 - px) / scale, (a.cy - a.h / 2 - py) / scale, a.w / scale, a.h / scale) },
            });
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (used[j] || sorted[j].cls != a.cls) continue;
                if (Iou(a, sorted[j]) > IouTh) used[j] = true;
            }
        }
        sw.Stop();
        return new Dictionary<string, object> {
            { "items", kept }, { "count", kept.Count },
            { "summary", string.Join("、", kept.GroupBy(k => k["labelZh"]).Select(g => g.Key + "×" + g.Count())) },
            { "ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1) },
            { "backend", backend }, { "engine", engine },
        };
    }
}
