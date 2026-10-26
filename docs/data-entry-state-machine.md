# 跨窗口数据录入状态机与回读验证设计

> 所属：dsh-screen-attention 识图引擎升级（PaddleOCR 集成 / 本地 VLM / 硬件自适应 / 状态机）
> 交付范围：①多图/多文件夹对照填表的状态机（当前进度 + 图片↔行映射）②Excel 单元格 UIA 定位 ③填表后回读验证方案。
> 本文所有原语、坐标、命令都对齐现有 helper（`helper/Program.cs`）与协议（`docs/protocol.md`）；新命令为草案，实现期细化。

---

## 0. 定位与分工

**目标**：让 DSH 的 AI 把「多个文件夹里的多张图片/扫描件」批量、可续跑、可验证地录入到一个 Excel 工作簿（一张图 → 一行，或一张图 → 多行 / 多张图 → 一行）。

**分工（与现有架构一致）**：

| 层 | 谁 | 职责 |
|---|---|---|
| 看懂图（语义） | AI（LLM）+ 本地 VLM（t3）+ PaddleOCR（t1/t2） | 图片 → 结构化字段（每图一条字段记录，可含多行明细） |
| 编排与状态机 | **插件（Node/Cordis）** | Job 生命周期、图片↔行映射、进度持久化、断点续跑、审批上下文 |
| 动手（确定原语） | **helper（dotnet9）** | 窗口枚举/聚焦、UIA、OCR、点击/键入/剪贴板、单元格 GoTo/读写 |

**为什么状态机放插件侧**（关键决策）：
1. 审批（`ctx.approval`）与会话语境只在插件侧；
2. Job/checkpoint 属于会话/用户，须随插件存盘，不能随 helper 重启（看门狗 3s 重启）丢失——helper 无状态、重启只需重新握手；
3. 提取字段是 AI/VLM 的行为，AI 必须在环内；填表/验证是确定循环，可自动化。
4. helper 保持「无状态原语」，符合单引擎铁律与现有协议设计。

---

## 1. 数据模型

### 1.1 实体

```
Job         一次录入任务（1 个 Excel 工作簿）
 ├─ Batch    一个源文件夹（映射到某 Sheet；或一个 Sheet 对应多文件夹，见 3）
 │   └─ ImageItem   一张图片（= 一个/多个目标行）
 │        └─ RowRecord  目标行（Excel 中的一行，含若干 Field）
 │             └─ Field    单个单元格 {name, excelCol, value, type, status}
 └─ Checkpoint 进度清单（磁盘 JSON，断点续跑依据）
```

### 1.2 Checkpoint JSON（进度持久化 + 断点续跑唯一事实源）

```jsonc
{
  "schemaVersion": 1,
  "jobId": "job-20260828-093012",
  "state": "filling",                 // 见 §2.1 状态
  "excel": { "path": "D:\\录入\\台账.xlsx", "caption": "台账.xlsx - Excel", "pid": 8123, "hwnd": 123456 },
  "sheet": "Sheet1",
  "headerRow": 1,                      // 表头所在行（行号从 1 起）
  "dataStartRow": 2,                   // 首个数据行
  "columns": [                          // 字段 → 列映射（校准后固化，见 §4.4）
    { "field": "date",     "header": "日期", "colLetter": "A", "colIndex": 1, "type": "text" },
    { "field": "amount",   "header": "金额", "colLetter": "B", "colIndex": 2, "type": "number" },
    { "field": "name",     "header": "名称", "colLetter": "C", "colIndex": 3, "type": "text" }
  ],
  "mapping": { "mode": "one_to_one", "sort": "natural", "groupBy": null },  // 见 §3
  "images": [
    {
      "id": 0,
      "path": "D:\\录入\\扫描\\2024\\img_001.jpg",
      "row": 2,                         // 目标行号（数据行）
      "rowRange": null,                 // one_to_n 时 [start,end]
      "status": "verified",             // pending|extracting|extracted|filling|verifying|verified|failed|skipped
      "fields": { "date": "2024-01-02", "amount": "1234.50", "name": "张三" },
      "verify": { "ok": true, "mismatches": [], "attempts": 1 },
      "error": null,
      "updatedAt": 1724815000000
    }
  ],
  "stats": { "total": 200, "pending": 43, "extracted": 0, "filled": 0, "verified": 157, "failed": 0, "skipped": 0 },
  "pausedBy": null,                     // user-activity | hotkey | approval | excel-dialog | null
  "updatedAt": 1724815000000
}
```

