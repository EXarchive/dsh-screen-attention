# Changelog (dsh-screen-attention)

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
