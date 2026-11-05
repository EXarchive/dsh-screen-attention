# Changelog (dsh-screen-attention)

## v1.4.0 (2026-11-05) — 按变化量选帧 / 拖框选区 / 自动收工

### 新增
- **变化分算法 `Cuts.cs`（HSV 64×36）**：移植 see-now 的口径 —— 64×36 网格每格 4 点采样，
  RGB→HSV，色相环形处理 + 低饱和降权，加权绝对差 0–255。配运动补偿 `McRatio`（搜索半径 4 格）。
  **性能关键：先测同位置再扫邻域** —— 屏幕最常见状态是"没变"，这一步单独省掉约 90% 的静止帧开销。
- **`saperm_sheet` 按变化量选帧**（`select:true` 默认）：不再等间隔硬抓。
  - 切点：`score ≥ 12 且 score > 2.5×邻居 且 mc ≥ 20 且 距上一刀 ≥ 0.4s`
  - 积分：`need = 4分 × fps × 3秒`（一镜到底没有尖峰，只能靠积分）
  - 静止兜底：一张都没攒到就只出 1 张，返回 `isStatic:true`
  - 每格烧**帧号**（3×5 点阵，白底黑字），看图能直接对上 `cells[].atMs`
  - `select:false` 退回旧的等间隔行为
- **`saperm_region`（拖框选区）**：全屏半透明覆盖窗（25% 黑 + 亮绿选框 + 实时尺寸），
  拖完松手即存 `out/region.json`；Esc 取消；超时自动关窗（不会把窗口留在屏幕上）。
- **自动收工**：`saperm_observe_task` 新增 `idleStopMin`（默认 3 分钟，用户没碰键鼠就停）与
  `maxReports`（汇报次数上限）。helper 新增 `idle` 命令（Win32 `GetLastInputInfo`）。

### 实测（本机 2560×1600 / DirectML）
| 场景 | 结果 |
|---|---|
| 静止屏幕 `frames:6, intervalMs:100` | **1 帧**，`isStatic:true` |
| 有真实变化（score 74.2） | 正确留帧 |
| `saperm_region pick timeoutMs:3000` | 窗口创建成功，超时自动关闭，返回 `cancelled:true` |
| 4 帧 high 拼图 | 1260×788 / **591 token** / 每帧 148 |

### 未移植（明确记录，不是遗漏）
- **变尺寸 DP 装箱**：see-now 用 `pack.js` 把不同档位（1/k）格子混排进方形画布，行流式、末行不留整格空槽。
  当前是等分网格（8 帧时格子缩到 313×196）。等分已够用，DP 装箱留待有真实需求时再做。
- **音轨条 + WASAPI 回环 ASR**：需要音频运行时与转写服务，独立排期。
- **悬浮窗 `say`（AI 的话进气泡并被念出来）**：需要 TTS 适配器。

## v1.3.0 (2026-11-05) — 多帧拼图 saperm_sheet

### 新增
- **`saperm_sheet`（多帧拼图）**：连续抓 N 帧（1~12）拼成一张网格图。
  核心是**价格按张算，不按帧算** —— 4 帧和 8 帧 token 相同，所以"多留一帧几乎免费、漏掉一帧才贵"。
  这只补上了 `local` 文本通道抓不到的东西：图标、照片、视频、游戏画面、艺术排版。
  - 参数：`frames` / `intervalMs`（默认 120，调大 = 看更长的时间窗）/ `quality` / `gap` / 区域 `x,y,w,h`
  - 返回：`grid`（行×列，按帧序左到右、上到下）、`cells`（每格时间戳）、`tokens`、`perFrameTokens`
- helper 新增 `sheet` 命令：等分网格布局（1→1×1、2→1×2、3~4→2×2、5~6→2×3、7~8→2×4），
  逐帧 `CaptureScaled` 后按行 `Buffer.BlockCopy` 直接拼进画布，深灰 gap 分隔。

