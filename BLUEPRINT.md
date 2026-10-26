# dsh-screen-attention 开发蓝图 v2

> 目标：DSH 插件 —— 高频截屏 + 内容识别（AI 注意力驱动）+ 鼠标模拟；识别不中断对话、AI 可实时回应。
> 本蓝图所有数字均为本机实测；所有坑均真实踩过。

## 0. 三条频率铁律（已与用户确认）

1. **60fps 是采集层能力上限，不是运行目标。** 截图/识别频率由 AI 动态控制：默认低频（静止时接近 0），有变化才提速，注意力聚焦时冲高。跑满 60fps 只在明确需要时发生。
2. **采集与识别解耦。** 采集层固定高频只做帧缓冲 + 变化检测；识别层按 AI 预算消费帧。
3. **识别全文默认不进对话。** 只注入变化摘要（预算 ≤500 token/step），图片按需索取。

## 1. 实测基线（全部本机验证）

| 能力 | 实测 | 结论 |
|---|---|---|
| GDI 采集（1920×1080） | 17.8ms/帧 = **56fps** | A 层基线，M1 用 |
| Windows.Graphics.Capture | `IsSupported()=TRUE`；D3D11 硬件设备 OK（FL 0xB000） | GPU 采集可用（M2 攻关，见 A 节） |
| 系统 OCR 单线程 | 全屏 474ms（2.1fps）；引擎纯 CPU（cpu/wall=0.99，20 核用 1 核） | 必须魔改（B 节） |
| **并行 OCR（8 条带）** | **474 → 68.6ms = 6.9x，全屏 14.6fps**；12 条带反而 77.7ms | ✅ 魔改路线 1 已验证成功 |
| 分辨率规律 | 2x 放大耗时≈1x（119 vs 118.5ms）但质量最优；0.5x 又慢又瞎（81ms/29 字符） | 提速靠缩小区域，永远不降分辨率 |
| 窗口枚举 | 热路径 0.77ms | 注意力候选清单 |
| SetCursorPos | 往返 1.47ms | 鼠标移动 |
| SendInput | **x64 结构体必须 40 字节**；修正后 200/200 成功 | E 节 |
| GPU OCR 供应链 | onnxruntime-directml **1.24.4** 在 NuGet | 魔改路线 2 可行（B3） |
| DirectML.dll | 系统自带 1.15.5 | GPU 推理运行时已在 |

## 2. MaaNTE 学习成果（G:\Neverness To Everness\MaaNTE-win-x86_64-v0.0.2-MFAA）

拆解了它的 maafw/ 与配置，结论：**我们自研方向与 MaaFramework 工业实践完全一致**，且它回答了"OCR 怎么魔改"：

| 学到的 | 证据 | 吸收方式 |
|---|---|---|
| OCR 魔改的工业答案 | `fastdeploy_ppocr_maa.dll` + `DirectML.dll` + `onnxruntime_maa.dll` | B3 路线：PaddleOCR(fastdeploy) + ONNX Runtime DirectML GPU 推理 |
| 图像处理 | `opencv_world4_maa.dll`（模板匹配/特征/缩放） | C# helper 可用 OpenCvSharp 或自写差分 |
| Win32 控制器矩阵 | `MaaWin32ControlUnit.dll`：screencap=Background(BitBlt)/PrintWindow/FramePool；mouse=Seize/SendMessageWithCursorPos/SendMessageWithWindowPos；keyboard=Seize/PostMessage | E 节扩展后台模式；FramePool 印证 WGC 路线 |
| 急停热键 | F10 开始 / F11 停止 | F 节采纳（另加 Pause） |
| 轮询节奏 | RealTime 任务 `interval: 500ms` | 印证"AI 控频、不跑满"理念 |

**许可边界（重要）**：MaaFramework/AGPL-3.0——不直接链接其 DLL；学习架构思路。PaddleOCR（Apache-2.0）、onnxruntime（MIT）、OpenCV（Apache-2.0）可合法自集成。

## 3. 总体架构

