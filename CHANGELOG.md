# Changelog (dsh-screen-attention)

## v1.11.1 (2026-11-06) — 浮窗实时评论验证通过

### 验证方法（不依赖注入链路）
```text
saperm_overlay_open({aspect:'landscape'})   开图像窗 480x270
saperm_scan / saperm_view                    取画面
saperm_say({text, speak})                    评论推到浮窗（图像窗上方）
```

### 实测结果
| 项 | 结果 |
|---|---|
| 图像窗 | `visible:true` @ 2040,1250 480x270 |
| 评论窗 | @ 2040,1132 480x110 → 自适应 480x80 |
| 窗口分离 | 评论在上、图像在下，互不遮盖 |
| 帧率 | 5.0 fps 稳定 |
| 连续更新 | 4 条评论依次覆盖，高度随文字自适应 |

### 现场证据
截图 `focus-1793974436863-4.jpg`：评论「DSH 主界面，女仆皮肤。左侧文件树停在
dsh-screen-attention，中间在跑工具流水，底部输入框刚敲完。」+ 图像窗带红色注意力框
+ 状态条「实时评论取景 2560×1600 5.0 fps」。

### 用法
```js
saperm_overlay_open({ aspect: 'landscape', ttlSec: 300 })   // 开窗
// 循环：取画面 → 生成评论 → 推送
saperm_say({ text: '画面描述…', speak: false, ms: 8000 })
```
`speak:true` 会同时用 Windows TTS 念出来；`ms` 控制停留时长。

### 注意
自动推送（`saperm_watch_link` 的注入）仍未通，实时评论需**由 AI 主动循环**，
或由外层定时任务驱动。画面获取走 `saperm_scan`/`saperm_view`，零 token（本地 OCR）。

## v1.11.0 (2026-11-06) — 交付版：清理调试代码，注入改为附加能力

### 本轮收口
- **移除全部调试探针**（6 个 `diag.txt` 写入点），生产代码不留调试痕迹。
- 代码回退到 v1.10.5 的干净状态，`node --check` 通过。

### 已确证可用（本轮及前几轮实测）
| 能力 | 状态 | 证据 |
|---|---|---|
| 消息窗 `saperm_say` | ✅ | 截图可见，`sayVisible:true`，`sayRealBounds` 正确 |
| 观察帧率自适应 | ✅ | 0.75 → 5.0 fps，`effectiveMs` 120↔329 双向浮动 |
| 采样间隔标定 `saperm_calibrate` | ✅ | 6 档候选，响应优先选值 |
| 后台窗口 `saperm_window` | ✅ | 抓被遮挡 Edge，UI 清晰 |
| 无头网页 `saperm_web` | ✅ | example.com 1200×800 完整渲染 |
| 工具收敛 | ✅ | 77 → 60 个，`char/excel/fill` 合并为 action 模式 |
| `fill` 填表全链路 | ✅ | start→next→submit→status→abort 跑通 39 张图 |

### 注入功能状态
**`watch_link` 的自动注入（`shot:true`）未验证通过**，追查六道闸门后仍未定位最后一个断点。
已确证的是：事件能到达插件（`kind=change`、`link=true` 有日志实证）、
分发/实例叠加/二次判据/`ctx.agents` 四项修复真实有效。

**使用建议**：需要观察时用 `saperm_watch_link` 不带注入，或直接用
`saperm_scan` / `saperm_view` / `saperm_observe` 主动取画面 —— 这些不依赖注入链路。

### 已知问题
1. 自动注入未通（见上）
2. helper 崩溃频率偏高，退出码 -1 多为部署/接管逻辑主动 kill，非真崩溃

## v1.10.5 (2026-11-05) — 注入链路的第五道闸门：注入器本身在 throw

### 决定性证据（diag 探针）
在 `onHelperEvent` 第一行写独立文件，绕过 `appendObservationLog`：
```text
15:25:46 kind=change link=true idx=25
15:25:49 kind=change link=true idx=26
15:25:52 kind=change link=true idx=27
```
**事件确实到达、`link` 确实为 true** —— 前面四道闸门都已拆掉。

### 第五道闸门：`injectToSession` 抛错被吞
```js
} catch (e) {
  console.log('[sa-perm] 注入失败: ' + ...)   // 只打控制台，不留痕
}
```
抛的是什么：**`ctx.agents` 访问失败**——插件的 `inject` 只声明了 `['tools']`，
而 Cordis 对未 inject 的服务会直接抛 `cannot get property "agents" without inject`。

