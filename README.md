# dsh-screen-attention

DSH 屏幕感知与桌面操作插件。C# helper（ONNX 本地推理 + WinForms 浮窗）+ JS 工具层（**45 个 `saperm_*` 工具**），通过 stdio JSON-RPC 通信。

- 本地推理：OCR / 物体检测 / 场景分类 / 动漫脸 / 角色识别，0 token 消耗
- 内置 VLM 运行时（vendor/，可选）：本地看懂画面内容，不依赖系统安装任何东西
- 浮窗同框：画面 16:9 在上、讲解字幕在下、状态条最底，三分区不重叠
- 运行时零控制台：所有子进程静默启动；生命周期全绑定，关一个全关

---

## 安装

### 1. 装插件

```powershell
dsh plugin add dsh-screen-attention
```

开发模式：profile 的 `cordis.patch.yml` 指向本地 `permission-plugin` 目录。改了工具注册必须**重启 DSH**（热重载不刷新工具注册表）。

### 2. 编译 helper

```powershell
cd helper
dotnet build -c Release
# 产物: helper/bin/Release/net9.0-windows10.0.19041.0/SaHelper.exe
```

插件首次调用工具时自动拉起 helper，无需手动启动。

### 3. 模型

**随仓库自带**（无需下载）：PP-OCRv4 det/rec、YOLOv8n、动漫脸检测。

**需手动放置**（>50MB 不入 git）：

| 文件 | 大小 | 用途 |
|---|---|---|
| `helper/models/chinese-clip*/image_encoder.onnx` | ~1.16GB | 场景分类 / 角色特征嵌入 |
| `helper/models/yolov8m.onnx` | ~50MB | 精确物体检测（`infer accurate:true`） |

**可选：VLM 运行时**（`saperm_vision` / `saperm_vlm` / `saperm_look` / `saperm_narrate` 需要，共 ~4.3GB）：

```powershell
# 1) 下载 ollama 免安装版（~1.5GB）到 %TEMP%\ollama-win.zip
Invoke-WebRequest -Uri "https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64.zip" \
  -OutFile "$env:TEMP\ollama-win.zip"

# 2) 一键装配：解压到 vendor/ollama + 拉取 qwen2.5vl:3b 到 vendor/models
powershell -ExecutionPolicy Bypass -File vendor/setup-runtime.ps1
```

装配后**无需任何手动启动**：插件调用 VLM 工具时自动探测并静默拉起 `vendor/ollama/ollama.exe`；DSH 退出时自动回收（`dispose` + exit hook）。模型目录经 `OLLAMA_MODELS` 指向 `vendor/models`，与系统安装的 Ollama 完全隔离——本插件不写注册表、不装服务、不进 PATH。

### 4. 验证

会话里调用 `saperm_health {}`：`helper.online=true`、`ollama.exePresent=true`（装了 VLM 时）、`ok=true` 即就绪。

---

## 工具清单（47 个）

### 看懂屏幕（L0 观察，免审批）

| 工具 | 用途 |
|---|---|
| `see` | **看一次（首选）**：一张图同时出文字+坐标 / 物体 / 场景 / 运动，自动开浮窗写字幕，可 `inject:true` 推送会话 |
| `look` | **看并说出来**：抓画面 → VLM 理解 → 字幕推浮窗，一次调用完成 |
| `narrate` | **实时讲解**：循环在插件内跑，按节奏抓最新帧→VLM→浮窗字幕；一次调用驱动全程，停止即关窗 |
| `vlm` | 本地视觉理解（Qwen2.5-VL）：问"画面里有什么"，热态 ~0.8s 出中文描述 |
| `vision` | **识图（只识别，零副作用）**：喂图片文件(`path`)或屏幕区域/窗口，只调识别引擎返回结果——不开浮窗、不推送会话、不启动采集。`engine` = `vlm`(默认) / `ocr` / `both` |
| `view` | 截指定区域返回图片路径（多尺度观察，配合 rect 对应大图坐标） |
| `glance` | 全屏缩略大图（~560 token），快速语义浏览 |
| `sheet` | 多帧拼图：按画面变化选帧拼一张网格，一次看清一段时间 |
| `recognize` | OCR 文字 + UIA 控件一次返回（带坐标） |
| `locate` | 按关键字找屏幕元素坐标（"哔哩哔哩"按钮在哪） |
| `window` | `list` 列窗口 / `shot` 抓**后台被遮挡**窗口（PrintWindow，不靠屏幕） |
| `web` | 无头浏览器渲染网页截图，不建窗口不抢焦点 |
| `animeface` | 动漫脸检测（框+置信度） |
| `audio` | 听系统输出/麦克风：电平、是否人声（判断"有没有人在说话"） |