### 实测（本机 2560×1600 / DirectML）
| 配置 | 画布 | token | 每帧摊薄 | 耗时 |
|---|---|---|---|---|
| 1 帧 high | 1260×788 | 591 | 591 | 68ms |
| **4 帧 high** | 1260×788 | **591** | **148** | 574ms |
| 8 帧 high | 1258×394 | 312 | 39 | 1268ms |
| 6 帧 low | 796×332 | 200 | 33 | 918ms |

同样看 4 个时刻：单帧连拍 4 次 = 4×591 = **2364 token**；一张 4 帧拼图 = **591 token**，省 **75%**。

### 已知限制
- 等分网格，不做变尺寸装箱（see-now 用 DP 装箱把不同档位格子混排，我们暂未实现）。
- 帧是"到点抓一张"，不是"按变化量选帧"——静止屏幕会重复抓同一画面（后续接切点+积分判据）。
- 画布高度由屏幕比例决定，8 帧时格子会明显变小（313×196）。

## v1.2.0 (2026-11-05) — 双运行方案（local / vision / hybrid）+ 环境自适应

### 新增
- **`saperm_profile`（环境探测）**：判定「本地识别 local」「视觉直传 vision」「混合 hybrid」哪条可用并给推荐。
  只看三件真事：可用执行器（TensorRT → CUDA → DirectML → CPU）、模型文件齐全度、CPU 线程数。
  返回 `modes`（各自 ok / 成本 / 速度）、`env`（providers / backend / cores / 可用内存）、`models`、`reasons`。结果缓存 60s，`force:true` 重新探测。
- **`saperm_scan({mode})` 三档路由**：`local`（默认，只本地识别，0 token）/ `vision`（额外返回可直传画面 + token 估算）/ `auto`（按 profile 推荐）。
  vision 模式的缩图**在截图阶段就做掉**（1260 宽 = 42×30），否则原分辨率 2582×1550 会撞 1024 token 上限。
- **`saperm_health.scheme`**：自检里直接给出推荐方案、backend、GPU 与理由，不用额外调用。
- **helper `profile` 命令**：输出执行器列表、GPU 布尔、backend、CPU 核数、可用内存、模型清单与决策理由。

### 实测（本机 RTX 5070 Ti Laptop + Ultra 9 275HX 24核 / 31.5GB）
- `saperm_profile` → `recommended: "hybrid"`，`providers: ["DmlExecutionProvider","CPUExecutionProvider"]`，`cores: 24`。
- `saperm_scan({mode:"local"})` → **163ms**，`image: null`，0 token。
- `saperm_scan({mode:"vision"})` → **200ms**，图 1260×681 = **529 token**（改前按原分辨率出图是 1024 token，省 48%）。
- `saperm_scan({mode:"auto"})` → `recommended: "hybrid"` → 自动走 vision。

## v1.1.1 (2026-11-01) — L3 一次性审批 / 长按输入

### 新增
- **`saperm_press`（长按）**：`target=mouse` 按住鼠标按钮、`target=key` 按住按键，`holdMs` 默认 500（10~10000）。用于长按菜单、游戏移动、连续输入、拖拽起点。helper 新增 `press` 命令与 `KeyDown`/`KeyUp` 分离原语。
- **`saperm_click` 支持 `holdMs`**：按下到抬起的保持时间（默认 25ms，范围 0~10000），返回 `clickMs` 实测按住耗时。

### 变更
- **L3 高危操作改为一次性审批**：`saperm_drag` / `saperm_doubleclick` 由「每次执行单独审批」改为「本会话首次审批一次，通过后免审」。
  - L3 授权独立存储（`state.l3`），**与 L2 授权解耦**：批准普通点击不会连带放开拖拽/双击，反之亦然。
  - 仅在执行成功时授予；审批被拒记 `denied`（下次重新发起），执行期错误保持未决。
  - 全局 `never` 策略下同时点亮 L2 与 L3 并留审计。
  - `saperm_status` 新增 `l3ByAgent`、`saperm_health` 新增 `l2`/`l3` 观测字段。
  - 急停（`saperm_stop`）仍在所有审批之前拦截，恢复仍需用户确认。

