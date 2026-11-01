# dsh-screen-attention (屏幕感知与智能注意力插件)

> DeepSeek Harness (DSH) 原生屏幕持续感知、ACG 角色识别与自动化操作插件。
> 架构设计：Node.js DSH 插件门控（60+ 工具） + C# .NET 9 高性能原生推理辅助进程（SaHelper）。

---

## 一、核心特性

1. **实时屏幕感知与理解**
   - **单流采集**：FrameHub 100ms 单流采集（GDI BitBlt ~17.8ms），多引擎共享帧缓冲，无订阅自动休眠。
   - **流式 OCR**：PaddleOCR PP-OCRv4 det+rec，基于 FNV 框签名缓存识别结果，每帧 OCR 耗时从 ~300ms 降至 70~110ms。
   - **目标检测**：YOLOv8n / YOLOv8m 双模型（带人体位置检测与身份绑定）。
   - **ACG 场景分类**：Chinese-CLIP ViT-L/14 (1.16GB ONNX, 768维向量)，内嵌 34 类二次元/游戏/VTuber/视觉小说语义标签。
   - **动漫脸检测**：YOLOv8n-face (12MB ONNX, NMS 0.45, ~28ms)，针对 ACG 画面进行高精度人脸定位。
   - **动态降频自适应**：静止画面自动跳过繁重模型计算（`staticSkipped` 计数可观测），每 10 帧（~1s）执行一次心跳兜底，首帧与场景未就绪帧立即执行全量推理；画面像素变动瞬时恢复实时推理。

2. **角色识别与认知体系**
   - **多参考画廊（Gallery）**：单个角色支持多张不同姿势/表情的 CLIP 裁剪特征嵌入，余弦相似度匹配（阈值 0.70）。
   - **未知角色自动聚类**：陌生角色自动聚类分配为`未知角色#N`，支持一键晋升（promote）或忽略（ignore）。
   - **角色知识库关联**：通过`saperm_char_note`写入背景知识，在识别到人物时一并输出（如 DeepSeek 蓝鲸拟人女仆）。
   - **原子持久化**：角色库采用 Temp + File.Replace 机制，杜绝并发写入损坏。

3. **双观察模式与互斥调度**
   - **连续解读（Interpret）**：~800ms 间隔，粗粒度帧差触发区域 OCR 与变化分类（内容变化 / 动态噪声 / 文字位移）。
   - **逐帧融合（Infer）**：逐帧融合 OCR + 物体 + 角色身份 + 动漫脸 + 场景，单模式稳定帧率 6.7~6.9 fps。
   - **互斥守护**：启动任一长时观察模式时，插件自动停止另一模式（返回 `autoStopped`），避免 DirectML/算力资源争用降级。

4. **审计与保活**
   - **长时观察 Hold**：长时观察期间插件强制持有 Helper 引用，杜绝闲置被杀。
   - **空闲保活**：调用结束后空闲 60 秒才延迟释放 Helper，避免连续调用反复产生 2.5s 冷启动。
   - **看门狗崩溃告警与状态自愈**：Helper 异常退出时自动向会话注入告警、记录 `helper_crash` 审计日志，并在 ~800ms 内重启恢复原观察模式（`infer` / `interpret` / `watch`）与急停锁状态。
   - **持久化审计日志**：自动按天轮转记录变化/崩溃/自愈事件至 `out/logs/observation-YYYY-MM-DD.jsonl`，提供 `saperm_history` 工具随时追溯。

5. **AI 可读数据流（stream）**：每帧识别压成 ~110 字节紧凑行（TX 增量语义 / CH 变化去重），落盘 `out/logs/stream-*.jsonl` + `stream-latest.txt`，用 `saperm_stream` 增量读取；静止时自动降载（零推理心跳 + 采集降频 + 5s 落盘节流）。

6. **四级权限门控与急停**
   - **L0 观察类**：免审批（识别、取色、截图、窗口列表、健康检查）。
   - **L2 普通输入**：会话级一次性授权（点击、滚动、文本输入、剪贴板、Excel 定位读写）。
   - **L3 高危输入**：本会话一次性审批（拖拽、双击），与 L2 授权相互独立。
- **长按输入**：`saperm_press` 按住鼠标/按键不放（10~10000ms），`saperm_click` 支持 `holdMs`。
   - **急停机制**：支持物理热键 `Pause/Break` 或 `saperm_stop` 100ms 瞬时锁死所有鼠标键盘注入。

---

## 二、目录结构

