# 硬件自适应推理设计 — t4

> 目标：helper 运行时探测本机硬件（CPU / 核显 iGPU / NPU / 独显 dGPU），自动选择最优推理后端并支持降级，覆盖两种推理引擎——ONNX Runtime（PaddleOCR det/rec、Chinese-CLIP）与 llama.cpp（VLM 慢通道）。
> 现状锚点（来自 BLUEPRINT.md 与 t1/t3）：helper = dotnet 9 常驻单文件 exe；已规划 onnxruntime-directml 1.24.4（B3 GPU OCR）；本机 Intel UHD 770 + DirectML 1.15.5 + D3D11 FL 0xB000，20 核 CPU，无独显；t3 定 VLM 慢通道走 llama-server GGUF。

---

## 1. 硬件四类与目标后端矩阵

| 硬件 | 典型代表 | ONNX Runtime 后端 | llama.cpp 后端 | 适用负载 |
|---|---|---|---|---|
| CPU | x86 AVX2/AVX-512 | CPU EP（默认，`Microsoft.ML.OnnxRuntime`） | CPU（AVX2/AVX-512） | 全部（兜底，永远可用） |
| 核显 iGPU | Intel UHD/Arc、AMD Radeon 核显 | **DirectML EP**（`Microsoft.ML.OnnxRuntime.DirectML`）；或 WinML 内置 DirectML | Vulkan / SYCL（Intel） | OCR/CLIP（VLM 慢但可用） |
| NPU | Intel Core Ultra NPU、高通 Snapdragon X NPU | **OpenVINO EP**（Intel，.NET 无官方 NuGet，经 WinML 或 OpenVINO 原生）；**QNN EP**（高通，`Microsoft.ML.OnnxRuntime.QNN`） | 不支持（NPU 无 llama.cpp 后端） | OCR/CLIP（int8 量化模型） |
| 独显 dGPU | NVIDIA RTX / AMD | **CUDA EP**（`Microsoft.ML.OnnxRuntime.Gpu`）、**TensorRT EP**（`Microsoft.ML.OnnxRuntime.TensorRT`）、DirectML | **CUDA**（NVIDIA）/ HIP（AMD） | 全部（VLM 可达 1 秒内） |

**关键 .NET 现实**（务必在实现前确认，避免踩坑）：
1. 官方 .NET NuGet 提供：CPU、`Gpu`(CUDA)、`TensorRT`、`DirectML`、`QNN`、`OpenVino`(社区/官方均不完整)。**OpenVINO EP 无稳定的一等 .NET NuGet**（官方 `onnxruntime-openvino` 是 Python/C++ 包）。Intel NPU 在 .NET 下的现实路径是：**WinML（`Microsoft.AI.MachineLearning`，内置 OpenVINO/QNN/DirectML/NvTensorRtRtx EP）** 或 OpenVINO 原生 C# 绑定（OpenVINOSharp）。
2. **DirectML EP 处于维护模式**：onnxruntime 官方 install 文档已标注 "DirectML (sustained engineering — use WinML for new projects)"；DirectML#682 也明确 "DirectML is in maintenance mode"（2025-07）。→ 本机 iGPU 用 DirectML 仍可行（已实测），但**新代码优先考虑 WinML 作为 Windows 统一后端**，避免押注一个进入维护的 EP。
3. ORT 已知问题 #25557：`GetEpDevices()` 无法发现 Intel NPU → **NPU 探测不能只靠 ORT**，需走 WinML/OpenVINO/设备枚举（见 §2）。
4. 多个 EP NuGet 会显著增大单文件 exe（每个 100MB+ 量级）。→ 后端按"可选插件"延迟加载，而不是全打包。

---

## 2. 硬件检测层（运行时，一次性，结果缓存）

### 2.1 CPU
- `Environment.ProcessorCount`（本机 20）、`RuntimeInformation.ProcessArchitecture`（x64/ARM64）。
- 判断 ARM64 用于区分高通 NPU（Copilot+ PC = ARM64）。
- 指令集检测（AVX2/AVX-512）可用 `System.Runtime.Intrinsics.X86` 的 `Avx2.IsSupported`（.NET 内置，无需 P/Invoke）。

### 2.2 GPU（核显 vs 独显、厂商）
- 用 **Vortice.DXGI**（BLUEPRINT §J-6 已选 Vortice.Windows 做 M2 采集）枚举 `IDXGIAdapter`：
  - `VendorId`：0x8086=Intel、0x10DE=NVIDIA、0x1002=AMD。
  - `DedicatedVideoMemory`：区分独显（几 GB 专用）vs 核显（共享内存/专用 ≈0）。
  - 可选 `DXGI_ADAPTER_FLAG_SOFTWARE` 过滤软件适配器。