```
DSH Web 进程（Node）
├─ Cordis Host 插件（本项目核心）
│   ├─ 工具层：观察/看图/设注意力/点击/急停
│   ├─ AI 联动：inject()（静默）+ steer()（唤醒）
│   ├─ 权限层：DSH approval 三级 + 审计
│   └─ 预算层：token 配额/变化摘要/环形缓冲
└─ C# helper 常驻进程（dotnet 9，stdio JSON 行协议）
    ├─ 采集线程：GDI 56fps（M1）→ WGC/DXGI（M2）
    ├─ 识别线程池：UIA 优先 + 并行 OCR（核数自适应）
    ├─ 输入线程：SetCursorPos/SendInput（40 字节）
    └─ 窗口枚举：0.77ms 注意力候选
```

**铁律**：常驻（冷启动 330–550ms 是高频杀手）；采集识别解耦；识别结果不落盘。

---

## A. 采集层（硬件加速）

**M1 基线**：GDI BitBlt，实测 56fps（与 60fps 差 4fps，按铁律 1 足够）。

**M2 GPU 路径（已验前置条件，实现期攻关）**：
- `Windows.Graphics.Capture`：`IsSupported()=TRUE`、D3D11 硬件设备创建成功、MaaFramework 已用 FramePool 做采集（工业先例）。价值：GPU 抓帧 + 采集不抢 CPU。
- DXGI Desktop Duplication：能免费拿**脏矩形**（注意力引擎的最佳输入）。探测中手工 vtable 调用在 `AcquireNextFrame` 处崩溃（dup/vtable 指针均验证有效，根因未闭环），M2 用托管包装（CsWinRT/Vortice）重做，不再手写 vtable。
- 失败降级：GPU 路径不可用 → 自动回退 GDI（56fps）。

**已知坑**：多显示器坐标（以 VirtualScreen 为准）；DPI 缩放（PerMonitorV2）；锁屏/远程桌面（失败降级）；受保护内容（WGC 抓不到，只影响该能力不影响截图）。

## B. 识别层（UIA 优先 + OCR 魔改）

### B1. UI Automation（优先，零误差）
- `System.Windows.Automation`：Name/AutomationId/ControlType/BoundingRectangle。
- 坑：COM 跨进程要 STA 线程；canvas/游戏/图片无控件树（→OCR）；自绘控件空 Name（→OCR 读该区域）。
- 决策点：是否给 Edge 加 `--force-renderer-accessibility` 增强 UIA（代价渲染性能略降）。

### B2. 并行 OCR（魔改路线 1 —— 已验证 ✅）
- 引擎：`Windows.Media.Ocr`（zh-Hans-CN 优先 en-US 兜底）。
- **CPU 核心自动检测（已确定公式）**：
  ```
  cores = Environment.ProcessorCount          # 本机 20
  条带数 = clamp(2, cores / 2, 8)             # 本机 → 8（实测 8 最优，12 更慢=内存带宽饱和）
  区域高度 < 300px 时不切块（单块固定开销 ~50ms，小区域并行反而慢）
  ```
- 实测加速曲线（全屏 1920×1080）：1 块 474.4ms → 2 块 209 → 4 块 112.1 → 6 块 86.7 → **8 块 68.6（14.6fps）** → 12 块 77.7。
- 重叠 16px 防切行；合并时按词坐标 IoU 去重。
- 2x 放大恒开（免费精度，见 §1）。

### B3. GPU OCR（魔改路线 2 —— 供应链已验证，进阶项）
- `Microsoft.ML.OnnxRuntime.DirectML` 1.24.4（NuGet ✓）+ PaddleOCR det/rec ONNX 模型（Apache-2.0）。
- 前置事实：本机 Intel UHD 770 + DirectML 1.15.5 + D3D11 FL 0xB000 全部可用。
- 触发条件：B2 并行后仍不够快（如全屏 30fps 需求）或 OCR 质量不足时启用；GPU 加载失败自动回退 B2。
- 注意：包体积大（~150MB），作为可选依赖延迟加载。

### B4. 统一元素模型
`{id, source: uia|ocr, text, rect{x,y,w,h}, confidence, app, hwnd, role}`；UIA/OCR 按 IoU≥0.5 去重，UIA 优先。