**这条仓库 docs/RETROSPECTIVE.md 的经验 16 早就写过**，我改注入器时没回头看。

### 本轮改动
1. `export const inject = ['tools', 'agents']` ← **关键**
2. catch 不再吞异常，写 `inject-skip/inject-throw`
3. **去掉 `watch_snapshot` 依赖**：事件已带 `ratio`/`rect`，快照只补 OCR 文本，
   而它在这条回调链里会挂住（RPC 从 stdout reader 回调发起，响应回不来）
4. `screenshot` 改抓**变化区域**（非全屏）+ 1.5s 超时，拿不到图也继续注入文字
5. `onHelperEvent` 改 async（去掉外层 `.then` 后 `await` 需要它）

### 状态
**以上 5 项均未通过热加载验证**：`inject` 是模块级声明，热加载改不动它。
需要重启 DSH 才能验证。验证判据：
```text
开 watch_link → 制造变化 → 读 jsonl 尾部
看到 type: "inject"            → 链路彻底闭合
看到 inject-skip/inject-throw  → catch 里会带出真实异常信息
```

## v1.10.4 (2026-11-05) — 注入链路的第三、四道闸门

重启后事件通道已完全正常（日志 15:10 实测），注入了三道闸门，逐层拆掉：

### 闸门 1（v1.10.3 修）：事件分发只转发 hotkey
`change` 被 `continue` 直接丢弃 → 改为全部转发。重启后实证事件到达。

### 闸门 2（v1.10.3 修）：热加载实例叠加
新模块实例的 listener 加在新 Set 上，旧实例的 reader 遍历旧 Set。
→ `globalThis` 共享 + 新实例 kill 上一代 helper 接管。

### 闸门 3（本轮修）：用 2 秒后的快照判变化
```js
if (!snap.motion && !text) skip   // watch_snapshot 是 2s 后拍的，那时画面早静止了
```
事件本就带 `ratio`/`rect`，却弃之不用去重新快照 —— 于是每条事件都被判成「没变化」丢弃。
→ 改用事件自带的 `data.ratio` / `data.rect`。

### 闸门 4（本轮修）：对 ratio 做二次判据
```js
if (evRatio <= 0 && !text) skip   // 细粒度变化时 ratio 可能≈0
```
helper 的 `m` 判定是 `ratio > ratioTh || windowSum >= 12`——细粒度累计触发时 `ratio` 可能接近 0，
二次判 `evRatio <= 0` 又把它们全扔了。
→ **事件本身已代表有变化，不再二次判**。刷屏由 1.2s 节流 + 30 次/分钟配额控制。

### 状态
四道闸门全部拆除后，**最后一次端到端验证未能完成**：helper 在 15:10:58 崩溃（日志 `helper_crash`），
测试期间没有 change 事件发出。

已知遗留：helper 崩溃频率偏高（本次会话日志里 `helper_crash` 出现十余次），需单独排查。

## v1.10.3 (2026-11-05) — 热加载实例叠加的两个真 bug

### 本轮确证的修复

**1. 事件分发只转发 hotkey**（v1.10.2 已修，本轮补实证）
helper 一直发 `change`，插件侧 `if (msg.event === 'hotkey') {...} continue` 把其余全丢。
改成全部转发后，日志实证 5 秒内 4 个 `change link=true` 到达。

**2. 热加载后事件通道整体失效** ⚠️
这是本轮新发现的、比 1 更隐蔽的问题：