**进度语义**：`current image = 第一个 status ∈ {pending,extracting,filling}`；`当前进度 % = verified/total`。`fill_job_status` 工具直接读该文件返回人类可读摘要（见 §9 工具草案）。

---

## 2. 状态机

### 2.1 Job 级状态机

```
                    ┌────────────┐
    start ────────▶ │  SCANNING  │ 枚举文件夹/文件 → 建索引 + 校准列映射(§4.4)
                    └─────┬──────┘
                          │ 索引就绪
                    ┌─────▼──────┐
                    │ EXTRACTING │ 逐图：VLM+OCR → fields（AI 在环）
                    └─────┬──────┘
                          │ 每图字段就绪
                    ┌─────▼──────┐
                    │  FILLING   │ 定位 Excel → 逐行写单元格(§5)
                    └─────┬──────┘
                          │ 写入完成
                    ┌─────▼──────┐
                    │ VERIFYING  │ 逐行回读验证(§6)
                    └─────┬──────┘
                          │ 全部 verified
                    ┌─────▼──────┐
                    │    DONE    │ 出报告 + notify_user
                    └────────────┘

   任意状态 ──(暂停: 用户操作/热键/审批等待/Excel 弹窗)──▶ PAUSED ──(恢复)──▶ 回到原状态
   任意状态 ──(致命: Excel 关闭/文件锁死/重试耗尽)───────▶ FAILED
   任意状态 ──(用户取消)───────────────────────────────▶ ABORTED
```

**状态表**：

| 状态 | 含义 | 退出条件 | 可暂停 |
|---|---|---|---|
| `IDLE` | 无任务 | `start` | — |
| `SCANNING` | 枚举文件夹/文件、建图片索引、校准列 | 索引+列映射就绪 | ✓ |
| `EXTRACTING` | 逐图识别（VLM+OCR）产出字段 | 全部图提取完（或按需流水线） | ✓ |
| `FILLING` | 写 Excel 单元格 | 本批所有行写入 | ✓ |
| `VERIFYING` | 回读验证 | 全部行 verified | ✓ |
| `PAUSED` | 挂起（记录 `pausedBy`） | `resume` | — |
| `FAILED` | 致命错误 | 人工介入 / `retry` | — |
| `DONE` | 全部完成 | 归档 checkpoint | — |
| `ABORTED` | 用户取消 | 归档 | — |

**流水线说明**：默认「批式」——SCANNING→EXTRACTING（全部）→FILLING（全部）→VERIFYING（全部）。可选「逐图流水线」——每张图 `extracting→filling→verifying` 一步到位再下一张（适合图大、内存/上下文受限），二者共用同一状态机，只是 EXTRACTING 与 FILLING 交替推进；checkpoint 的 `images[].status` 天然支持任意粒度。

### 2.2 图片（行）级子状态机

```
pending → extracting → extracted → filling → verifying → verified
                         │           │          │
                         ▼           ▼          ▼
                       failed      failed     retry(≤3) → failed
```

- `extracting`：VLM/OCR 读图 → 结构化字段；失败可重试（换引擎/降级）。
- `filling`：定位目标行 → 逐字段写；任一字段重试耗尽 → 该图 `failed`（不阻塞其他图，按策略 `haltOnRowError` 决定整批停或跳过）。
- `verifying`：回读比对；`verified` 才允许推进（幂等/防漏，见 §6.4）。

### 2.3 事件与守卫