### 修复
- 鼠标 `MouseButton` 原先把 down/up 放在同一批 `SendInput` 内（间隔≈0μs），过快会被 DirectInput 类应用忽略；改为分段发送并保证最小按住时长 25ms。
- 键盘 `KeyPress` 同样瞬时完成，改为 `KeyDown` → 12ms → `KeyUp`（`holdMs` 可覆盖）。

### 实测
- `click holdMs=400` → `clickMs=404`；默认点击 → `29ms`；`press key SHIFT holdMs=300` → `elapsedMs=302`。
- L3 连续调用：`doubleclick` → `granted` → 第二次 `doubleclick` 免审 → `drag` 复用同一 L3 授权，三次全部 `injected=true`。

## v1.1.0 (2026-10-31) — AI 可读数据流 / 帧账本 / 高速识别

### 新增
- **AI 可读数据流（stream）**：每帧识别结果压成一行约 110 字节紧凑文本，字段 `W=`前台窗口 / `SC=`场景 / `OB=`物体(含身份) / `F=`动漫脸 / `TX=`文本增量 / `CH=`变化事件 / `fps/ms`；`TX=` 采用增量语义（`=` 与上帧相同、`+…` 尾部新增、其它为全文），`CH` 与上一行去重；落盘 `out/logs/stream-YYYY-MM-DD.jsonl` + `stream-latest.txt`（滚动 200 行），相比 `infer_status` 全量 JSON（2574 字节/帧）压缩约 **22×**。新工具 `saperm_stream` / `_start` / `_stop`。
- **帧账本（ledger）**：`framesConsumed = recognized + skippedStatic + skippedBusy` 恒等可校验，每帧要么被识别、要么带原因记账，不再静默丢帧；`recognizeHz` 暴露真实识别能力。
- **`saperm_scan`**：一次截图完成多路感知（OCR 文字+坐标，可选 CLIP 场景 / YOLO 物体），返回分阶段耗时与 `withinBudget`（默认 1900ms 预算内）。
- **`saperm_frames`**：连续帧流水线，以目标帧率（默认 60fps）持续采集并逐帧记录时间戳/采集耗时/空洞(gap)。
- **`saperm_topmost`**：临时把被遮挡的窗口抬到最高层（`on=false` 复原），解决最大化窗口互相遮挡导致截图截错窗口。

### 改进
- **自适应识别节奏**：静止时 4fps 兜底省电，画面一动立刻切到帧率上限；新增 `fullRate` 参数可完全关闭静止节流。
- **采集层**：1ms 定时器精度（`timeBeginPeriod`）；动作后目标间隔 16ms（60fps）+ 1.2s 滞回防抖动；DC/位图/输出缓冲全部复用（原每帧分配约 16MB）；半分辨率 `StretchBlt` 快速通道跳过全屏回读；上下半屏并行采集（双锁双缓冲）；逐帧 gap 记账。
- **识别层**：ROI 差分 + 48 块轮转，每帧只识别变化最大的一块，未变区域复用上次文本框；det 降频为每 10 帧一次；`RecImgW` 320 → 160。
- `saperm_scan` 默认 `maxTexts` 由 40 提升到 80（浏览器 chrome 会占满前 40 个坑，导致正文被截断）。
- ONNX Runtime 选项：`GraphOptimizationLevel` BASIC → **ORT_ENABLE_ALL**、`EnableMemoryPattern` 打开、CPU EP 支持多线程。

### 修复
- **det 短边判断写反**：`m < 736 ? 736/m : 1.0` 使 1600px 高的屏幕永远按 `ratio=1.0` 跑全尺寸 det（2560×1600 = 4M 像素），单帧 200ms+；改为 `DetLimitSide / m`。
- **Session 桥接超时**：worker 接受等待 4s → **20s**。机器繁忙时 Session 1 worker 启动超时被误杀，helper 降级回 Session 0（看不到用户桌面且白吃约 2GB 跑全模型）；降级路径现在直接跳过预热。
- **worker 单实例互斥**：`Local\SaHelperWorker_S{sessionId}` Mutex，重复拉起的 worker 立即退出，避免多实例叠加 CPU/内存。
- **滚轮打偏**：滚轮消息只发给光标下方窗口；现在先移动光标到前台窗口中心（或传入的 `x,y`）。
- **组合键无效**：热键事件一次性同时间戳发出导致 `Alt+F4` 被忽略，改为分段发送 + 帧间延迟（28/38/22ms）。