```
dsh-screen-attention/
├── permission-plugin/        # DSH Node.js 插件包
│   ├── package.json
│   └── lib/index.js          # 插件入口，注册 60 个 saperm_* 工具
├── helper/                   # C# .NET 9 高性能原生进程
│   ├── SaHelper.csproj
│   ├── Program.cs            # stdio JSON 行协议分发、FrameHub、Infer/Interpret 循环
│   ├── Providers.cs          # ONNX 推理后端路由 (TensorRT > CUDA > DirectML > CPU)
│   ├── PaddleOcr.cs          # PaddleOCR PP-OCRv4 识别引擎与 FNV 缓存
│   ├── YoloDetect.cs         # YOLOv8n / YOLOv8m 物体检测
│   ├── ClipScene.cs          # Chinese-CLIP ViT-L/14 场景分类与裁剪嵌入
│   ├── AnimeFaceDetect.cs    # YOLOv8n-face 动漫人脸检测
│   ├── CharId.cs             # 角色画廊匹配、未知聚类与原子持久化
│   └── models/               # 本地模型目录
│       ├── ch_PP-OCRv4_det_infer.onnx
│       ├── ch_PP-OCRv4_rec_infer.onnx
│       ├── yolov8n.onnx / yolov8m.onnx
│       ├── animeface/model.onnx
│       ├── chinese-clip-large/image_encoder.onnx
│       ├── characters.json
│       ├── characters-unknown.json
│       └── characters-notes.json
├── docs/                     # 架构蓝图与演进文档
│   ├── continuous-interpretation.md
│   ├── RETROSPECTIVE.md
│   └── UNFINISHED.md
├── tests/                    # 冒烟与回归测试脚本
└── README.md
```

---

## 三、快速开始

### 1. 编译 C# Helper
```powershell
cd helper
dotnet build -c Release
```
二进制生成在：`helper/bin/Release/net9.0-windows10.0.19041.0/SaHelper.exe`。

### 2. DSH 插件生效
若修改了插件或工具参数定义，需要重启 DSH 服务使 Web GUI 重新加载工具列表：
```powershell
# 重启 DSH Web 服务以更新插件
dsh web
```

### 3. 健康自检
在 DSH 会话中调用：
```json
saperm_health {}
```
返回样例：
```json
{
  "helper": { "online": true, "pid": 47124 },
  "paused": false,
  "observation": { "mode": null, "holds": 0 },
  "models": {
    "ch_PP-OCRv4_det_infer": true,
    "ch_PP-OCRv4_rec_infer": true,
    "yolov8m": true,
    "animeface": true
  },
  "clip": "chinese-clip-large",
  "faceModel": true,
  "characters": 2,
  "unknowns": 0,
  "ok": true
}
```

---

## 四、核心工具使用场景与策略

| 类别 | 工具名 | 说明 | 推荐使用方式 |
|---|---|---|---|
| **自检** | `saperm_health` | 投产自检 | 每次会话首次使用前执行确认模型与后端正常 |
| **审计** | `saperm_history` | 历史日志读取 | 传入 `limit` 或 `type: "infer"|"interpret"` 查看历史变化 |
| **轻量监控** | `saperm_interpret_start` | 连续屏幕解读 | 日常监控屏幕内容变化，低资源占用（默认 800ms） |
| **深度理解** | `saperm_infer_start` | 逐帧融合分析 | 遇到游戏画面、动漫角色、复杂 UI 交互时切入 |
| **小窗注意** | `saperm_overlay_open` | 打开悬浮小窗 | 480x270 屏幕右下角红框可视化 AI 当前视线与注视点 |
| **角色管理** | `saperm_char_list` | 已知角色清单 | 查看当前画廊注册的角色列表 |
|  | `saperm_char_register` | 注册角色特征 | 裁剪画面人物并写入特征与可选背景备注 |
|  | `saperm_char_note` | 更新角色知识 | 记录或丰富角色的百科描述与上下文 |
|  | `saperm_char_unknowns` | 查看未知聚类 | 检视陌生人物出现统计并决定是否 promote |
|  | `saperm_char_promote` | 晋升未知角色 | 将 `未知角色#1` 命名为正式角色名 |
| **自动化输入** | `saperm_click` / `press` / `type` / `hotkey` | 鼠标键盘操作（含长按 holdMs） | 基于 OCR 坐标或 UIA 控件进行前台自动化交互 |
| **急停** | `saperm_stop` | 立即急停 | 锁定输入注入；恢复需 `saperm_resume` 并经由用户明确授权 |

---

## 五、运行指标与资源要求

- **算力平台**：支持 DirectML（本机 iGPU/CPU 兼容）与 CUDA/TensorRT（自动检测）。
- **内存占用**：Helper 全模型加载后 WorkingSet 约 2.5GB。
- **磁盘占用**：ONNX 运行时模型约 1.6GB。
- **运行性能**：
  - 单模式 Infer（含 OCR、YOLOv8m、CLIP 场景、动漫脸）：**6.7 ~ 6.9 fps**（DirectML 环境）。
  - 静止画面自适应降频模式：CPU 占用率从 80% 降至 <10%。
