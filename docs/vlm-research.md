# 本地视觉语义模型（VLM）调研 — t3

> 目标：给 dsh-screen-attention 的识图引擎加"本地语义理解"层（离线、不依赖云端 vision-router）。
> 硬件前提（本机）：Intel UHD 770 iGPU（DirectML / D3D11 FL 0xB000）、20 核 CPU、**无独显 NVIDIA GPU**。内存共享显存。
> 结论速览：**1 秒内语义理解只有 CLIP 型（embedding/zero-shot 分类）可靠做到；2B 级 VLM 在本机 iGPU 上无法稳定 1 秒内。** 推荐两层架构：CLIP 常驻快通道 + 按需慢通道 VLM。

---

## 1. 候选模型与下载源

### 1.1 Qwen2-VL / Qwen2.5-VL / Qwen3-VL（推荐主线）

| 模型 | 参数 | 特点 | 下载源 |
|---|---|---|---|
| Qwen2-VL-2B-Instruct | 2B | 2024，llama.cpp 已支持（2024-12 起） | HF `Qwen/Qwen2-VL-2B-Instruct`；GGUF `bartowski/Qwen2-VL-2B-Instruct-GGUF`（Q4_K_M≈**0.99GB**、f16≈3.09GB） |
| Qwen2.5-VL-3B-Instruct | 3B | 2025-01，2B 的升级，视觉编码器更省 | HF `Qwen/Qwen2.5-VL-3B-Instruct`；GGUF `unsloth/Qwen2.5-VL-3B-Instruct-GGUF`、`bartowski/Qwen_Qwen2.5-VL-3B-Instruct-GGUF`（Q4_K_M≈**1.8GB**）；ModelScope 镜像 `bartowski/Qwen_Qwen2.5-VL-3B-Instruct-GGUF` |
| Qwen3-VL-2B-Instruct | 2B | 2025 最新 | GGUF `unsloth/Qwen3-VL-2B-Instruct-GGUF`（Q4 约 1.13–1.61GB） |
| Qwen2-VL-7B / Qwen2.5-VL-7B | 7B | 生产甜点，本机太慢 | HF `Qwen/Qwen2.5-VL-7B-Instruct` |

- **Ollama 捷径**：`ollama run qwen2.5vl:3b`（tags: `3b`/`7b`/`72b`），OpenAI 兼容 `/api/chat`，零代码本地部署。库页 https://ollama.com/library/qwen2.5vl
- llama.cpp 支持情况：Qwen2-VL 静态图已支持（mtmd/CLIP projector，2024-12）；Qwen2.5-VL 静态图已支持（视频是 open feature req，见 https://github.com/ggml-org/llama.cpp/issues/17660）。
- 注意：Qwen2-VL-2B GGUF 需配套 vision projector `mmproj-*.gguf`（f16），`llama-mtmd-cli` / `llama-server` 加载时用 `--mmproj`。

### 1.2 InternVL2 / InternVL2.5（文档/UI 语义最强，但部署最难）

| 模型 | 参数 | 特点 | 下载源 |
|---|---|---|---|
| InternVL2.5-2B | 2.21B（InternViT-300M-448px + internlm2.5-1.8b-chat） | 小模型里 OCR/Doc/Chart 最强（DocVQA 88.7、OCRBench 804） | HF `OpenGVLab/InternVL2_5-2B`；ModelScope `OpenGVLab/InternVL2_5-2B` |
| InternVL2-2B | 2B | 上一代 | HF `OpenGVLab/InternVL2-2B` |

- InternVL2.5 全系：1B/2B/4B/8B/26B/38B/78B。
- **关键坑**：llama.cpp 原生**不支持 InternVL**（社区讨论确认）；GGUF 转换仅有社区小众版（`mradermacher/InternVL2-1B-GGUF`、`cstr/internvl2.5-2b-crispembed-GGUF` 仅用于 embedding）。官方路线是 transformers / vLLM / LMDeploy / TRT-LLM。
- 结论：语义能力好，但与本项目 .NET helper 集成成本最高 → **优先级放低**，仅当需要"第二引擎"且接受 Python 侧进程时再上。

### 1.3 中文 CLIP（分类/embedding，1 秒内唯一可靠项）

| 模型 | 特点 | 下载源 |
|---|---|---|
| Chinese-CLIP ViT-B/16 | 官方，支持转 ONNX/TensorRT | HF `OFA-Sys/chinese-clip-vit-base-patch16`；转换说明 https://github.com/OFA-Sys/Chinese-CLIP/blob/master/deployment_En.md |
| Chinese-CLIP ViT-B/16（已转 ONNX） | **直接可用**，opset 18，224×224，CLIP mean/std 归一化，含 tokenizer | HF `felixdu/chinese-clip-vit-base-patch16-onnx` |
| Chinese-CLIP ViT-L/14（transformers.js ONNX） | 更大更强 | HF `Xenova/chinese-clip-vit-large-patch14` |
| Taiyi-CLIP-Roberta-102M/326M-Chinese | 首个开源中文 CLIP，小 | HF `IDEA-CCNL/Taiyi-CLIP-Roberta-102M-Chinese` |

- 用途：zero-shot 屏幕类型分类（"登录页/代码编辑器/视频播放器/终端/游戏/文档/空桌面"）、图文检索、内容过滤、"是否值得细看"的 relevance 门控。

### 1.4 极简 captioning（1 秒边缘选手，中文弱）

- **SmolVLM-Instruct**（256M/500M/2.2B）：<1GB 显存、prefill 比 Qwen2-VL 快 3.3–4.5x、生成快 7.5–16x，浏览器 WebGPU 40–80 tok/s。英文为主。HF `HuggingFaceTB/SmolVLM-Instruct`。
- **Moondream2**（1.86B）：3.87GB 显存、极快、caption 专用、英文。HF `vikhyatk/moondream2`。

