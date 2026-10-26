# dsh-screen-attention-perm

DSH 屏幕注意力插件 · 权限模块 + 观察引擎（单引擎捆绑架构，2026-08-28 完整版）。

## 功能

### 权限（DSH 原生审批链）
- **L2 会话级授权**：`saperm_click` / `saperm_scroll` / `saperm_type` 本会话首次调用弹审批，批准后免审；拒绝 fail-closed 且区分审批拒绝与执行期错误（USER_ACTIVE 让位等保持未决可重试）。
- **L3 高危逐次审批**：`saperm_drag` / `saperm_doubleclick` 每次单独审批。
- **急停锁**：`saperm_stop` 免审锁定全部注入；急停下注入工具不弹审批直接拒。
- **恢复需用户确认**：`saperm_resume` 每次弹审批——AI 无法自行解除。
- **观察免审**：`saperm_status` 随时可查授权与急停。

### 观察引擎（单一采集，多个消费端）
- **单引擎铁律**：helper 内一个 watch 采集循环（GDI 全屏，静止 intervalMs / 运动自动 100ms）是唯一帧源；overlay 小窗画面、变化检测、焦点 OCR 全部消费它。
- **高频自适应**：静止低频（默认 1000ms，≈1.7% 单核），运动自动提速 100ms（迟滞 3/6 帧防抖）。
- **变化检测**：稀疏网格（16px 步长/通道差 20/阈值 0.08%）+ 6 帧滑动窗口累计（细变滚动文字也感知）→ 变化率 + 包围盒；change 事件 3s 抑制上报。
- **多尺度 AI 视角**：`saperm_glance`（全屏大图）与 `saperm_view`{x,y,w,h,scale,maxW,note}（任意区域+缩放）——统一虚拟屏幕坐标，小图 rect 即大图子区域（对应关系天然成立）；图片存 out/sa-view/*.jpg 返回 {path, rect}，不进模型上下文，视觉按需 read_image/vision_describe。
- **注意力可视化小窗**：无边框 TopMost（SetWindowPos 强制顶层）+ 屏幕缩略画面 + AI 注意力红框 + 注视绿十字 + 底部信息条；可拖动/双击关闭。
  - 打开时自动拉起 watch（autoWatch，关闭自动停止——捆绑）；AI 显式设定（view/overlay_set）后自动跟踪让位，否则脏矩形自动驱动红框（AI 自动视线）。
- **AI 会话语境感知**：`watch_focus`{x,y,w,h} 焦点区 OCR 增量 diff（added 字段）——对话滚动即感知 AI 自己输出的文本。
- **联动**（对话不中断）：`saperm_watch_link`{intervalMs,wake,focus}——change→OCR 摘要→inject（排队不打断）或 steer（唤醒实时响应），节流 2s+文本去重+分钟配额（60s ≤6 次）。`saperm_observe` 手动取当前快照。

## 工具清单

| 类别 | 工具 |
|---|---|
| 观察/状态 | saperm_status |
| 观察/采集 | saperm_watch_start / watch_stop / watch_status / watch_focus / watch_snapshot |
| 观察/联动 | saperm_watch_link / watch_unlink / observe |
| 观察/视角 | saperm_glance / view |
| 观察/小窗 | saperm_overlay_open / close / set |
| 注入 L2 | saperm_click / scroll / type |
| 注入 L3 | saperm_drag / doubleclick |
| 急停 | saperm_stop / resume |

## 实现机制
- 门控：tools/pre-execute waterfall（ask/deny），DSH registry approval 服务解析（allowed-once 唯一放行）；approval/asked + approval/decided 自动落日志审计。
- helper：dotnet9 单文件 exe（stdio JSON 行协议，UTF-8 强制）；GDI 采集 + Windows.Media.Ocr（并行条带 8 核自适应）+ FlaUI UIA + SendInput（40 字节 union INPUT）+ WinForms overlay。
- 授权/联动状态为内存态（per-agent），重启即清（安全默认）。

## 已知边界
- 急停物理层：Pause 热键（helper 隐藏热键线程）与 stop 命令；用户活动时 GuardInject 等待空闲（≤5s 超时才让位）。
- OCR 大缩放画面小字逐词（小窗缩略图场景）；焦点区原图 OCR 质量良好。
- vision-router OVH 后端可能 429，视觉看图用 read_image 兜底（模型多模态直读）。