| 事件 | 守卫/动作 | 状态影响 |
|---|---|---|
| 用户正在操作（`GetLastInputInfo` idle<300ms） | helper `GuardInject` 已等待≤5s；持续操作抛 `USER_ACTIVE` | 图/Job → `PAUSED`（`pausedBy=user-activity`），空闲后自动续 |
| Pause 热键 / `stop` | 注入锁 `locked=true` | → `PAUSED`（`pausedBy=hotkey`），仅用户 `resume` 可解除 |
| 审批等待（L3 或首次 L2） | 弹审批，等待结果 | → `PAUSED`（`pausedBy=approval`） |
| Excel 意外弹窗（保存提示/受保护视图/宏安全） | 前台窗口非 Excel 主窗口 → 不盲打，steer AI | → `PAUSED`（`pausedBy=excel-dialog`） |
| Excel 窗口丢失/关闭 | `windows` 枚举找不到 hwnd/pid | → `FAILED`（可重定位重试） |
| helper 崩溃（看门狗 3s 重启） | 重新握手，从 checkpoint 续 | 状态不变，重放当前图 |
| 重试耗尽（同字段 mismatch ≥3 或连续失败 ≥N） | 标记 failed，按策略跳/停 | 图 `failed` / Job `FAILED` |

---

## 3. 图片 → 行映射

**目标**：把「源图片文件」稳定、可复现地对应到「Excel 数据行」，且支持断点续跑不串行。

### 3.1 三种映射模式

| 模式 | 场景 | 规则 |
|---|---|---|
| `one_to_one`（默认） | 一图一行（发票/报销单/工单） | 自然排序后第 i 张 → `dataStartRow + i` |
| `one_to_n` | 一图多行（一张发票多行明细） | 提取结果含明细数组，`rowRange=[start, start+len-1]`；下一图从 `end+1` 起 |
| `n_to_one` | 多图一行（证件正反面） | `groupBy: "prefix"`（文件名前缀分组）或显式 manifest |

### 3.2 排序与显式 manifest

- **默认自然排序**：数字感知（`img_2` < `img_10`），跨文件夹按「文件夹名 + 文件名」字典序；保证两次运行结果一致（可复现）。
- **显式 manifest（最高优先级）**：`manifest.json` / CSV 每行 `{image, row, sheet}`；messy 数据必用，覆盖排序与自动行号。

```jsonc
// mapping 配置示例
{ "mode": "one_to_one", "sort": "natural",
  "manifest": "D:\\录入\\manifest.json",   // 可选，存在则优先
  "rowKeyColumn": null }                   // 可选：用某列做行归属校验（见 §6.4）
```

### 3.3 防串行 / 防覆盖守卫

1. **行占用检查**：写某行前，先读该行（L1 UIA / L2 clipboard 读回），非空且未标记 `overwrite:true` → 该图 `skipped`（记入报告，不覆盖已有数据）。
2. **rowKey 校验**（可选）：若配置了 `rowKeyColumn`（如「单号」列），写入后回读该列，与图片提取出的 key 比对，防「图↔行」错位（视觉批次错位是高频事故）。
3. **断点续跑**：只重放 `status ∉ {verified}` 的图；`filling` 中崩溃的图整行重做（幂等覆盖），`verified` 跳过（可配置 `reverify:true` 全量复验）。

---

## 4. Excel 单元格 UIA 定位

### 4.1 窗口定位

- 用现有 `windows` 命令枚举；Excel 主窗口 `class="XLMAIN"`、标题 `"<工作簿名> - Excel"`。
- **多实例消歧**：按 `pid` + `caption 含工作簿名` 匹配；找不到 → `FAILED`（提示用户确认 Excel 已打开、非「受保护视图」）。
- 把 `{pid, hwnd, caption}` 固化进 checkpoint，后续所有定位以此 hwnd 为锚。

### 4.2 三级定位策略（由稳到糙，逐级回退）

**L1 — UIA Grid/TablePattern（首选，零误差）**
- 用 FlaUI（helper 已集成 `FlaUI.UIA3`，STA 线程）从 `XLMAIN` 取工作表网格元素（`ControlType.DataGrid/Table`）。
- 拿到：网格屏幕矩形（用于坐标换算）、可视单元格（`Name="A1"`、`Value`）、列头/行头。
- **局限**：Excel UIA 只物化「当前可见窗口内」的单元格（虚拟化），离屏单元格不存在于树 → 仅用于①网格矩形标定 ②读可视/当前单元格值 ③表头校准。

