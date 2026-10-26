using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// ============ 场景语义分类（Chinese-CLIP image encoder，零样本） ============
static class ClipScene
{
    const int Size = 224;
    static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
    static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };
    static InferenceSession _sess;
    static bool _init;
    static string _err;
    static readonly object Gate = new object();
    static string[] _labels;
    static float[][] _labelEmb;
    public static string Backend = "cpu";

    static volatile bool _warming;
    public static bool IsReady => _init && _err == null;
    public static void Warmup() { try { EnsureInit(); } catch { } }
    public static void WarmupAsync()
    {
        if (_init || _warming) return;
        _warming = true;
        System.Threading.Tasks.Task.Run(() => { try { EnsureInit(); } catch { } });
    }

    static void EnsureInit()
    {
        if (_init) { if (_err != null) throw new Exception(_err); return; }
        lock (Gate)
        {
            if (_init) { if (_err != null) throw new Exception(_err); return; }
            try
            {
                string baseDir = PaddleOcr.ModelDir();
                string dirLarge = Path.Combine(baseDir, "chinese-clip-large");
                string dirBase = Path.Combine(baseDir, "chinese-clip");
                string dir = File.Exists(Path.Combine(dirLarge, "image_encoder.onnx")) ? dirLarge : dirBase;
                string onnx = Path.Combine(dir, "image_encoder.onnx");
                string lbl = Path.Combine(dir, "labels.json");
                if (!File.Exists(onnx)) throw new Exception("找不到 chinese-clip(image_encoder.onnx)");
                if (!File.Exists(lbl)) throw new Exception("找不到 chinese-clip(labels.json)");
                var (sess, backend) = Providers.CreateSession(onnx);
                _sess = sess;
                Backend = backend;
                using var doc = JsonDocument.Parse(File.ReadAllText(lbl));
                var labels = new List<string>();
                var embs = new List<float[]>();
                foreach (var e in doc.RootElement.GetProperty("labels").EnumerateArray()) labels.Add(e.GetString());
                foreach (var e in doc.RootElement.GetProperty("embeddings").EnumerateArray())
                    embs.Add(e.EnumerateArray().Select(v => (float)v.GetDouble()).ToArray());
                _labels = labels.ToArray();
                _labelEmb = embs.ToArray();
            }
            catch (Exception e) { _err = e.Message; }
            _init = true;
        }
    }

    static float SampleBilinear(byte[] bgra, int w, int h, float fx, float fy, int ch)
    {
        int x0 = Math.Max(0, Math.Min(w - 1, (int)fx)), y0 = Math.Max(0, Math.Min(h - 1, (int)fy));
        int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
        float dx = fx - x0, dy = fy - y0;
        int c0 = (y0 * w + x0) * 4 + (ch == 0 ? 2 : ch == 1 ? 1 : 0);
        int c1 = (y0 * w + x1) * 4 + (ch == 0 ? 2 : ch == 1 ? 1 : 0);
        int c2 = (y1 * w + x0) * 4 + (ch == 0 ? 2 : ch == 1 ? 1 : 0);
        int c3 = (y1 * w + x1) * 4 + (ch == 0 ? 2 : ch == 1 ? 1 : 0);
        float v = bgra[c0] * (1 - dx) * (1 - dy) + bgra[c1] * dx * (1 - dy) + bgra[c2] * (1 - dx) * dy + bgra[c3] * dx * dy;
        return v;
    }

    static DenseTensor<float> BuildInput(byte[] bgra, int w, int h, int ox, int oy, int side)
    {
        float scale = (float)side / Size;
        var inp = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        for (int y = 0; y < Size; y++)
        {
            float fy = oy + (y + 0.5f) * scale - 0.5f;
            for (int x = 0; x < Size; x++)
            {
                float fx = ox + (x + 0.5f) * scale - 0.5f;
                for (int c = 0; c < 3; c++)
                {
                    float v = SampleBilinear(bgra, w, h, fx, fy, c) / 255f;
                    inp[0, c, y, x] = (v - Mean[c]) / Std[c];
                }
            }
        }
        return inp;
    }

    static float[] RunEncoder(DenseTensor<float> inp)
    {
        float[] emb;
        int dim;
        lock (Providers.Gate)
        {
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_sess.InputMetadata.Keys.First(), inp) };
            using var results = _sess.Run(inputs);
            var outT = results.First().AsTensor<float>();
            dim = outT.Dimensions[outT.Dimensions.Length - 1];
            emb = new float[dim];
            for (int i = 0; i < dim; i++) emb[i] = outT[0, i];
        }
        double norm = 0;
        for (int i = 0; i < dim; i++) norm += emb[i] * emb[i];
        norm = Math.Sqrt(Math.Max(1e-9, norm));
        for (int i = 0; i < dim; i++) emb[i] = (float)(emb[i] / norm);
        return emb;
    }

    // 区域嵌入（角色识别用）
    public static float[] Embed(byte[] bgra, int w, int h, int x, int y, int cw, int ch)
    {
        EnsureInit();
        if (_err != null) throw new Exception(_err);
        x = Math.Max(0, x); y = Math.Max(0, y);
        cw = Math.Min(cw, w - x); ch = Math.Min(ch, h - y);
        if (cw <= 0 || ch <= 0) return null;
        int side = Math.Min(cw, ch);
        int ox = x + (cw - side) / 2, oy = y + (ch - side) / 2;
        return RunEncoder(BuildInput(bgra, w, h, ox, oy, side));
    }

    // 输入 BGRA 帧 → 场景标签排序
    public static Dictionary<string, object> Run(byte[] bgra, int w, int h, int topK = 5)
    {
        EnsureInit();
        if (_err != null) throw new Exception(_err);
        var sw = Stopwatch.StartNew();
        // 中心裁剪正方面 + resize 224
        int side = Math.Min(w, h);
        int ox = (w - side) / 2, oy = (h - side) / 2;
        var inp = BuildInput(bgra, w, h, ox, oy, side);
        float[] emb = RunEncoder(inp);
        int dim = emb.Length;
        var scored = new List<(string label, float score)>();
        for (int j = 0; j < _labels.Length; j++)
        {
            var le = _labelEmb[j];
            double dot = 0;
            for (int i = 0; i < dim && i < le.Length; i++) dot += emb[i] * le[i];
            scored.Add((_labels[j], (float)dot));
        }
        sw.Stop();
        var ranked = scored.OrderByDescending(s => s.score).ToList();
        return new Dictionary<string, object> {
            { "labels", ranked.Take(Math.Max(1, topK)).Select(s => new Dictionary<string, object> { { "label", s.label }, { "score", Math.Round(s.score, 3) } }).ToList() },
            { "top", ranked.Count > 0 ? ranked[0].label : "" },
            { "ms", Math.Round(sw.Elapsed.TotalMilliseconds, 1) },
            { "backend", Backend },
        };
    }
}