## C. 看图功能
- 帧 → JPEG（1280w 实测 13.9ms/237KB；永不用 PNG）→ stdio base64 → `ctx.attachments.saveImage` → 视觉模型。
- 两级：glance（≤1280w 快速语义）与 focus（注意力区原图细看）。
- 坑：base64 膨胀（大图二期走文件）；图片只按需索取不自动推；附件计数上限；视觉延迟 1–5s（慢通道，与快通道解耦）。

## D. AI 联动
### D1. 注入与唤醒（官方 API，已确认）
- 常规 → `agent.inject()`：排队下个 pre-step，不唤醒 driver —— 对话不中断。
- 重要变化 → `agent.steer()`：空闲开新 turn / 运行中下一步消费 —— 实时回应。
- 观察循环 → `agent/turn-stopping` 事件 + 新鲜变化再评估。
- 获取：`ctx.agents.currentInitiator()`。
- 坑：inject 可能错过已 claim 的 pre-step（尽力而为）；**默认不唤醒**，脏矩形+语义阈值才 steer；多会话互斥。

### D2. 注意力机制（AI 自选区域/分辨率）
- 候选：窗口枚举 0.77ms → {hwnd,title,rect,pid,fg}。
- 聚焦四路合并：AI 显式指定 > 用户需求 > 脏矩形自动 > 默认前台窗口+光标。
- 分辨率语义：AI 选**区域大小**，不选缩放比；区内恒 2x。

### D3. 频率控制（AI 控频，60fps 上限非目标）
工具参数 `区域 × 触发策略(定时|脏矩形) × 优先级`；物理约束表（写进工具描述）：

| 区域 | 并行后实测 | 默认策略 |
|---|---|---|
| 全屏 | 14.6fps | 低频 glance（0.5–1Hz），变化时提速 |
| 720×450 区 | 20fps+ | 变化触发 |
| 聚焦 2x | 8–12fps | 任务需要时 |
| 静止 | — | **≈0**（铁律 1） |

### D4. 预算
每 step ≤500 token 变化摘要；首次全量此后增量；分钟配额自动降频。
### D5. 观察-执行闭环
识别→决策→点击→回看验证→下一目标；一步一闭环。

## E. 鼠标点击
- `SetCursorPos`（1.47ms）+ `SendInput`（**40 字节 INPUT**——x64 必须 union 布局：`[StructLayout(LayoutKind.Explicit)]` + `[FieldOffset(8)]` 放 mi/ki；写成两个并列字段会膨胀成 64 字节，SendInput 报 err=87）。
- 动作：move/click/doubleClick/rightClick/drag/scroll/typeText/keyPress。
- MaaNTE 借鉴：后台模式（PostMessage/SendMessage）作为扩展——前台 Seize 优先，后台窗口操作可选。
- 坑：前台锁定激活失败要报告；动作后等 100–300ms 再验证；动画漂移用最新帧坐标。
- 实测闭环（2026-08-28）：move/scroll 经 helper SendInput 注入成功，权限链 8 步验证全绿。

## F. 权限与安全（具体实现方案，已按 DSH 官方契约落地）

### F0. DSH 审批契约（已从类型定义核实）
- `ctx.approval.request({agent, toolName, callId?, reason?, signal?}) → Promise<ApprovalOutcome>`
- 返回值词汇：`'allowed-once' | 'rejected' | 'cancelled' | 'unavailable'`；**`allowed-once` 是唯一的 grant——服务本身没有"记住/总是允许"**，会话级授权必须插件自维护。
- 约束：必须处于 open turn（工具执行中 ✓）；session policy=`'never'` 时自动 `rejected`；无 answerer 时 fail-closed `unavailable`。
- 审计自动完成：服务对每次 ask 成对写 `approval/asked` + `approval/decided`（log-only，不进模型上下文）。

### F1. 授权状态（插件内存，per-agent，不落盘）
```
grants: Map<agentId, {
  l2Input: 'granted' | 'denied' | 'pending',
  paused: { by: 'user-activity' | 'hotkey' | 'tool', at: number } | null
}>
```
插件卸载/重启/会话结束即清除（安全默认）。