**L2 — 名称框 / GoTo 键盘导航（最稳，主用）**
- 用 `F5`（定位对话框）→ 键入 A1 引用（如 `Sheet1!A5`）→ `Enter`，Excel 会自动滚动到该单元格并选中。
- 纯键盘驱动，无坐标漂移、无 DPI 问题；离屏单元格也能到达。
- **已实现（2026-10）**：`VkOf` 已补 F1–F12、字母/数字、Win/Insert/CapsLock 等；新增 `hotkey {keys:["CTRL","G"]}` 组合键命令 + 插件工具 `saperm_hotkey`。

**L3 — 坐标 + OCR（兜底，最糙）**
- 用 L1 拿到的网格矩形 + 表头行高 + 单元格宽高推算目标单元格屏幕坐标 → 点击 → 键入。
- 需处理：冻结窗格（frozen panes）偏移、缩放（Excel 缩放 ≠ 100%）、滚动后重标定。
- 仅在 UIA 树不可得、且 GoTo 不可用时使用。

### 4.3 虚拟化与滚动

- 原则：**永远用 L2 GoTo 让目标单元格进入可视区，再用 L1 读值 / 剪贴板读值**，不依赖「整表 UIA 遍历」。
- 冻结窗格：GoTo 到达的单元格若被冻结行/列遮挡，读值仍正确（值在单元格里），但坐标点击会错位 → 一律用 GoTo 选中 + 键盘写入，不做坐标点击。

### 4.4 列映射校准（SCANNING 阶段完成，固化进 checkpoint）

1. 读表头行（`headerRow`，默认第 1 行，可配置）：L1 UIA 读可视表头，或 OCR 表头行。
2. 把配置里的字段 `header`（如「金额」「日期」）与读到的表头文本做归一化匹配（trim/全半角/去空格）→ 得到 `colLetter/colIndex`。
3. 匹配失败的字段 → 列出候选表头交 AI 确认（steer），或按配置 `defaultCol` 兜底。
4. 校准结果写回 checkpoint `columns[]`；此后 FILLING/VERIFYING 只用 `colLetter`，不再每次读表头。

---

## 5. 填表执行原语

### 5.1 单单元格写入序列（L2 GoTo 主路径）

```
1. excel_goto  {ref:"Sheet1!B5"}        # F5 → 键入引用 → Enter（自动滚动+选中）
2. 清空        key DELETE                # 选中态下 Delete 清空（避免残留/追加）
3. 写入        type(value)               # KEYEVENTF_UNICODE 直键（中文/Unicode OK）
              或 clipboard_set + Ctrl+V  # 多行/超长/含 Alt+Enter 的值走粘贴
4. 提交        key ENTER                 # 提交并下移（或 TAB 右移）
```

- **逐格 GoTo 而非依赖上格位置**：更慢但绝对稳健；后续性能优化可加 `excel_write_row`（helper 内一次 GoTo 后 `Tab` 依次写一行，减少往返）。
- **窗口聚焦前置**：每次写前 `focus{hwnd}` 把 Excel 置前台 → `GuardInject` 等待用户空闲 → 再注入；写后短暂延时（100–300ms，沿用 BLUEPRINT E 节经验）再验证。

### 5.2 字段类型处理（Excel 写入大坑，必须显式）

| 字段类型 | 写入方式 | 说明 |
|---|---|---|
| `text` | 直键；若值以 `=` `+` `-` `'` 开头或含前导零 → 前缀 `'`（强制文本） | 防止被当作公式/数字吞前导零 |
| `number` | 直键纯数字+小数点 | 用系统小数点（中文 Windows 为 `.`）；不写千分位 |
| `date` | 默认写文本 `'2024-01-01`（前缀 `'`） | 避免区域/格式歧义；确需真日期类型时再设单元格 NumberFormat（L2 后加一步，实现期可选） |
| `multiline` | `clipboard_set` + `Ctrl+V` | `type` 不支持换行/Alt+Enter |
| `formula-literal` | 前缀 `'` 后直键 | 存 `=A1` 字面量而非公式 |