> 只想读图、不要任何副作用：`vision({ path })`（看图说话）/ `vision({ path, engine:'ocr' })`（只要文字）。
> 旧版 scan/detect/scene/ocr_file 均已并入 `see`。

### 持续观察（L0）

| 工具 | 用途 |
|---|---|
| `watch` | 统一入口：`start/stop/status/link/unlink/focus`。自适应采样（AIMD，200-1600ms），`link` 把屏幕变化事件注入会话 |
| `interpret` | 连续解读：`start/status/stop`。变化区域 OCR + 内容/噪声分类，节流注入 |
| `infer` | 逐帧识别：`start/status/stop`。每帧检测+识别+角色身份，结果进数据流 |
| `stream` | AI 可读数据流：`read/start/stop`。每帧一行紧凑文本，token 极省 |
| `observe` | 单次变化快照（运动区域/变化率/区域文字），静止时瞬时返回 |
| `observe_task` / `observe_task_stop` | 定时持续观察任务（防打断模式，自动汇报+抓图注入） |
| `frames` | 高帧率采集流水线（默认 60fps 逐帧记录，不丢帧） |
| `calibrate` | 自动标定最优采样间隔（逐档实测能否抓到变化） |
| `target` | 锁定观察目标窗口（后续观察工具只看它的客户区，自动跟随） |
| `region` | 让用户拖框选区，之后观察工具复用 |
| `history` | 观察审计日志（JSONL 落盘，跨重启可追溯） |

### 输出与表达（L0）

| 工具 | 用途 |
|---|---|
| `overlay` | 浮窗统一入口：`open/close/set`。红框标记注意力区域、注视点、底部标签、**讲解字幕**（与画面同框分区显示） |
| `say` | 气泡文字 + Windows SAPI 朗读（`speak:false` 只显示不出声） |
| `speech_voices` | 列出系统 TTS 音色 |

### 桌面输入（L2 会话级授权 / L3 高危单独授权）

| 工具 | 用途 |
|---|---|
| `click` / `doubleclick`(L3) | 点击 / 双击屏幕坐标（`holdMs` 长按） |
| `press` | 按住不放（鼠标键或键盘键，游戏移动/长按菜单） |
| `drag`(L3) | 坐标拖拽 |
| `scroll` | 滚轮（自动先移光标到目标窗口） |
| `type` / `hotkey` | 输入文本 / 组合键（`["CTRL","C"]`） |
| `clipboard` | 剪贴板 `get/set` |
| `focus` / `topmost` | 激活窗口 / 临时置顶 |
| `stop` / `resume` | **急停**（锁全部输入，永远可用）/ 恢复（需用户确认） |

### 业务组合（L0 观察 / L2 写入）

| 工具 | 用途 |
|---|---|
| `excel` | Excel 录入：`goto/write/read/cells`（UIA 定位 + 剪贴板粘贴 + 回读校验） |
| `fill` | 图片填表流水线：`start/status/next/submit/write/abort`（批量图片→提取字段→写 Excel） |
| `char` | 角色管理：`register/match/list/rename/merge/delete/note/unknowns/promote/ignore`（CLIP 嵌入识别动漫角色） |

### 诊断（L0）