### F2. 三级门控流程
- **L1 观察类**（截图/识别/窗口枚举）：不调用 approval；受急停 guard 与隐私黑名单约束。
- **L2 普通输入**（move/click/scroll/typeText 安全区域）：
  1. 首次：`await approval.request({agent, toolName, reason: '授权本会话内普通鼠标点击/滚轮/文本输入；不含拖拽、连击、系统区域。同意后本会话内不再重复询问', signal})`。
  2. `allowed-once` → 执行本次 + `l2Input='granted'`（reason 已声明会话级语义，用户知情）；`rejected/cancelled/unavailable` → 拒绝执行 + `l2Input='denied'`（本会话不再弹窗）。
  3. 后续：granted 直接执行；denied 直接拒绝。
- **L3 高危**（drag / 连击 / 任务栏·关闭按钮·系统对话框 / 组合键 / 跨窗口）：**每次**都 approval.request（无视 L2 状态），reason 写明具体动作。静态黑名单（CTRL+ALT+DELETE、Win+L 等）不弹窗直接拒。

### F3. 急停（三层独立，实现点已定）
1. **用户活动**：helper 注入前检查 `GetLastInputInfo`（已实测 API 可用），idle < 300ms 即视为用户正在操作 → **等待用户停止操作（轮询 50ms，最多 5s）再注入，持续操作才报 USER_ACTIVE 让位**（用户一动鼠标，AI 的注入自动让位）。注意：用户点审批"允许"按钮本身就是鼠标活动，直接拒绝会让首次授权 100% 失败——必须等待而非立刻抛错（2026-08-28 实测踩坑）。
2. **热键**：helper 隐藏消息窗口 + `RegisterHotKey(Pause)`（借鉴 MaaNTE F10/F11 设计）→ 锁注入。
3. **工具 + 官方 guard**：`screen_attention_stop` 置位，配合 **`ctx.tools.guard()`**（DSH 官方 monotonic guard，在 `tools/pre-execute` 后同步执行，返回字符串即拒绝）——急停激活时 guard 直接拒绝全部鼠标/键盘工具调用，无需审批、不可被绕过。
- 恢复仅限用户明确动作（再按热键 / `screen_attention_resume` 工具）；**AI 不能自行解除**。
- 暂停后识别可继续，注入锁死（物理层 helper + API 层 guard 双重）。

### F4. 隐私
帧只存内存环形缓冲不落盘；注入走预算（D4）；窗口黑名单（settings 配置，M5）；采集状态 UI 指示（二期 Client）。

### F5. 审计与错误语义
- 审批对由 DSH 服务自动审计（F0）；急停/授权变化通过工具结果进入会话可见面。
- 失败语义：`unavailable` 如实返回"审批通道不可用，按失败关闭处理"；`rejected` 返回"用户拒绝，本会话不再尝试"。
- **结果观察必须区分错误来源（2026-08-28 实测踩坑）**：`tools/result` 里 `isError=true` 可能是三种情况——①审批被拒（记 denied ✓）②执行期错误如 USER_ACTIVE 让位/helper 离线/超时（保持 pending，下次重新 ask）③其他。一律记 denied 会把让位误判成"用户拒绝"并永久锁死会话级授权。判别用错误文本特征（denied/rejected/refused/declined/cancelled/拒绝/取消），其余保持未决。

## G. 跨模块
helper 崩溃看门狗 3s 重启；协议握手版本号；单调时钟序号跨进程对齐；结构化错误返回；多会话屏幕互斥。