### 5.3 跨窗口聚焦管理

- 「跨窗口」指：Excel 窗口 ↔（图片浏览窗口 / DSH GUI）之间切换，以及多 Excel 实例间切换。
- 规则：**注入只发生在已 `focus` 且前台校验通过的目标窗口**；每次注入前重验前台 `hwnd==目标`，不一致 → 重新 `focus` 或 `PAUSED(excel-dialog)` 上报 AI。
- 图片查看：图片是文件，AI 用视觉管线（`read_image`/本地 VLM）读，不占窗口焦点；如用户用看图器对照，AI 只读不改窗口。

---

## 6. 填表后回读验证方案

### 6.1 三级回读（按精确度排序，逐级回退）

| 级 | 方法 | 精确度 | 适用 |
|---|---|---|---|
| **V1 剪贴板** | 选中单元格 → `Ctrl+C` → `clipboard_get` | **精确**（位级） | 默认首选 |
| **V2 UIA Value** | 读可视单元格 `ValuePattern`（L1） | 精确（可视时） | 剪贴板被占/失败时 |
| **V3 OCR** | `screenshot` 单元格区域 → `ocr` | 模糊（有噪声） | 前两者都不可用时 |

- **剪贴板礼仪（隐私/礼貌）**：回读前保存用户剪贴板原内容，回读后恢复；恢复失败要显式报告（不静默吞）。
- **剪贴板占用**：`OpenClipboard` 失败（被其他进程锁定）→ 立即回退 V2/V3，不重试。

### 6.2 比较与归一化

```
normalize(v) = trim → \r\n→\n → 全角→半角(ASCII 字符) → 去不换行空格(NBSP) → 尾随零规整(数字)
```

- `text`：归一化后**全等**为 PASS；仅 V3 OCR 结果允许**模糊**（Levenshtein ≤ 阈值，如 `≤ max(2, 0.1*len)` 且对易混字符映射 `0/O、1/l、8/B` 后相等）。
- `number`：解析为数值，`|a-b| ≤ max(0.01, 0.001*|b|)`。
- `date`：解析为日期对象比较（文本写入时按 `yyyy-MM-dd` 解析）。
- 多行：按行拆分逐行比。

### 6.3 重试策略

```
mismatch → 重选(ref) → Delete → 重写 → 回读 → 仍 mismatch → …
   最多 3 次；3 次后字段 failed
连续 3 个字段 failed → 该图 failed（防整批带病）
```

- 每次重试**重新 GoTo**（不信任上一次光标位置）。
- 记录 `verify.mismatches[] = {field, expected, actual, via, attempt}`，进报告。

### 6.4 行级 / 批级验证与报告

- **行级**：某行全部字段 PASS → `verified`；任一字段 3 次失败 → 该图 `failed`（保留已写内容，报告定位）。
- **rowKey 防错位**（§3.3）：配置 `rowKeyColumn` 时，回读该列并与图片 key 比对，不一致 = 错位 → 该行 `failed` + 高亮报告。
- **批级**：`VERIFYING` 完成后做一次**全量复验**（可选 `reverify:true`）：只读不写，逐行回读 → 产出报告：
  - `report.json`：`{total, verified, failed, skipped, mismatches[], durationMs, images[]}`
  - 人类可读摘要（Markdown）→ 存 `out/` + `notify_user` 通知。
- **幂等**：`verified` 是唯一推进条件，崩溃重放只重做非 verified 行，不会重复「推进成功但漏写」。

---

## 7. 容错与恢复

| 故障 | 检测 | 恢复 |
|---|---|---|
| 用户操作中 | `idle` < 300ms / `USER_ACTIVE` | 等待≤5s → 仍操作则 `PAUSED`，空闲续跑 |
| 注入锁（热键/stop） | helper `hotkey` 事件 / `locked` | `PAUSED`，仅用户 `resume` |
| Excel 弹窗 | 前台非 Excel 主窗口 | `PAUSED(excel-dialog)` + steer AI（截图给 AI 判断） |
| Excel 关闭/切换工作簿 | `windows` 找不到目标 hwnd/pid | `FAILED`，提示重新打开后 `retry` |
| 文件被锁（他人编辑中） | 写入报错 / 只读 | `PAUSED` 提示关闭独占 |
| helper 崩溃 | 看门狗 3s 重启 + 握手 | 从 checkpoint 重放当前图 |
| 剪贴板被占 | `clipboard_get` 失败 | 回退 V2/V3 回读 |
| 目标行非空 | 行占用检查 | 该图 `skipped`（除非 `overwrite:true`） |
| VLM/OCR 提取失败 | 单图提取异常 | 重试/降级引擎 → 仍败则图 `failed`，不阻塞他图 |

