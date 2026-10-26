using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// ============ ACG 动漫脸检测（YOLOv8n face，单类） ============
static class AnimeFaceDetect
{
    const int InputSize = 640;
    const float ConfTh = 0.25f;
    const float IouTh = 0.45f;
    static InferenceSession _sess;
    static bool _init;
    static string _err;
    static readonly object Gate = new object();
    public static string Backend = "cpu";

    public static void Warmup() { try { EnsureInit(); } catch { } }

    static void EnsureInit()
    {
        if (_init) { if (_err != null) throw new Exception(_err); return; }
        lock (Gate)
        {
            if (_init) { if (_err != null) throw new Exception(_err); return; }
            try
            {
                string path = Path.Combine(PaddleOcr.ModelDir(), "animeface", "model.onnx");
                if (!File.Exists(path)) throw new Exception("找不到 animeface/model.onnx");
                var (sess, backend) = Providers.CreateSession(path);
                _sess = sess;
                Backend = backend;
            }
            catch (Exception e) { _err = e.Message; }
            _init = true;
        }
    }

    static float Sample(byte[] bgra, int w, int h, float fx, float fy, int ch)
    {
        int x0 = Math.Max(0, Math.Min(w - 1, (int)fx)), y0 = Math.Max(0, Math.Min(h - 1, (int)fy));
        int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
        float dx = fx - x0, dy = fy - y0;
        int off(int x, int y) => (y * w + x) * 4 + (ch == 0 ? 2 : ch == 1 ? 1 : 0);
        float v = bgra[off(x0, y0)] * (1 - dx) * (1 - dy) + bgra[off(x1, y0)] * dx * (1 - dy)
                + bgra[off(x0, y1)] * (1 - dx) * dy + bgra[off(x1, y1)] * dx * dy;
        return v;
    }

    static float Iou((float x, float y, float w, float h, float c) a, (float x, float y, float w, float h, float c) b)
    {
        float ax2 = a.x + a.w, ay2 = a.y + a.h, bx2 = b.x + b.w, by2 = b.y + b.h;
        float ix1 = Math.Max(a.x, b.x), iy1 = Math.Max(a.y, b.y);
        float ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        float iw = Math.Max(0, ix2 - ix1), ih = Math.Max(0, iy2 - iy1);
        float inter = iw * ih;
        float ua = a.w * a.h + b.w * b.h - inter;
        return ua <= 0 ? 0 : inter / ua;
    }

    public static Dictionary<string, object> Run(byte[] bgra, int w, int h, float conf = ConfTh, int maxFaces = 20)
    {
        EnsureInit();
        if (_err != null) throw new Exception(_err);
        var sw = Stopwatch.StartNew();
        float r = Math.Min((float)InputSize / Math.Max(1, h), (float)InputSize / Math.Max(1, w));
        int nw = Math.Max(1, (int)Math.Round(w * r)), nh = Math.Max(1, (int)Math.Round(h * r));
        int dx = (InputSize - nw) / 2, dy = (InputSize - nh) / 2;
        var inp = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < InputSize; y++)
                for (int x = 0; x < InputSize; x++)
                    inp[0, c, y, x] = 114f / 255f;
        for (int y = 0; y < nh; y++)
        {
            float sy = (y + 0.5f) / r - 0.5f;
            for (int x = 0; x < nw; x++)
            {
                float sx = (x + 0.5f) / r - 0.5f;
                inp[0, 0, dy + y, dx + x] = Sample(bgra, w, h, sx, sy, 0) / 255f;
                inp[0, 1, dy + y, dx + x] = Sample(bgra, w, h, sx, sy, 1) / 255f;
                inp[0, 2, dy + y, dx + x] = Sample(bgra, w, h, sx, sy, 2) / 255f;
            }
        }
        var cand = new List<(float x, float y, float w, float h, float c)>();
        lock (Providers.Gate)
        {
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_sess.InputMetadata.Keys.First(), inp) };
            using var results = _sess.Run(inputs);
            var outT = results.First().AsTensor<float>();
            int d0 = outT.Dimensions[outT.Dimensions.Length - 2];
            int d1 = outT.Dimensions[outT.Dimensions.Length - 1];
            bool transposed = d0 <= 10 && d1 > d0;
            int n = transposed ? d1 : d0;
            int k = transposed ? d0 : d1;
            for (int i = 0; i < n; i++)
            {
                float cx, cy, bw, bh, score;
                if (transposed)
                {
                    cx = outT[0, 0, i]; cy = outT[0, 1, i]; bw = outT[0, 2, i]; bh = outT[0, 3, i];
                    score = k > 4 ? outT[0, 4, i] : 0;
                }
                else
                {
                    cx = outT[0, i, 0]; cy = outT[0, i, 1]; bw = outT[0, i, 2]; bh = outT[0, i, 3];
                    score = k > 4 ? outT[0, i, 4] : 0;
                }
                if (score < conf) continue;
                cand.Add((cx - bw / 2, cy - bh / 2, bw, bh, score));
            }
        }
        var sorted = cand.OrderByDescending(c => c.c).ToList();
        var used = new bool[sorted.Count];
        var faces = new List<Dictionary<string, object>>();
        for (int i = 0; i < sorted.Count && faces.Count < maxFaces; i++)
        {
            if (used[i]) continue;
            var a = sorted[i];
            used[i] = true;
            faces.Add(new Dictionary<string, object> {
                { "x", Math.Round((a.x - dx) / r) }, { "y", Math.Round((a.y - dy) / r) },
                { "w", Math.Round(a.w / r) }, { "h", Math.Round(a.h / r) },
                { "conf", Math.Round(a.c, 3) },
            });
            for (int j = i + 1; j < sorted.Count; j++)
            {
                if (used[j]) continue;
                if (Iou(a, sorted[j]) > IouTh) used[j] = true;
            }
        }
        sw.Stop();
        return new Dictionary<string, object> {
            { "faces", faces }, { "count", faces.Count },
            { "ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1) },
            { "backend", Backend }, { "engine", "animeface-v1.4n" },
        };
    }
}