热加载会**创建新的模块实例**，但**旧实例的 helper 进程、stdout reader、看门狗定时器都还在跑**。
旧 reader 闭包遍历的是**旧实例那个已经空掉的 `listeners` Set`** —— 新实例的 `onHelperEvent` 永远收不到事件。
RPC 却照常可用（走 pending/response 通路），所以表面一切正常，只有事件静默丢失。

修法（两层）：
```js
// listeners 跨实例共享
listeners: (globalThis.__saPermListeners || (globalThis.__saPermListeners = new Set()))
// 新实例接管：杀掉上一代 helper，强制重新 spawn 以绑定新 reader
const oldPid = globalThis.__saPermHelperPid; if (oldPid) process.kill(oldPid)
```

**3. Watch 循环帧率 0.75 → 5 fps**
加速判据用 `motionRatio >= 0.005`，而 change 事件用 `m`（含 `windowSum >= 12` 细粒度）——
两套判据打架，导致 `motion=true` 却一直减速到 1600ms，change 事件 4 秒才一个。
统一为 `if (motion)` 加速，下限从 120 提到 200（防自身界面刷满 CPU）。

### 仍未闭合
**变化注入最后一个环节（`injectToSession`）未验证通过。**
事件已确认到达（日志 72 条 `change link=true`），节流也放行了一部分，
但从没有一条 `inject` 或 `inject-skip/no-initiator` 落盘 —— 卡在 `.then` 回调内部。

**诊断受阻的原因**：反复热加载后实例状态叠加，删掉的诊断代码仍在写日志（`ev2` 明明删了却持续产出），
说明运行的是旧实例的闭包。**这种情况下任何验证结果都不可信，必须重启 DSH 或整套 kill helper 才能验准。**

### 教训（写进 RETROSPECTIVE）
对有**长生命周期状态**的插件（进程、reader、定时器、全局缓存），
热加载 ≠ 干净重载。改完先 kill 全套再验，否则会像本轮一样在“改了没生效”和“改了生效但被旧实例干扰”之间反复误判。

## v1.10.2 (2026-11-05) — 消息窗修复 + 事件分发 bug（变化注入从未通过的根因）

### A · 消息窗不显示（已修，有截图证据）
根因：`Bounds = target` 写在 `new SayForm { ... }` 初始化器里，此时**窗口句柄还没创建**，
WinForms 直接忽略该值 —— 窗口建出来了、`sayOpen:true`、`sayTarget` 正确，但屏幕上什么都没有。

修法：在 `Shown` 事件里再设一次（此时句柄已建），并显式 `Visible/TopMost/BringToFront`。
新增 `sayVisible` / `sayRealBounds` 回报实测值。

验证（`saperm_view` 抓图）：窗口显示为深蓝圆角框 + 左侧蓝竖条 + 文字，「SA 消息窗测试：看到这行说明修好了」。

### B · 变化注入从未执行的根因：事件被丢弃
```js
if (msg.event === 'hotkey') { for (const fn of listeners) fn('hotkey', msg.data) }
continue        // ← change 到这儿就被扔了
```
helper 一直在发 `change` 事件，插件侧却**只转发 `hotkey`**，其余一律丢弃。
所以 `onHelperEvent` 里 `kind === 'change'` 的分支从写下那天起就没被调用过，
「屏幕变化 → 注入会话」整条链路是死的。改成全部转发。

修复后实证（`saperm_history` 诊断）：
```text
14:29:36  kind=change  link=true
14:29:39  kind=change  link=true
```
事件到达、link 正确。此前只有 `ready`，`change` 一条都没有。

### 附带修掉
- **注入配额 6→30 次/分钟**：6 次上限一次测试就耗光，之后全是 `reason=budget`
- **`saperm_history` 的 `limit` 取的是文件头不是最新**：导致「最新记录」查询返回旧数据，
  排查时把我带偏过。查最新请用 `type` 过滤（按类型过滤时是全文件扫描）。

### 仍未闭合
Watch 循环帧率异常低（4 秒只推进 3 帧，预期 ~6），导致 change 事件稀疏。
这是注入链路的下一道瓶颈，已加 `loopFrames`/`changeEmits`/`lastChangeError` 三个计数器备用。

## v1.10.1 (2026-11-05) — excel_*/fill_* 收敛 + 修掉两个既存缺陷

### 合并
- `saperm_excel_goto/write/read/cells` → **`saperm_excel({action})`**
- `saperm_fill_start/status/next/submit/write/abort` → **`saperm_fill({action})`**
- 工具数 **68 → 60**，index.js **3010 → 2953 行**

### 修掉两个既存缺陷（不是我引入的，是合并时暴露的）

**1. `fill` 系列一直是坏的**
`fillLoad`/`fillResolve`/`fillSave`/`fillJobFile`/`walkImages`/`colLetter`/`sleepMs`
在 v1.10.0 里**只有调用、没有定义**——这 6 个工具任何一次调用都是 `ReferenceError`。
现在补齐实现（checkpoint 落 `out/fill/<id>.json`）。

**2. L2 审批白名单指向了已不存在的工具名** ⚠️
```js
// 改前（合并后这些名字都不存在了 → 写 Excel 绕过审批）
L2_TOOLS = [..., 'saperm_excel_goto', 'saperm_excel_write', 'saperm_excel_read', 'saperm_fill_write']
// 改后
L2_TOOLS = [..., 'saperm_excel', 'saperm_fill']
```
这是合并类重构最容易漏的一处：**改了工具名，忘了改权限白名单 = 审批形同虚设**。
以后合并工具必须同步检查 `L2_TOOLS`/`L3_TOOLS`。

### 端到端验证（fill 首次跑通）
```text
start  → job=fill-mw3mwtv7  total=39
status → pending:39
next   → img1  1/39
submit → status: extracted
status → pending:38  extracted:1
abort  → aborted:true
```

`saperm_excel({action:'cells'})` → `{"cells":[],"total":0}`（Excel 未打开，空结果正确）

## v1.10.0 (2026-11-05) — 工具收敛：char_* 十个合成一个

### 构造问题（实测数字）
| 指标 | 值 |
|---|---|
| index.js | 3082 行 |
| 工具注册 | **77 个** |
| 常驻 schema 估算 | 约 8500 token（每轮固定开销） |

`char_*` 10 个、`fill_*` 6 个、`excel_*` 4 个，全是**单行转发**（`helperCall('char_xxx', {...})`），
各占一个工具名额、各进一次模型上下文，纯属浪费。

### 改动
`saperm_char_register/match/list/delete/rename/merge/unknowns/promote/ignore/note`
→ **`saperm_char({ action })`** 单一入口，schema 用 enum 约束 action，非法值直接报错。

**77 → 68 个工具**，index.js **3082 → 3010 行**。

### 验证
| 调用 | 结果 |
|---|---|
| `action:'list'` | `count:2`（DeepSeek娘…） |
| `action:'unknowns'` | `count:1`（未知角色#1） |
| `action:'bogus'` | 报错「action 必须是: register \| match \| …」 |
| `saperm_status` | helper online |

### 两次失败的真因（都是我的操作，不是设计）
**第一次**：文件是 CRLF，我用 `split('\n')` 切割再 `join('\n')` 拼回 → 混合换行符。
**第二次**：改提示文案时把 `saperm_char(action:'promote')` 写进了**单引号字符串里**，字符串提前闭合：

```js
const hint = unknown ? '（未登记，可用 saperm_char(action:'promote') 命名）' : ''
//                                           ^ 单引号在这里结束
```

而「语法检查通过」是假象——**先测语法、后改文案**，改完没再测。教训：任何改写之后都重新 `node --check`。

### 待办
`fill_*`（6→1）、`excel_*`（4→1）同样可合，预计再省 8 个工具。

## v1.9.0 (2026-11-05) — 间隔自适应（不绑死）+ 九点采样回归

### 核心：采样间隔改成运行时自适应（AIMD）
间隔不该固定——实测同一场景 **120ms 有效、500/900ms 反而 0**（内容节拍不均匀，
某些间隔正好踩在同一姿态）。固定值必然有盲区，所以让它自己浮动：

```text
实质变化(ratio>=0.005) → cur = max(120, cur × 0.7)   乘性加速
连续静止 2 帧以上       → cur = min(1600, cur × 1.4)   乘性减速
```

`watch_status` 新增 `effectiveMs` 暴露当前实际间隔。

| 场景 | effectiveMs |
|---|---|
| 视频播放（持续变化） | 392 → **120**（拉满） |
| 屏幕静止（ratio 0.0019→0.0005） | 120 → **329**（自己往上爬） |

**加速必须看实质变化**：初期版本只要 `motion=true` 就加速，结果全屏观察时被
DSH 自己的光标闪烁/流式输出刷成 120ms，白烧 CPU。加 `motionRatio >= 0.005` 门槛后才正常。

### 新增 `saperm_calibrate`
对同一区域逐个试 120/250/500/900/1500/2500ms，测每个间隔能否真正抓到变化，
用「平均分 × 变化率」算有效度：

| intervalMs | avg | 变化率 | 有效度 |
|---|---|---|---|
| 120 | 5.37 | 0.50 | 2.68 ← 响应优先选它 |
| 250 | 3.75 | 0.50 | 1.88 |
| 500 | 4.90 | 0.00 | 0 |
| 900 | 2.86 | 0.00 | 0 |
| 1500 | 3.00 | 0.00 | 0 |

选值规则：变化率 ≥0.34 的候选里取最小间隔（响应优先），没有就退回有效度最高。
结果缓存 2 分钟，支持 `force` 重测。

### 九点采样回归
3×3 九点采样改回来——当初回滚是基于误判（score=0 的真实原因是抓屏间隔，不是采样密度）。

## v1.8.1 (2026-11-05) — 变化注入带图 + 抓屏间隔真相

### 新增
- **watch_link 的 shot 参数（默认开）**：屏幕变化时把画面一起注入会话
  - 走宿主 `ctx.attachments.saveImages()` → `content: [{type:'image', attachment}]`，模型能直接看到画面
  - 之前只注入文字摘要，等于蒙着眼睛报「屏幕变了」——这正是和 see-now 体验的最大差距
  - 关了（`shot:false`）更省 token

### 关键发现（实测）
**抓屏间隔太短会拿到同一帧**：
| intervalMs | 结果 |
|---|---|
| 200 | score 恒 0，只留首帧 |
| 900 | score 0 / 6 / 6.8 / 42.5，正常留帧 |

屏幕合成有更新粒度，比它更快地连续抓只会重复拿到旧内容。`watch` 也一样：300ms 时 `motion:false`，800ms 时 `motion:true, ratio:0.0367`。

### 回滚
- 3×3 九点采样改回原 4 点：回滚后行为一致，证明当初 score=0 不是采样密度问题，而是上述抓屏间隔问题。
  误判已记录在 Cuts.cs 注释里。

### 教训
- **「进程还在」≠「画面还在变」**：ffplay 播完停在末帧，进程仍活着，我据此误判了两轮
- **「哈希不同」≠「判据该给高分」**：DSH 界面流式输出的小变化，64×36 网格平均后本来就不够切点
- 排查顺序应该是：先确认目标区域是否可见（是否被全屏窗口盖住）→ 再确认采样间隔 → 最后才怀疑算法

## v1.8.0 (2026-11-05) — 后台窗口捕获 + 无头网页（不只识别屏幕）

### 新增
- **saperm_window：抓窗口自身内容，不依赖它是否可见**
  - PrintWindow + PW_RENDERFULLCONTENT(0x2)：被其它窗口完全盖住的窗口也能拿到画面（DWM 渲染离屏内容）
  - 最小化窗口先 SW_SHOWNOACTIVATE 恢复再抓，抓完立刻 SW_MINIMIZE 还原（不抢焦点、不改用户布局）
  - 全黑检测：抓到全黑时明确提示「窗口最小化或属于受保护内容」
  - action: list 列可抓窗口；shot 按 hwnd 或 title 抓
- **saperm_web：无头浏览器看网页**
  - --headless=new --virtual-time-budget 渲染后截图，**全程不建窗口、不抢焦点、不遮挡用户**
  - 视口可调（w/h/waitMs），实现在插件侧（spawn + 文件轮询）

### 关键边界（实测）
| 目标 | saperm_window（PrintWindow） | saperm_web（无头） |
|---|---|---|
| 普通 Win32 / GDI 窗口 | 有效 | — |
| 浏览器 UI（标签栏/地址栏） | 有效 | — |
| **浏览器网页渲染区** | **空白**（GPU/D3D 渲染层抓不到） | 有效 |
| 最小化窗口 | 需先恢复（会短暂出现） | 不需要窗口 |

### 踩坑
- **窗口标题含不可见字符**：Edge 标题里 Microsoft 与 Edge 之间有零宽字符，按子串匹配直接失败。
  → 已知 hwnd 时一律优先用 hwnd（工具契约已写明）。
- **PrintWindow 拿不到 GPU 渲染层**：D3D surface 不在 GDI 世界里，这是设计使然不是 bug，补法是无头渲染或 CDP。

### 实测
| 场景 | 结果 |
|---|---|
| 抓被 DSH 全屏盖住的 Edge（hwnd=16518486） | ok 2247×1530 → 1260×858，标签页/地址栏/书签栏清晰，网页区空白 |
| saperm_web example.com | ok 1200×800，多语言页面完整渲染，无任何窗口出现 |

## v1.7.0 (2026-11-05) — 开口说话（say 气泡 + TTS）/ 外观整合 / 闪屏修复

### 新增
- **`saperm_say`：AI 边看边说**
  - 文字显示在小窗的**圆角气泡**里，同时用 Windows 自带 SAPI 朗读
  - 依赖 `System.Speech` 9.0（官方包，无需第三方）；本机音色：**Microsoft Huihui Desktop（中文）**、Zira、David
  - `speak:false` 只显示气泡不出声；新话打断旧话（`SpeakAsyncCancelAll`），不排队积压
  - `saperm_speech_voices` 列出可用音色
- **外观整合（统一设计语言）**
  - 色板：深蓝底 `(10,16,34)` / 强调青蓝 `(96,196,255)` / 主文字 `(232,240,255)` / 次要 `(150,175,205)`
  - **注意力区域：实心矩形 → 四角括号**（画满框会遮挡内容，括号只标边界，长度自适应最长 18px）
  - **注视点：绿十字 → 同心圆 + 短十字**
  - **气泡：直角矩形 → 圆角（r=8）+ 青蓝描边**，距信息条留 18px，最大宽度 88%
  - **信息条：Label → 全自绘**，左=状态圆点（绿在动/琥珀静止）+ 注意点，右=区域尺寸 + 帧率；单行省略互不挤压
- **`helper/deploy.ps1`**：全量同步 + 重试 kill + **逐文件哈希校验**（部署不完整直接报错，不再带病运行）

### 修复
- **浮窗闪屏**（三个原因叠加，逐个掐掉）
  1. **没有双缓冲**：`new Form` 拿不到 protected 的 `DoubleBuffered` → 新增 `OverlayForm` 子类 `SetStyle(OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint)`，并让 `OnPaintBackground` 空实现（连 WM_ERASEBKGND 都不擦）
  2. **每帧新建 Bitmap**：480×270@100ms = 每秒 10 张，GC 直接表现为卡顿 → 复用同一张位图（注意不能 `using`，会 Dispose 掉缓存）
  3. **每 tick 三件事都干**：改 `form.Text`（发 WM_SETTEXT 触发重排）、`SetWindowPos` 强化置顶（每 100ms 强制重排 z 序）、无条件 `Invalidate`
     → 改成：标题只在 note 变化时改、置顶降到 1s 一次、**帧序号没变就不重绘**（`frameStamp`）
- **`System.Speech.dll` 同名不同类型**：根目录是 310KB 的 facade（引用壳），实现版是 `runtimes\win\lib\net9.0\` 下的 685KB。
  只复制根目录那份 → 运行时报 `Could not load file or assembly 'System.Speech'`。
  修法：`runtimes` 目录整体同步，并把实现版覆盖到根目录（.NET 探测顺序优先应用目录）。

### 实测
| 项 | 结果 |
|---|---|
| 音色列表 | 3 个（Huihui 中文 / Zira / David） |
| `saperm_say` | `ok:true shown:true spoke:true spokeOk:true`，截图可见圆角气泡 |
| 浮窗像素稳定性（6 帧/150ms） | `isStatic:true`，score 全 0 |
| 浮窗像素稳定性（10 帧/100ms） | 抖动分（0<score<8）**0 个** |
| helper CPU | 16.9% 单核（含 500ms 全屏采集 + 浮窗绘制） |
| `observe_task maxReports:2` | 10s 自动收工，浮窗一并回收 |

## v1.6.0 (2026-11-05) — 锁定观察目标 / 音频分麦克风 / 选帧尺度修复

### 新增
- **`saperm_target`：锁定观察目标窗口**
  - 锁一个窗口后，`saperm_sheet` / `saperm_view` / `saperm_scan` / `saperm_locate` /
    `saperm_watch_focus` / `saperm_observe_task` 不传 x/y/w/h 就自动只看它的**客户区**
  - 用 `GetClientRect + ClientToScreen`，不是 `GetWindowRect` —— 标题栏和边框不属于内容
    （实测窗口 1302×776@189,75 → 客户区 1280×720@200,120）
  - 只存 hwnd/title，矩形每次现取：窗口移动/缩放自动跟随（实测移到别处后 rect 变 1931×1108@161,135）
  - 优先级：显式 x/y/w/h > 锁定窗口 > 拖框选区 > 全屏
  - 与 `saperm_focus`（置前窗口，L2 输入）区分：这个是纯观察范围，L0
- **`saperm_audio` 分麦克风与设备音频**（`source`）
  - `device`（默认）：`eRender + AUDCLNT_STREAMFLAGS_LOOPBACK` —— 电脑正在放什么
  - `mic`：`eCapture + eCommunications`，**不带** LOOPBACK —— 麦克风
  - `both`：两路并行采集（不同设备互不干扰），返回 verdict："只有设备在放/只有麦克风有声音/两边都有"

### 修复
- **选帧打分尺度太小（真 bug）**：原来按格子宽度抓帧再算变化分，全屏 2560 只抓 418 宽，
  视频里的角色动作被 HALFTONE 平均掉 —— score 恒为个位数，视频在播也报 `isStatic`。
  改成分开两件事：**打分固定 ≥900 宽**（`pickScale`），留帧时再 `ScaleBuffer` 缩到格子尺寸。
  实测同一段视频：全屏 score 6 → 聚焦窗口 81。
- **"像人声"误判**：只有下限没有上限，`zcr=0.75` 的游戏 BGM 被判成人声。
  加上限 `zcr < 0.35`（人声在中间带，太高是噪声/金属感音乐）。

### 实测（2560×1600 / 本机）
| 项 | 结果 |
|---|---|
| 窗口 1302×776@189,75 → 客户区 | **1280×720@200,120**（去掉边框标题栏） |
| 窗口移动后 rect | 1931×1108@161,135（跟随，不缓存） |
| `saperm_sheet` 不传区域 | 自动用客户区 |
| `saperm_target clear` | 回退全屏 0,0,2560,1600 |
| 音频 device | 设备 id `8d88de52…`，db -42，voice=false（音乐） |
| 音频 mic | 设备 id `b21a48c5…`，db -37.3，voice=true |
| 音频 both | "设备在放，麦克风也收到声音" |

## v1.5.0 (2026-11-05) — 听声音 / 手机竖屏适配 / 悬浮窗形状跟随

### 新增
- **`saperm_audio`：WASAPI 回环采集**（`AudioCapture.cs`，224 行，纯 P/Invoke）
  - 走 `IMMDeviceEnumerator → IAudioClient(AUDCLNT_STREAMFLAGS_LOOPBACK) → IAudioCaptureClient`
  - **不需要虚拟声卡**（"立体声混音"在现代 Windows 上大多被禁用，这条路更可靠）
  - 输出 RMS / 峰值 / dB / 过零率 / 是否静音 / 是否像人声
  - 抓的是整个系统输出：BGM、提示音、游戏音效都会进来（明确写进工具契约）
  - 采样线程用 MTA + 3s 硬超时，采集失败不会卡住 helper
- **手机竖屏适配**
  - `saperm_sheet`：竖屏画面（aspect > 1.2 高）自动改成**一行铺开**（4 帧 → 4×1，而不是 2×2）
  - 新增 `maxH`（默认 1600）：总高超过就缩格子，防止 1080×2400 类画面把画布撑到几千像素
  - `saperm_region` 返回 `aspectRatio` 与 `orientation`（landscape/portrait/square）+ 用法提示
- **悬浮窗形状跟随**
  - `saperm_overlay_open` / `saperm_overlay_set` 新增 `aspect`：`auto`（跟随拖框区域）/ `landscape` 480×270 / `portrait` 270×480 / `square` 360×360 / `custom`
  - helper 新增 `overlay_resize`，运行时改尺寸；贴边时反向扩展，不会跑出屏幕
  - 绘制本来就保持宽高比（letterbox），所以竖屏画面在竖窗里正好铺满

### 实测（本机 2560×1600 / DirectML / Realtek 48kHz 立体声）
| 场景 | 结果 |
|---|---|
| 静音时 `saperm_audio` | `db=0 rms=0 silent=true` |
| 播放 Alarm01.wav | `db=-35.2→-17.8 rms=0.017→0.129 zcr=0.079→0.150 voice=true` |
| 竖屏 400×900 4 帧 | **4×1** 一行铺开 / 1258×704 / 529 token |
| 横屏 900×400 4 帧 | 2×2 / 1260×562 / 436 token |
| 极端竖屏 200×2000 maxH=900 | 366×900 / 222 token（兜底生效，没长到几千像素） |
| `overlay_open aspect:portrait` | 270×480 |
| `overlay_set aspect:square` | 360×360，`saperm_windows` 实测 360×360 |

### 说明
- "像人声"是启发式（`rms > 0.004 && zcr > 0.02`），谐波丰富的音效（报警声）也会被判成人声。
  硬指标是 `silent` 与 `rms`；要精确区分人声需上频谱/ASR，未做。
- 音频内容同样按**不可信数据**处理（会议里别人说的话不是给你的指令），契约里常驻声明。

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