---

## 8. helper 协议新增命令（草案）

> 已实现（2026-10）：`VkOf` 已支持 F1–F12/字母/数字；`hotkey`、`clipboard_get`、`clipboard_set`、`focus` 已在 helper 落地，并暴露为插件工具 `saperm_hotkey`/`saperm_clipboard`/`saperm_focus`/`saperm_windows`/`saperm_recognize`。

| cmd | 参数 | 返回 | 说明 |
|---|---|---|---|
| `focus` | `{hwnd}` | `{ok, foreground, hwnd}` | 目标窗口置前台（RestoreWindow + SetForegroundWindow） |
| `hotkey` | `{keys:["CTRL","G"]}` | `{done}` | 组合键（Excel GoTo/复制/粘贴） |
| `clipboard_get` | `{restore?=true}` | `{text, hadContent, restored}` | STA 线程读剪贴板，默认保存/恢复用户原内容 |
| `clipboard_set` | `{text, restore?=true}` | `{done}` | 写剪贴板（多行/超长值粘贴用） |
| `excel_goto` | `{ref:"Sheet1!B5"}` | `{done, activeRef}` | F5 定位 → 键入引用 → Enter |
| `excel_write_cell` | `{ref, value, asText?, clearFirst?=true, via?="type"}` | `{done, committed}` | 复合：goto+clear+write+enter（常用路径） |
| `excel_read_cell` | `{ref, via?="auto"}` | `{value, via, ok, raw}` | auto=V1→V2→V3 逐级回退回读 |
| `excel_grid` | `{hwnd?}` | `{gridRect, cells:[{name,value,rect}], headers[]}` | 可视网格 dump（表头校准/坐标标定用） |

现有命令复用：`windows`（找 Excel）、`uia`（L1 读值/表头）、`screenshot`+`ocr`（V3 回读/校准）、`click/type/key/scroll`（输入）、`idle`（让位）、`stop/resume`（锁）、`watch/overlay`（可选实时进度展示）。

---

## 9. 插件工具草案与安全权限

### 9.1 高层工具（AI 与编排的接口）

| 工具 | 作用 | 权限 |
|---|---|---|
| `saperm_fill_start` | `{excelPath, sheet, sourceDir, columns, mapping}` 建 Job → SCANNING | L1 观察（读盘）+ 后续 L2/L3 按注入类型 |
| `saperm_fill_status` | 读 checkpoint → 进度摘要（当前图/行、%、failed 明细） | L1 |
| `saperm_fill_extract_next` | 取下一张待提取图 → AI 返回字段（AI 在环） | L1 |
| `saperm_fill_submit` | `{imageId, fields}` 提交提取结果 → 进入 filling | L1（只改 checkpoint） |
| `saperm_fill_step` | 推进一行（write+verify）一个原子步 | L2（write）|
| `saperm_fill_pause/resume/abort/retry` | 状态控制 | L1 / resume 弹审批 |

### 9.2 权限映射（沿用现有三级门控）

- **L1 观察**：枚举文件、读 checkpoint、UIA 读值、回读验证（剪贴板读回读**新增长**——读剪贴板涉及隐私，建议纳入 L2 首次授权说明，或默认「回读后恢复」并明示）。
- **L2 普通输入**：`type/click/scroll/hotkey`（单元格写入、GoTo 导航）——会话级授权（已具备）。
- **L3 高危逐次**：`drag/doubleclick`（一般不用于填表）；**跨窗口 `focus` 切换**建议按 L2 会话级（属常规输入导航，非系统区域）。
- **急停**：`Pause` 热键 / `stop` 全程有效；`resume` 必须用户确认。
- **剪贴板隐私声明**：`clipboard_get` 默认 `restore`，回读后还原用户剪贴板；restore 失败显式报告。