- 输出：`GpuInfo[] { vendor, isDiscrete, vramBytes, description }`。

### 2.3 NPU
三种路径，按可用性组合：
1. **WinML 设备枚举**（若引 WinML）：`Windows.AI.MachineLearning.LearningModelDevice` 系列 / `LearningModelDeviceKind.NeuralProcessor` 探测。
2. **OpenVINO 原生**（若引 OpenVINOSharp）：`ov.Core().AvailableDevices` 是否含 `"NPU"`。
3. **设备树兜底**：SetupAPI/`Windows.Devices.Enumeration` 按 PNP 设备类（Intel NPU 类 GUID、Qualcomm 神经处理器）枚举——仅当 1/2 不可用时用。
- 注意 ORT `GetEpDevices` 对 Intel NPU 无效（#25557），**不要**作为 NPU 探测主手段。

### 2.4 探测原则
- **探测与选择分离**：探测只描述硬件（静态、快）；选择用"真实会话创建"做最终裁决（见 §4），因为 EP 是否真能用、op 是否被 EP 支持，只有建 session 才知道。
- 探测结果落盘缓存（`%LOCALAPPDATA%\dsh-screen-attention\hardware.json`，含时间戳），下次启动跳过，检测失败/硬件变化时重测。

---

## 3. 后端注册表与能力分

```
BackendSpec {
  id: "cpu" | "dml" | "cuda" | "trt" | "openvino" | "qnn" | "winml-dml" | "winml-npu" | "llama-cpu" | "llama-vulkan" | "llama-cuda"
  engine: OnnxRuntime | LlamaCpp | WinML
  requires: { vendor?, discrete?, arm64?, npu?, minVram? }   // 硬件门槛，null=不限
  onnxProviders: [ ... ]                                     // ORT 用
  llamaBackend: "cpu"|"vulkan"|"sycl"|"cuda"|"hip"           // llama.cpp 用
  modelTag: "fp32"|"int8"|"fp16"                             // 需要的模型量化档位
  score: 0..100                                              // 同硬件内的偏好分
}
```

打分基准（同硬件可用时）：`tensorrt > cuda > dml/winml > cpu`（dGPU）；`openvino-npu > dml > cpu`（Intel NPU 机）；`dml > cpu`（核显机）；CPU 恒为兜底 score=0。

---

## 4. 后端自动选择算法（probe + 决策缓存）

```
detectHardware() -> HardwareInfo
for workload in [ocr_det, ocr_rec, clip, vlm]:
    candidates = backends.Where(b => b.engine 匹配 workload 且 b.requires 被 HardwareInfo 满足)
                          .OrderByDescending(score)
    selected = null
    foreach c in candidates:
        if probe(c, workload) 成功:        // 用该后端建一个小模型/真模型 session 并跑一次 1x1 输入
            selected = c; break
    if selected == null: selected = cpu   // 最终兜底
    decision[workload] = selected          // 缓存到进程内存 + 落盘
```

- **probe 规则**：EP 是否可用、模型是否加载成功、op 是否被 EP 支持，全部用"真建 session + 一次推理"验证（这是 ORT 唯一可靠的判据，比 vendor 判断更硬）。失败吞掉异常继续下一候选。
- **模型按需动态加载**：每个 workload 的模型文件不随 exe 打包，按 `modelTag`（fp32/fp16/int8）从 `helper/models/` 延迟加载；只有被选中后端需要的量化档位才下载/加载。
- **模型档位矩阵**（PaddleOCR/CLIP）：CPU 用 fp32；DirectML 用 fp32（或 fp16）；CUDA/TensorRT 用 fp16；NPU（QNN/OpenVINO）用 **int8 量化**（NPU 通常要求 int8，见 OpenVINO NPU 仅支持 int8 权重；PaddleOCR 可用 PaddleSlim 量化或 ONNX 动态量化）。

---

## 5. 降级链（degradation chain）

**静态降级（启动时）**：§4 的候选序本身即降级链（探测失败逐级下探，最终 CPU）。

**运行时降级（运行中触发）**，触发条件与动作：