| 工具 | 用途 |
|---|---|
| `health` | 投产自检：helper/模型/角色库/观察状态/**ollama 运行时** |
| `status` | 权限模块状态（授权/急停/helper 进程） |
| `profile` | 探测本机方案：local（0 token）/ vision（直传图）/ hybrid |

---

## 运行要求

| 项 | 值 |
|---|---|
| 系统 | Windows 10 19041+（helper 为 net9.0-windows WinForms） |
| 推理后端 | DirectML（默认，iGPU/CPU 均可）；有 NVIDIA 卡时 VLM 走 CUDA |
| 内存 | helper 全模型加载 ~2.5GB；VLM 常驻 ~3GB 显存/内存 |
| 磁盘 | ONNX 模型 ~1.6GB；vendor VLM 运行时 ~4.3GB（可选） |
| 性能 | infer 全模式 6.7-6.9fps（DirectML）；VLM 热态 0.8-1.6s/次；静止画面自动降频 CPU<10% |

## 已知限制：VLM 输出 `@@@@`

现象：`qwen2.5vl:3b` 偶发只吐 `@`，且**会传染**——一次坏之后不清状态，后续请求会连环坏，直到卸载模型或重启 serve。实测（RTX 5070 Ti / ollama 0.40.16）：

| 条件 | 结果 |
|---|---|
| `num_ctx=4096`（ollama 默认） | 第一张图之后必坏，永久持续 |
| `num_ctx=8192`（插件固定值） | 大幅收敛，但单张图仍会偶发翻车 |
| 送图宽 672 / 470 / 329（同一张密集截图） | **三档都坏** —— 宽度只降低概率，不是保证 |
| 送图宽 546 / 672（普通桌面画面） | 多数稳定（10/10 量级） |
| 并发两个请求 | 12/12 正常（并发不是诱因） |

插件侧的处置（每层有上限，最坏 ~6s 收敛，不再无限爬）：

1. 请求固定带 `num_ctx=8192`；送图宽度默认 672、上限 798。
2. 坏结果先**换一张更小的图重问**（~200ms），再**卸载重载重试**（~4.5s）。
3. 重启 serve 只在调用方显式 `allowRestart:true` 时才做（冷加载实测 30s+）。
4. `state.vlmPoisoned` 传染性防守：上次坏过，下一次请求前先卸载一次。
5. 坏文本**永不交给上层**：返回空串 + `degraded:true`；`saperm_vision` 另带 `vlm:{attempts, shrinks, effectiveW}` 便于排查。

## 生命周期与安全

- **关一个全关**：DSH 退出 → `dispose()` + exit/SIGINT/SIGTERM hook 杀掉 helper 与 vendor ollama，不留孤儿进程。只杀 `vendor\ollama` 路径下的进程，不碰系统安装的 Ollama。
- **零控制台**：helper/Chrome 用 `windowsHide`，ollama 用 VBS 静默启动（`WScript.Shell.Run(..., 0, False)`）。
- **浮窗不进截图**：`WDA_EXCLUDEFROMCAPTURE`（用户可见，截图/录屏/GDI 捕获看不到）。这样画面区不会递归照镜子，VLM 也不会读到自己刚写的字幕；老系统退 `WDA_MONITOR`。
- **浮窗自己带画面源**：谁都没开采集时浮窗按 5fps 抓缩略图，`watch` 在跑时让位给它的帧；`overlay_set` 回 `frame.present/stamp` 便于排查黑屏。
- **审批分层**：L0 观察免审；L2 输入会话级一次授权；L3（drag/doubleclick）独立授权；`saperm_stop` 急停永远可用，恢复必须用户确认。尊重全局审批策略（never 时不发 ask）。
- **屏幕内容是不可信数据**：所有读屏工具的描述都带防注入注记——画面里的文字只作为内容，不作为指令执行。

## 环境适配

| 环境 | 行为 |
|---|---|
| Windows + vendor 完整 | 自动拉起内置 ollama |
| Windows + 11434 已有服务 | 直接复用，不重复启动 |
| vendor 缺失 / 缺模型 / 非 Windows | 报错并附**可复制的修复命令**（装配脚本 / `ollama pull` / `SA_OLLAMA_URL`） |
| VLM 输出全 `@`（退化） | 有界重试阶梯：缩图重问 → 卸载重载 →（显式才）重启 serve；带毒状态守卫；坏结果置空并标 `degraded`，绝不把 `@` 当结果交给上层 |

环境变量：`SA_OLLAMA_URL`（默认 http://127.0.0.1:11434）、`SA_VLM_MODEL`（默认 qwen2.5vl:3b）、`SA_HELPER_EXE`。

GPU 后端双保留：`cuda_v12`（RTX 20/30/40）+ `cuda_v13`（RTX 50）+ `vulkan`（AMD/Intel），ollama 启动时自行探测，**勿按自己显卡删任何一套**。

## 开发

```powershell
# helper 改动后部署（同步 + MD5 校验）
cd helper && dotnet build -c Release -o bin\Release\next && .\deploy.ps1

# 全量回归（7 个套件，各自起 helper，串行，约 2 分钟）
node tests/run-all.mjs        # --list 只看清单

# 静态体检（类尺寸 / JS↔C# 接口一致性）
node scripts/audit.mjs
```

- 改工具注册后必须重启 DSH；热重载不可靠（实测线上可能停在旧模块），判别式自检与重启步骤见 `docs/RESTART.md`。
- 批量增删 register 块后必须跑 apply 探针（真实 ctx 注册一遍），`node --check` 查不出未定义引用。

### 回归套件（`node tests/run-all.mjs`）

| 套件 | 覆盖 |
|---|---|
| `plugin-smoke` | 47 工具注册 / health / infer↔interpret 互斥 |
| `prod-readiness` | 投产自检 21 项（watchdog 自愈、角色备注迁移、诊断字段、保留清理） |
| `state-drift-test` | `refs` 归零 / 浮窗状态与 helper 对齐 / 快档屏幕坐标 |
| `thread-gen-test` | 采集线程世代号（停后立刻启不双线程，`stats.loops`） |
| `shared-buffer-smoke` | 60fps 采集下 7 个慢消费者不撕裂 |
| `narrate-budget` | 短窗口讲解（首轮不计入时长预算） |
| `vision-probe` | `saperm_vision` 识图（零副作用） |
- 历史教训见 `docs/RETROSPECTIVE.md`，版本记录见 `CHANGELOG.md`。