---

## 2. 集成方案（.NET / C# helper 视角）

### 2.1 ONNX Runtime GenAI（Microsoft.ML.OnnxRuntimeGenAI）
- 原生只支持 **Phi-3 / Phi-3.5-vision**（`microsoft/Phi-3.5-vision-instruct-onnx`，CPU/GPU 优化）；Qwen2-VL / Qwen2.5-VL 视觉**不支持**（[onnxruntime-genai#1790](https://github.com/microsoft/onnxruntime-genai/discussions/1790) 仍在讨论）。
- → **不适用于 Qwen/InternVL**，仅当想用 Phi-3.5-vision 时才走这条（英文、语义一般）。

### 2.2 LLamaSharp（SciSharp/LLamaSharp）+ GGUF
- .NET 绑定 llama.cpp，支持 GGUF；多模态走 `LLava` executor，但 Qwen2-VL 支持不成熟（[#1136](https://github.com/SciSharp/LLamaSharp/issues/1136) 加载 llava 报错等）。
- → 文本 LLM 可用；**VLM 支持不稳定**，不推荐作为主路。

### 2.3 llama.cpp `llama-server` / `llama-mtmd-cli` 作为兄弟进程（**推荐**）
- llama.cpp 支持 Qwen2-VL / Qwen2.5-VL 静态图；`llama-server` 提供 OpenAI 兼容 HTTP API。
- 命令范式（来自社区已验证）：
  ```
  ./llama-mtmd-cli -m Qwen2-VL-2B-Instruct-Q4_K_M.gguf \
      --mmproj mmproj-Qwen2-VL-2B-Instruct-f16.gguf \
      --image shot.png -p "识别这张图片中的文字" -n 256
  ```
- 与现有架构契合：helper 是 C# 常驻进程，已有"看门狗 3s 重启"模式 → 再加一个受管的 `llama-server` 兄弟进程，helper 走 localhost HTTP 调用即可。GGUF 单文件分发、无 Python 依赖。

### 2.4 Ollama（捷径，若已装）
- `ollama run qwen2.5vl:3b` + `/api/chat`（`images: [base64]`）。零代码，OpenAI 兼容。DSH vision-router 本就声明支持 Ollama 本地后端。

### 2.5 CLIP via ONNX Runtime（进程内，**快通道主路**）
- Chinese-CLIP ONNX（`felixdu/chinese-clip-vit-base-patch16-onnx`）直接在 helper 内用 ONNX Runtime（CPU 或 DirectML）推理，**复用 OCR 已规划的 onnxruntime**，无额外进程。
- 推理范式：`image`(1,3,224,224) + `input_ids`/`attention_mask` → `image_emb`、`text_emb`、`logit_scale`；scores = logit_scale × (image_emb · text_embᵀ)。C# 侧有成熟示例（ONNX Runtime C# + CLIP zero-shot）。

---

## 3. 1 秒内语义理解的可行性（本机 iGPU 实测预估）

| 方案 | 硬件 | 预估延迟 | 1 秒内？ |
|---|---|---|---|
| Chinese-CLIP ViT-B/16 zero-shot 分类 | CPU / iGPU(DirectML) | 图像 embedding 20–60ms（CPU）/<20ms（iGPU），标签文本 embedding 预计算 | ✅ **稳**（总 <150ms） |
| Chinese-CLIP 图文检索/内容过滤 | CPU / iGPU | 同上 | ✅ |
| SmolVLM-256M / Moondream2 caption | GPU(WebGPU) / CPU | GPU 40–80 tok/s → 20–30 token 约 0.5–1s；CPU 1–3s | ⚠️ 边缘可行，中文弱 |
| Qwen2-VL-2B Q4_K_M（0.99GB） | iGPU(UHD770) / 20 核 CPU | 短 caption 2–6s | ❌ |
| Qwen2.5-VL-3B Q4（1.8GB） | 同上 | 3–8s | ❌ |
| Qwen2-VL-7B / InternVL2.5-8B | 同上 | 8–20s+ | ❌ 完全不行 |

> 结论：**"1 秒内语义理解"在本机只能由 CLIP 型 embedding/分类可靠达成**；free-form VLM caption 无法 1 秒内。故把"语义"拆成两层。

---

## 4. 推荐方案（两层架构）

```
识别结果/截图
   │
   ├─ 快通道（常驻、<150ms、进程内）: Chinese-CLIP ONNX @ ONNX Runtime
   │     zero-shot 屏幕类型分类 + relevance 门控 + 图文 embedding（语义 diff/检索）
   │
   └─ 慢通道（按需、2–5s、兄弟进程）: llama-server 加载 Qwen2.5-VL-3B Q4_K_M GGUF
         自然语言"这张屏幕在干什么"描述，仅在 AI 明确索取/CLIP 判定值得细看时触发
```

**落地顺序建议**：
1. **先做快通道**（Chinese-CLIP ONNX 进程内），成本最低、确定 <1s、复用 onnxruntime。
2. **慢通道首选 llama-server + Qwen2.5-VL-3B Q4_K_M GGUF（~1.8GB）**；追求更低延迟换 Qwen2-VL-2B Q4（~1GB）。若本机已装 Ollama，先用 `qwen2.5vl:3b` 验证效果。
3. InternVL2.5-2B 文档/UI 语义最强，但 llama.cpp 不支持、.NET 集成成本最高 → 仅作备选，且需接受 Python 侧进程（vLLM/LMDeploy）。

**边界提醒**：本地 VLM 是"语义理解"层（这是什么/值不值得看/自然语言描述），**不替代 PaddleOCR 做逐字 OCR**（PaddleOCR 快且准）；两者是"识别文字 + 理解含义"的分工。