| 触发条件 | 判定 | 动作 |
|---|---|---|
| 后端 session 抛异常 / OOM | 建 session 或推理时捕获 | 标记该 backend 失效，落到下一候选，重建 session |
| 延迟超标 | 该 workload 平均延迟 > 预算（如 OCR > 300ms、CLIP > 500ms）连续 N 次 | 降到下一候选（通常到 CPU），并记降级原因 |
| 后端进程崩溃（VLM llama-server） | 看门狗检测退出码/心跳超时 | 看门狗 3s 重启（复用现有模式），重启仍失败→标记不可用并回 CPU 后端或直接返回"本地 VLM 不可用" |
| 硬件热插拔/驱动变化 | 检测失败或 EP 消失 | 清空决策缓存，重新走 §2 探测 + §4 选择 |

**每次降级都写审计**：`{workload, from, to, reason, ts}`，进 helper 日志并通过工具结果暴露给 AI（AI 可感知"当前本地推理已降级到 CPU"）。

**降级链顺序（默认）**：
- OCR/CLIP（ONNX 路径）：`TensorRT → CUDA → DirectML/WinML → CPU`（dGPU 机）；`OpenVINO-NPU → DirectML → CPU`（Intel NPU 机）；`DirectML → CPU`（核显机，本机）。
- VLM（llama.cpp 路径）：`llama-cuda → llama-vulkan/sycl → llama-cpu`；NPU 机无 llama 后端 → VLM 直接走 CPU 或关闭（NPU 只服务 OCR/CLIP）。

---

## 6. C# 接口骨架（示意）

```csharp
enum EngineKind { OnnxRuntime, LlamaCpp, WinML }
record HardwareInfo { int CpuCores; Arch Arch; GpuInfo[] Gpus; bool HasNpu; string NpuVendor; }
record GpuInfo { string Vendor; bool IsDiscrete; ulong VramBytes; string Desc; }
record BackendSpec { string Id; EngineKind Engine; HardwareReq Requires; int Score; string ModelTag; }
interface IInferenceBackend {
    string Id { get; }
    bool Probe(string workload, string modelPath);        // 建 session + 一次推理
    IInferenceSession Create(string workload, string modelPath);
}
// 调度器
class BackendRouter {
    HardwareInfo Detect();                                  // §2
    IInferenceBackend Select(string workload, string modelPath);  // §4 probe 链
    void OnRuntimeFailure(workload, backend, reason);       // §5 降级
    void OnHeartbeatLost(workload, backend);                // VLM 进程崩溃
}
```

- OCR 与 CLIP 用 ONNX Runtime：`InferenceSession` 按选中的 `onnxProviders` 创建；TensorRT/CUDA/DirectML/QNN 通过对应 NuGet 注册 provider。
- VLM 用 llama-server 子进程：`BackendRouter` 根据 `llamaBackend` 选 llama.cpp 构建（CUDA/Vulkan/SYCL/CPU），启动 `llama-server` 时传 `-ngl`（offload 层数）、后端相关参数，helper 走 localhost OpenAI 兼容 API；看门狗负责存活。

---

## 7. 落地建议（分期）

1. **M1（本机即可交付）**：硬件检测层（CPU 核数 + DXGI GPU 枚举，Vortice.DXGI）+ `BackendRouter` 骨架 + ONNX 路径 `DirectML → CPU` 两级（本机 UHD770 实测基线，OCR det/rec + CLIP）。DirectML 已实测可用（BLUEPRINT §1/B3），风险最低。
2. **M2**：接入 CUDA/TensorRT 分支（仅在检测到 NVIDIA 独显时生效，本机不会触发，代码走"候选未命中"分支即可，逻辑上覆盖 dGPU）。VLM 的 llama.cpp 后端选择（CUDA/Vulkan/SYCL/CPU）随 t3 慢通道一起落地。
3. **M3（NPU，可选/延后）**：Intel NPU 走 WinML 或 OpenVINO 原生（需 int8 量化模型 + 单独下载档位）；高通 NPU 走 QNN EP（`Microsoft.ML.OnnxRuntime.QNN`，仅 ARM64 生效）。**先做检测与占位，后端接插件化，不阻塞 M1/M2**——NPU 是四类里 .NET 集成最不成熟、收益最不确定的一类。
4. **贯穿**：运行时降级链 + 审计（§5）从 M1 就有，因为"CPU 兜底"是永远存在的底线，降级是这套设计里最重要的可靠性保证。

**设计取舍提醒**：DirectML EP 已进入维护模式（官方建议新项目用 WinML）。短期（本机核显）继续用 DirectML 无碍且已实测；中期若要统一"核显+NPU"两条 Windows 路径，考虑迁到 WinML（`Microsoft.AI.MachineLearning`）作为 Windows 统一后端，CUDA/TensorRT 仍走 onnxruntime-gpu/tensorrt（NVIDIA 独显场景）。
