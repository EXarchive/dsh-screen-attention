using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

// ============ 推理后端选择：高性能独显（TensorRT/CUDA）> DirectML > CPU ============
static class Providers
{
    public static readonly object Gate = new object();
    static List<string> _available;

    public static List<string> Available
    {
        get
        {
            lock (Gate)
            {
                if (_available == null)
                {
                    try { _available = OrtEnv.Instance().GetAvailableProviders().ToList(); }
                    catch { _available = new List<string>(); }
                }
                return _available;
            }
        }
    }

    public static string Describe() => string.Join(",", Available);

    // 依次尝试已安装的 EP：TensorRT（独显+显存）→ CUDA → DirectML（核显/通用）→ CPU
    public static (InferenceSession session, string backend) CreateSession(string modelPath)
    {
        lock (Gate)
        {
        var avail = Available;
        var candidates = new List<string>();
        if (avail.Any(a => a.Contains("Tensorrt"))) candidates.Add("tensorrt");
        if (avail.Any(a => a.Contains("CUDA"))) candidates.Add("cuda");
        if (avail.Any(a => a.Contains("Dml"))) candidates.Add("directml");
        candidates.Add("cpu");

        foreach (var p in candidates)
        {
            try
            {
                var opts = new SessionOptions
                {
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                };
                switch (p)
                {
                    case "tensorrt": opts.AppendExecutionProvider_Tensorrt(0); break;
                    case "cuda": opts.AppendExecutionProvider_CUDA(0); break;
                    case "directml":
                        opts.EnableMemoryPattern = false;
                        opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                        opts.AppendExecutionProvider_DML(0);
                        break;
                    default: opts.AppendExecutionProvider_CPU(1); break;
                }
                var sess = new InferenceSession(modelPath, opts);
                return (sess, p);
            }
            catch { }
        }
        throw new Exception("无法创建推理会话（无可用 EP）: " + modelPath);
        }
    }
}