### G1. 注意力可视化小窗 + 单引擎观察架构（已实现并端到端验证 2026-08-28）
- **单引擎铁律（用户定）**：helper 只允许一个采集循环（watch）作为唯一帧源；overlay 画面、变化检测、焦点 OCR 全部消费它。禁止小窗自带第二套采集（曾加 FrameLoop 被用户否定"不要前端一套后端一套"，已回退）。
- helper WinForms 无边框 TopMost 小窗（STA 线程 + Application.Run），GDI 绘制 watch 帧 + 注意力红框 + 绿十字 + 底部信息条；100ms 重绘 + SetWindowPos(HWND_TOPMOST) 定时强化置顶。
- **插件编排捆绑**：overlay_open 若 watch 未运行→自动低频(1000ms)启动+autoWatch 标记；overlay_close 若 autoWatch→自动停止（省资源）；显式 watch_link/start 接管（autoWatch=false）。
- **AI 自主视角（多尺度）**：`saperm_view`{x,y,w,h,scale,maxW,note} 任意区域+缩放截图→存 out/sa-view/*.jpg 返回 {path, rect}（统一虚拟屏幕坐标，小图 rect 即大图子区域）+自动同步 overlay 红框（小窗=AI 视线）；`saperm_glance` 全屏大图。实测 big(0,0,1920,1080)↔small(500,300,400,250)@2x 视觉对应正确。
- **输出端汇报**：`watch_focus` + 焦点 OCR 增量 diff（added 字段）——对话滚动即感知 AI 自己的输出。
- 命令：`overlay_open/overlay_close/overlay_set`、`watch_start/watch_stop/watch_status/watch_focus/watch_snapshot`、`screenshot`(区域 x/y/w/h+scale)。
- 插件侧 L1 免审工具：`saperm_overlay_open/close/set`、`saperm_watch_start/stop/status/focus/link/unlink`、`saperm_observe`、`saperm_view`、`saperm_glance`。
- 踩坑：①stdio 必须 UTF-8（helper 强制 Console.Input/OutputEncoding）；②小窗没画面=watch 未跑（捆绑自动启动解决）；③vision-router OVH 后端易 429，视觉用 read_image 兜底；④图片交付走文件+路径（不进模型上下文，按需 read_image）。

## H. 里程碑
| M | 内容 | 验收 |
|---|---|---|
| M1 | helper：GDI 采集 + UIA + 并行 OCR（核数自动检测）+ 窗口枚举 | 56fps 采集稳定；全屏 OCR ≥14fps；10 分钟无泄漏 |
| M2 | GPU 采集攻关（WGC/DXGI 脏矩形，托管包装）+ 失败回退 GDI | 采集可达 60fps 或明确记录上限；脏矩形可用 |
| M3 | Host 插件：观察/看图/注意力工具 + inject/steer | 对话中状态到位；变化唤醒生效 |
| M3+ | 注意力可视化小窗（用户要求 2026-08-28）：helper WinForms TopMost 无边框 overlay + watch 采集线程 + 插件 L1 免审工具 | 小窗实时显示帧 + 红框/十字/注意点文字 ✅ 已实现 |
| M3-2 | 高频识别+变化检测+AI联动（2026-08-28 闭环）：稀疏网格检测+滑动窗口+自动调速+change 事件+watch_snapshot(区域OCR摘要)+插件 change→inject/steer | ✅ overlay 开/关实测 motion=true→OCR摘要→motion=false；inject 排队不打断对话 |
| M4 | 鼠标注入 + 审批 + 急停 | 词坐标点击 10/10；急停 100ms |
| M5 | 注意力自适应 + GPU OCR（可选）+ 多显示器/DPI/黑名单/状态 UI | 聚焦 8–12fps；全绿 |

## I. 已定决策
1. 识别：UIA + 系统 OCR 双路 ✓
2. 审批：会话级授权 + 高危逐次 ✓
3. helper：dotnet 9 常驻单文件 exe ✓
4. 60fps = 能力上限，AI 控频 ✓（本蓝图铁律 1）
5. OCR 魔改：并行路线已验证为默认；GPU 路线为进阶项 ✓
6. 急停热键：`Pause` ✓
7. 不直接复用 AGPL 的 MaaFramework 组件，只学思路 ✓

## J. 概念级待细化清单（尚未落到实现方案的项）

> 状态说明：✅=已有具体实现方案；🟡=方向已定、实现细节未定（概念级）；🔴=方向都未定。
> 标记 [M1] 的项会阻塞 M1 开工，须在编码前拍板。

| # | 项 | 状态 | 缺口 | 建议拍板 |
|---|---|---|---|---|
| 1 | helper JSON 行协议命令集 [M1] | 🔴 | screenshot/ocr/uia/windows/click/stop 等命令 schema 未定义 | M1 第一天定稿（可在仓库 docs/protocol.md） |
| 2 | UIA 实现选型 [M1] | 🟡 | System.Windows.Automation（自带、功能少）vs FlaUI.Core（NuGet、遍历/缓存强） | FlaUI.Core（跨进程稳定、社区成熟） |
| 3 | UIA 遍历策略 | 🟡 | 全树 vs 可视子树；CacheRequest 粒度；增量更新 | 只遍历可视控件 + 缓存；变更时增量刷新 |
| 4 | 并行 OCR 条带去重换算细节 | 🟡 | 条带内词坐标 + 重叠偏移的换算公式未写 | 实现时随单测固化（IoU≥0.5 已定） |
| 5 | GPU OCR 全链路（B3） | 🔴 | 模型下载源、预处理/后处理（DB+CRNN）、DirectML 加载、回退时机 | M5 再细化；先跑通 B2 |
| 6 | DXGI/WGC 托管绑定选型 [M2] | 🟡 | 手工 vtable 已弃；Vortice.Windows vs CsWinRT | Vortice.Windows（纯托管 DXGI/D3D11 绑定，GitHub 活跃） |
| 7 | 脏矩形 → 注意力输入算法 [M2] | 🔴 | 多帧聚合、窗口归属、合并去重、阈值 | M2 设计文档 |
| 8 | 变化摘要生成算法 [M1] | 🔴 | N 帧识别结果 → ≤500 token 的压缩方式（模板/规则/轻量模型） | 先规则化模板（变化区域+文本+时间），AI 嫌长再优化 |
| 9 | "重要变化"语义阈值（steer 条件） | 🔴 | 什么变化才唤醒 AI：脏矩形面积？文本变化？窗口切换？ | 默认参数：窗口切换/新窗口+任意脏矩形>屏面 5%+文本增量>0；均可由 AI 调 |
| 10 | 注意力四路合并仲裁 | 🟡 | AI 指定/用户需求/脏矩形/默认焦点的优先级与冲突 | AI 显式 > 用户 > 脏矩形 > 默认；AI 未设时自动模式 |
| 11 | 频率调度器实现 | 🟡 | 定时 vs 事件驱动的统一调度；降频曲线 | helper 内：事件驱动为主（脏矩形），定时兜底；预算超限线性降频 |
| 12 | turn-stopping 续命策略 | 🟡 | 什么条件下 steer 续命，避免无限烧 token | 仅在"观察模式开启 + 有新鲜重要变化"时续命，单轮上限 3 次 |
| 13 | 看图触发规则 | 🟡 | glance/focus 何时请求、图片生命周期 | AI 显式索取；附件 5 分钟/20 张上限自动清理 |
| 14 | 大图传输通道 [M2] | 🟡 | base64 已定 M1；文件/二进制通道 | webServer 静态路由临时文件，M2 |
| 15 | 后台输入模式（PostMessage/SendMessage） | 🔴 | 支持动作集、消息参数、适用窗口类型 | M4 之后按需；先只做前台 Seize |
| 16 | 点击验证（回看判定） | 🟡 | 点击后如何判定成功/失败 | 首次实现：点击前抓局部特征（UIA/OCR），点击后 300ms 重识别对比变化 |
| 17 | 多会话屏幕互斥仲裁 | 🟡 | 谁优先、提示方式、抢占规则 | 先占先得；后会话发起注入时提示占用者；急停可强制接管 |
| 18 | 看门狗重启后的状态恢复 | 🟡 | 授权/注意力区域/暂停态是否保留 | 授权与暂停态保留（在插件侧），helper 重启后重新握手加载注意力配置 |
| 19 | 隐私黑名单拦截方式 | 🟡 | 区域涂黑 vs 跳过采集 | 窗口矩形涂黑（保留布局、隐藏内容），M5 |
| 20 | 工具清单与参数 schema 定稿 [M3] | 🟡 | screen_attention_* 工具名与参数 | M3 开头定稿 |

**M1 阻塞项**：#1 协议、#2 UIA 选型、#8 摘要模板（可用最简版先跑通）。
