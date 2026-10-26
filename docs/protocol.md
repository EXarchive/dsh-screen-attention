# SaHelper 协议 v1（stdio JSON 行协议）

- 传输：helper 进程 stdin/stdout，每行一个完整 JSON 对象，UTF-8。
- 请求：`{"id": <int>, "cmd": "<name>", ...命令参数}`
- 响应：`{"id": <int>, "ok": true, "data": {...}}` 或 `{"id": <int>, "ok": false, "error": "<msg>"}`
- 事件（helper 主动推送，与响应共用 stdout，由 `event` 字段区分）：
  `{"event": "ready"}` 启动完成
  `{"event": "hotkey", "locked": true|false}` Pause 热键切换注入锁
- 坐标：全部屏幕物理像素（VirtualScreen 坐标系，DPI 感知后与屏幕 1:1）。

## 命令集

| cmd | 参数 | 返回 data | 说明 |
|---|---|---|---|
| handshake | — | `{version: 1, cores, screen:{w,h}, ocrLang}` | 握手；版本不匹配应拒绝继续 |
| ping | — | `{pong: true}` | 心跳 |
| screen_bounds | — | `{x,y,w,h}` | 虚拟屏幕矩形 |
| windows | — | `{windows:[{hwnd,title,cls,x,y,w,h,pid,fg}]}` | 可见窗口枚举（注意力候选） |
| screenshot | `{maxW?=1280}` | `{w,h,jpegB64,bytes}` | JPEG 截图（内存帧→临时文件编码后即删） |
| ocr | `{x?,y?,w?,h?,scale?=1}` | `{items:[{source:"ocr",text,rect{x,y,w,h},line}], chars, lines, ms}` | 并行条带 OCR（核数自适应）；缺 rect 即全屏 |
| uia | `{x?,y?,w?,h?}` | `{items:[...]}` | 预留：UI Automation（M1 暂返回 not_implemented） |
| idle | — | `{idleMs}` | 用户输入空闲毫秒数 |
| click | `{x,y,button?="left"}` | `{done:true}` | 单击（40 字节 SendInput） |
| doubleclick | `{x,y}` | `{done:true}` | 双击 |
| move | `{x,y}` | `{done:true}` | 移动光标 |
| drag | `{x1,y1,x2,y2,durationMs?=300}` | `{done:true}` | 拖拽 |
| scroll | `{amount}` | `{done:true}` | 滚轮（正=上） |
| type | `{text}` | `{done:true, chars}` | Unicode 文本输入（KEYEVENTF_UNICODE） |
| key | `{name}` | `{done:true}` | 按键（ENTER/ESC/TAB/BACKSPACE/DELETE/方向键/F1-F12 等） |
| stop | — | `{locked:true}` | 注入锁（锁操作永远可用） |
| resume | — | `{locked:false}` | 解除注入锁（由插件侧审批确认后调用） |
| shutdown | — | `{bye:true}` | 退出 |

## 安全规则（helper 物理层）

1. **用户活动让位**：注入类命令（click/move/drag/scroll/type/key/doubleclick）执行前检查
   `GetLastInputInfo`；idle < 300ms（用户正在操作）→ 拒绝 `{"ok":false,"error":"USER_ACTIVE"}`。
2. **注入锁**：`stop` 后所有注入命令拒绝（识别/截图不受影响）。
3. **Pause 热键**：任意时刻切换锁，事件上报。
4. 帧不落盘：截图/OCR 均为内存帧；JPEG 编码使用瞬时临时文件，编码完成立即删除。
