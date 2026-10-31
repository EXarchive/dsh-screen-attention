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
    public static (InferenceSession session, string backend) CreateSession(string modelPath, bool cpuOnly = false)
    {
        lock (Gate)
        {
        var avail = Available;
        var candidates = new List<string>();
        // OCR 小块用 CPU 更快：DirectML 每次 dispatch 有数十 ms 固定开销，小块完全被它吃掉。
        if (cpuOnly)
        {
            try
            {
                var o = new SessionOptions
                {
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_PARALLEL,
                    IntraOpNumThreads = Math.Max(4, Environment.ProcessorCount),
                    InterOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2),
                    EnableMemoryPattern = true,
                    EnableCpuMemArena = true,
                };
                o.AppendExecutionProvider_CPU(1);
                return (new InferenceSession(modelPath, o), "cpu");
            }
            catch { }
        }
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
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                switch (p)
                {
                    case "tensorrt": opts.AppendExecutionProvider_Tensorrt(0); break;
                    case "cuda": opts.AppendExecutionProvider_CUDA(0); break;
                    case "directml":
                        opts.EnableMemoryPattern = true;      // 复用内存规划（原来 false：每帧重规划）
                        opts.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                        opts.EnableCpuMemArena = true;
                        opts.AppendExecutionProvider_DML(0);
                        break;
                    default:
                        opts.AppendExecutionProvider_CPU(1);
                        opts.IntraOpNumThreads = Math.Max(4, Environment.ProcessorCount);
                        opts.InterOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2);
                        break;
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