---

## 10. 里程碑与验收

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M-A | 状态机 + checkpoint 骨架（插件侧）+ `fill_status` | 模拟 200 图 Job，进度/续跑正确；崩溃重放不重复推进 |
| M-B | Excel L2 GoTo 定位 + 写单元格（含 `hotkey`/`VkOf` 补齐） | `excel_write_cell` 写 50 格，含中文/数字/日期/前导零文本，值正确 |
| M-C | 三级回读验证 + 重试 + 报告 | 人为造 mismatch，3 次重试后 failed 定位准确；剪贴板恢复验证 |
| M-D | 端到端：多文件夹多图 → 填表 → 全量复验 | 真实台账 200 行，`verified/total ≥ 99%`，无串行（rowKey 校验通过），报告落 `out/` |
| M-E | 容错：Excel 弹窗/关闭/锁/急停 | 每类故障注入一次，状态机按 §7 正确转态并可续 |

**本任务交付核对**：①状态机（§2，含当前进度 `stats`/`status` 与图片↔行映射 §3）②Excel 单元格 UIA 定位（§4，三级策略 + 虚拟化/滚动 + 列校准）③回读验证（§6，三级回读 + 归一化 + 重试 + 报告）——均已覆盖。

---

## 11. 实测验证记录（2026-10）

### 11.4 填表管线端到端（2026-10-23）
- 图片文件 OCR：helper 新增 `ocr_file {path, scale}` → 3 张测试图全部识别（中文正确：\"重启 DSH。现在做端到端演示\"、\"watch 采集启动成功。测手动快照\"），热路径 ~80-130ms/张
- 填表状态机（插件）：`saperm_fill_start/status/next/submit/write/abort` — checkpoint JSON 落 `out/fill/<id>.json`，图片↔行映射 1:1，nextRow 递增
- 写表：GoTo A{row} → 剪贴板 TSV（Tab 分隔）→ Ctrl+V 一次填整行 → **区间回读**（GoTo \"A{row}:{lastCol}{row}\" → Ctrl+C → 剪贴板比对）
- **结果：3/3 行写入并回读校验通过**
- **关键坑**：单格 Ctrl+C 只复制单格 → 校验必须复制**区间**（\"A2:B2\"），否则多列行永远校验失败


### 11.1 Excel 端到端（真实 Excel 16，工作簿1）
1. `focus {hwnd}` → `done:true`，前台确认为 `XLMAIN`（**注意**：需 Alt 空按解锁前台锁，已实现）
2. `hotkey {keys:["F5"]}` → `type "B5"` → `key ENTER` → GoTo **成功**
3. `type "测试123"` → ENTER 写入成功
4. 再 GoTo B5 → `hotkey CTRL+C` → `clipboard_get` = **"测试123"**（含 Excel 复制尾随 \r\n，已裁剪）
5. 结论：**L2 键盘导航 + 写入 + V1 剪贴板回读 全链路可用**

### 11.2 Excel UIA 网格（L1）
- 暴露 `DataGrid:网格` 1 个 + **DataItem 单元格 645 个**（A1@29,237、B5@98,311…）
- **离屏单元格坐标为虚拟化假值**（如 A10@-21305,-20929）→ 必须过滤（插件 `saperm_excel_cells` 已过滤 x/y<0）
- 结论：L1 可读可见单元格名+坐标；离屏必须走 L2 GoTo

### 11.3 已落地命令/工具
- helper：`hotkey`（组合键）、`clipboard_get`、`clipboard_set`、`focus`（Alt 解锁 + SwitchToThisWindow 兜底）
- 插件：`saperm_hotkey`、`saperm_clipboard`、`saperm_focus`、`saperm_windows`、`saperm_recognize`、`saperm_excel_goto`、`saperm_excel_write`、`saperm_excel_read`、`saperm_excel_cells`
- 注：插件工具需重启 DSH 后生效。