### 实测（本机 2560×1600 / ONNX Runtime DirectML）
| 项目 | 数值 |
|---|---|
| 识别速率 | **16.6 Hz**（1 框/帧）、14.0 Hz（4 框/帧） |
| 单次 OCR | avgOcr 46~65 ms，P50 42~63 ms |
| 采集速率 | 34~35 Hz（GDI 上限），动作后目标 16 ms |
| 静止 CPU | 21.7% → **10.0%**（心跳零推理 + 采集降频） |
| stream 压缩比 | ~110 B/行 vs 2574 B/帧（**≈22×**） |
| 端到端延迟 | 采集 ~28 ms + 识别 ~46 ms ≈ **74 ms** |

### 已知限制
- 识别速率天花板约 14~17 Hz：PP-OCRv4 单次 DirectML Run 存在约 **40 ms 固定成本**（dispatch + 同步 + tensor 传输），与输入尺寸、框数、批量均无关。已逐项排除：det 尺寸（1000/800/448/320）、det 降频、固定输入 shape、CPU 多线程（24 线程 269ms）、FP16 权重（DML 不支持）、rec 批量（更慢）。
- 达到 30 fps 需替换为专用轻量模型（单类文本检测 + 小字典识别），属模型训练任务。

## v1.0.0 (2026-10-26) — 正式投产版
- **Session 0 -> Session 1 跨会话透明桥接 (`SessionBridge.cs`)**：当 DSH 以 `NT AUTHORITY\SYSTEM` 运行在 Session 0 时，通过 `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` + 活动控制台会话令牌 + `winsta0\default` + 本地回环 TCP 桥接，自动在用户交互桌面（Session 1）启动 Worker 进程。解决 Session 0 隔离导致的窗口列表为空、GDI 抓屏黑屏、SendInput 失效及 DirectML 退化问题（内存占用从 ~2.5GB 降至 ~770MB）。
- **DirectML 全局线程安全串行化 (`Providers.Gate`)**：消除后台推理线程与主线程并发调用 DirectML `CreateSession` / `Run` 引发的原生访问冲突崩溃。
- **看门狗崩溃告警与状态自愈 (B13 / C14)**：Helper 异常退出时自动注入会话告警、落盘 `helper_crash` / `helper_recover` 日志，并在 ~800ms 内自动拉起新进程恢复 `infer` / `interpret` / `watch` 与急停状态。
- **结构化变化摘要与窗口切换感知 (B3 / B4)**：融合前台窗口标题（`窗口「...」`）与 `窗口切换 -> ...` 事件。
- **隐私黑名单窗口自动涂黑 (B14) 与安全桌面容错 (C10)**：采集层自动涂黑匹配黑名单（`1Password` / `KeePass` / `Bitwarden` / `SA_PRIVACY_TITLES`）的可见窗口区域，并检测锁屏/安全桌面状态。
- **点击后局部像素回看校验 (B11)**：`click` 自动对比点击前后 96×64 局部区域像素变化率（返回 `verified` 与 `diffRatio`）。
- **截图与日志生命周期治理 (B8 / C13)**：`pruneViewDir` 自动保留最新 30 张截图（保护测试夹具），`pruneLogsDir` 自动清理 7 天前日志并限幅单文件大小。
- **角色画廊防漂移自动学习 (C4) 与未知聚类治理 (C6 / C15)**：支持高置信自动扩充参考、`mtime` 跨进程热重载、备注随重命名/删除同步，修复 `saperm_char_ignore` 协议 `id` 覆盖缺陷。
