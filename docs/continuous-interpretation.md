# 连续变化下的屏幕持续获取与解读（Interp Engine）

## 目标
在屏幕**连续变化**（视频/动画/滚动/界面切换）时，持续采集并解读：
- 持续获取：后台线程按间隔采集全屏（GDI，~17.8ms/帧）
- 变化定位：粗粒度帧差（步长 32px，通道阈值 24）→ 变化包围盒
- 变化区域 OCR：仅对变化包围盒（+12px padding）做 PaddleOCR（scale 1.5）
- 变化分类：
  - `content_change`：OCR 文本变了（内容性变化）
  - `dynamic_noise`：画面变了但文本没变（视频/动画/噪声 → 不重复解读）
  - `text_shift`：文本位置/顺序变化
  - `idle`：无变化
- 滚动历史：最近 300 条 {时间, 分类, 新增文本, 文本长度, OCR 耗时, 区域}

## 接口
| helper | 说明 |
|---|---|
| `interpret_start {intervalMs?, x?, y?, w?, h?}` | 启动；无区域=自动检测变化区域，有区域=固定区域解读 |
| `interpret_status {limit?}` | 运行状态 + 滚动历史 |
| `interpret_stop` | 停止 |

插件工具（L0 观察，无需审批）：`saperm_interpret_start/status/stop`

## 实测（2026-10-23，1920×1080）
- 固定区域 1180×700，间隔 700ms，滚动制造变化：
  - 5 ticks / 5 次 OCR / 平均 OCR 340ms
  - 历史：首帧 `content_change` → 未变帧 `dynamic_noise` → 滚动后连续 `content_change`
  - 文本正确读出：证序列 | 智能体团队 | PTC模式 | 费用明细 | 1个后台任务运行中
- 静态屏幕：DiffRect=0 变化 → 不 OCR（零浪费）

## 性能与频率（BLUEPRINT 三铁律）
- 采集与识别解耦：采集恒定，识别仅变化区域
- AI 可控频率：`intervalMs` 由调用方决定（最小 100ms）
- 识别结果不进对话：只回状态/历史，按需读取

## 已知限制
- OCR 耗时随区域大小增长（全屏 ~200-800ms）→ 建议固定较小关注区域或降低 scale
- 粗帧差对细微变化（光标闪烁）不敏感（有意为之，抑制噪声）
- 变化区域较大时 OCR 全区域；后续可做行/块级裁剪优化


---

## 12. P0 实施记录（2026-10-23）

### FrameHub 单一采集流
- 新增 `FrameHub`（helper）：一个采集线程（默认 100ms）发布最新帧 + 序号，`watch` 与 `interpret` 通过引用计数订阅（`Acquire/Release`）
- 无人订阅时自动停止采集（refs=0）→ 零空转
- Watch/Interpret 均改为 `FrameHub.WaitNext`，不再各自 `Gdi.Capture` 全屏

### 变化区域限幅
- 区域面积 >700k px：scale=1.0 + `maxBoxes=60`；>350k px：scale=1.2 + `maxBoxes=120`
- `PaddleOcr.Run` / `Ocr.Run` 新增 `maxBoxes` 参数（识别框数上限）
- 实测：全屏/大区域时 OCR P90 470ms（预算内）

### 指标
`interpret_status` 新增：`fps`、`captureMs`、`avgCaptureMs`、`diffMs`、`ocrP50Ms`、`ocrP90Ms`；历史条目含 `boxes` 与 `diffMs`。

### 实测（watch+interpret 同开，滚动变化）
- ticks 27 / ocrRuns 15 / fps 9.1 / captureMs 17-20ms / diffMs 0
- OCR P50 152ms、P90 470ms；分类正确（content_change / dynamic_noise / text_shift）
- watch 侧正常（motion/interval 可读）


---

## 13. 逐帧实时识别（Infer，2026-10-23）

**需求**：不能"先截屏再按需识别"，要求每一帧都实时送识别。

### 架构
```
FrameHub(每100ms一帧) → Infer 工作线程（每帧都处理）
  → det 每帧全量（PP-OCRv4 det，736 短边，~60-70ms）
  → rec 仅对「内容签名变化」的框执行（签名=框内采样像素 FNV 哈希）
  → 结果流式输出（seq/ocrMs/boxes/recHits/recMiss/文本）
```

- `PaddleOcr.RunStreaming`：det 全量 + rec 增量缓存（2000 条，超限清空）
- 「每一帧都送识别」，处理不过来时**丢帧并计数**（framesSeen / processed / dropped），不阻塞采集
- helper 命令：`infer_start {maxBoxes?}` / `infer_status {limit?}` / `infer_stop`
- 插件工具（L0）：`saperm_infer_start/status/stop`

### 实测（1920×1080，UHD 770 DirectML，maxBoxes=60）
| 指标 | 值 |
|---|---|
| 逐帧识别 fps | **9.3 - 9.6** |
| 单帧 OCR | **P50 100ms / P90 105ms** |
| rec 缓存命中 | 57-59 / 60（仅 6-8 框重识别） |
| 丢帧 | 12/54（处理 ~100ms ≈ 采集 100ms，偶尔掉帧） |
| 文本 | 正确（deepseek / HARNESS / 控制中心 / 参数 / 端口: 3085 …） |

### 物理上限说明
- 本机（iGPU + DirectML）：逐帧识别 ~10fps 已接近上限；**30fps 逐帧需 GPU（CUDA/TensorRT）**
- 若把 FrameHub 间隔调到 ≥120ms，可做到 0 丢帧（8fps 稳定逐帧）
- 语义理解（"画面是什么"）仍受模型源限制，需另补


---

## 14. 视觉物体理解（YOLOv8n，2026-10-23）

**需求**：重点是对每一帧画面内容的**物体描述**（不是文字）。

### 模型
- YOLOv8n → ONNX（12.8MB，COCO 80 类，中英标签）
- 导出方式：`ultralytics`（清华 PyPI 镜像安装）+ `YOLO('yolov8n.pt').export(format='onnx', imgsz=640)`
- 模型路径：`helper/models/yolov8n.onnx`（`yolov8n.pt` 保留源文件）

### 实现
- helper `YoloDetect`：letterbox 640 → DirectML/CPU 推理 → 解码(84×8400) → NMS(IoU 0.45)
- 输出每个物体：`{label, labelZh, conf, x, y, w, h, color}`（color=主色采样：红/橙/黄/绿/青/蓝/紫/粉/白/黑/灰）
- 加 `summary`："人×2、笔记本电脑×1"
- 命令：`detect {path?}`（缺省=当前屏幕）；`infer_start {objects:true}` 逐帧带物体检测
- 插件工具：`saperm_detect {path?}`、`saperm_infer_start {objects:true}`

### 实测（1920×1080，DirectML）
| 项 | 结果 |
|---|---|
| 单帧 detect | **22-96ms**（DirectML，含冷启动 205ms 首次） |
| 逐帧 infer（OCR+物体） | **fps 9.0**，OCR P50 80ms + 物体 22-41ms |
| 识别结果 | 人 (0.58)、笔记本电脑 (0.47/灰) 等，坐标+置信度+主色 |
| 说明 | 屏幕内容为 UI/游戏时物体较少；真实场景（人物/物品）识别效果更好 |

### 边界
- COCO 80 类：常见人/动物/车辆/家具/电子产品等；不认识的物体不会输出
- 模型对 UI 截图（无真实物体）检出少属正常
- 30fps 逐帧 + 物体需独显（CUDA/TensorRT）；本机 ~9fps


---

## 15. 物体跟踪与变化描述（2026-10-23）

### 新增能力
- **跨帧物体跟踪**：IoU>0.4 + 同标签匹配，分配稳定 `id`（`#1`、`#2`…）
- **变化描述**：`出现` / `消失` / `移动`（中心位移 >10px），写入**滚动变化日志**（最近 100 条，状态返回最近 10 条）
- **误报过滤**：面积 >70% 全屏的检测框丢弃（UI 大窗口误报）；置信度门槛 0.30 → **0.35**

### 接口
`infer_status` 的 `latest` 新增：
```json
{
  "objects": [{"id":1,"labelZh":"人","color":"灰","conf":0.59,"x":..,"y":..,"w":..,"h":..}],
  "tracks": 1,
  "changes": ["人#1 出现"],          // 最近一帧
  "changesRecent": ["13:29:55 人#1 出现", "13:29:58 床#2 出现"]  // 滚动日志
}
```

### 实测
- 移动窗口：`人#1` 保持同一 ID（跟踪成功），新物体记为 `床#2 出现` 并写入日志
- 静态屏：ID 稳定、无变化日志
- 物体层：YOLOv8n DirectML 22–41ms/帧（逐帧 infer 总 fps ~8-9）

### 已知局限
- COCO 模型对 UI/游戏画面仍有误报（窗口→"笔记本电脑"、面板→"床"），已用面积过滤 + 置信度缓解
- 真实场景（人物/物品/车辆）识别效果显著更好


---

## 16. 场景语义分类（Chinese-CLIP，ACG 适配，2026-10-23）

### 模型
- **Chinese-CLIP ViT-B/16**（ModelScope `AI-ModelScope/chinese-clip-vit-base-patch16`）
- 导出：图像编码器 → `image_encoder.onnx`（345MB，opset14，动态 batch）
- 文本侧：34 个中文标签 × 4 个提示词模板（`{}`、`一张{}的图片`、`这是{}`、`{}截图`）平均嵌入 → `labels.json`（512 维）
- 标签覆盖 ACG：二次元游戏界面、抽卡游戏、视觉小说、漫画页面、动画视频、VTuber 直播、像素风游戏、游戏战斗/菜单、动漫场景插画等

### 实现
- helper `ClipScene`：中心裁剪 224 → CLIP 归一化 → DirectML 推理 → 余弦相似度排序
- 命令：`scene {path?, topK?}`（缺省=当前屏幕）
- 逐帧：`infer_start {scene:true}` → 每帧输出场景标签
- 插件工具：`saperm_scene`、`saperm_infer_start {scene:true}`

### 实测
| 场景 | 结果 |
|---|---|
| 桌面+QQ | 聊天软件窗口 0.477、桌面图标网格 0.452 |
| 二次元/游戏截图 | 视觉小说界面 0.483、VTuber直播界面 0.477、二次元游戏界面 0.434 |
| 逐帧耗时 | **13-15ms**（DirectML，模型常驻）；冷启动 ~220-360ms |
| 逐帧 infer（OCR+物体+场景） | fps ~6.9（OCR 87ms + 物体 24ms + 场景 13ms） |

### 资源策略
- 已按"解除资源限制"使用更大模型（CLIP 345MB ONNX 常驻，约 1-2GB 内存）
- 可进一步升级 ViT-L/14-336（精度更高，需再导出/约 1.9GB）

### 局限
- 零样本分类为"场景级别"，非自由描述
- 相似度区分度中等（0.4-0.5 区间），标签集与提示词影响大
- 与 YOLO 物体检测互补：场景=整体语义，物体=具体目标


---

## 17. 中期目标：识别 Harness 中的人物（2026-10-23）

**目标**：识别 DSH Harness 界面中的角色（女仆等），输出"谁 + 位置 + 相似度"。

### 方案（三合一）
1. **人物检测**：YOLOv8m（精确模型）检测 person 框 —— Anime 角色也能命中（实测 conf 0.82-0.93）
2. **角色嵌入**：CLIP 裁剪嵌入（`ClipScene.Embed`，768 维，Chinese-CLIP ViT-L/14）
3. **注册/匹配**：`CharId` 注册表（`models/characters.json`），余弦相似度匹配（阈值 0.70）

### 接口
| 命令 | 说明 |
|---|---|
| `char_register {name, path?, x,y,w,h?}` | 注册角色；不传区域=自动检测最大人物框 |
| `char_match {path?, x,y,w,h?}` | 检测并匹配已注册角色 |
| `detect {model:"m"}` | 人物框自动附加 `identity` + `identityScore` |

插件工具：`saperm_char_register`、`saperm_char_match`（L0）

### 实测
```
char_register {name:"harness女仆"} → ok, dim 768, rect (1055,673,252x386)
char_match → harness女仆 score 0.843
detect {model:"m"} → 人 0.925 => harness女仆 0.76 @ (1037,342)
```

### 下一步（该目标延伸）
- 多角色注册（Harness 女仆 / 用户头像 / 游戏角色），匹配返回 top-1 + 置信度
- 逐帧 infer 集成身份（物体列表带 identity，变化日志含"角色出现/离开"）
- 角色专属模型（anime face detection / 角色识别模型）提升 ACG 角色精度
- 注册表管理接口（列出/删除/重命名）


### 17.1 扩展（2026-10-23 续）
- **注册表管理**：`char_list` / `char_delete {name}` / `char_rename {from,to}`（插件工具同名 saperm_*）
- **逐帧身份**：`infer_start {objects:true, accurate:true}` 的人物框自动附加 identity；变化日志带角色名（"harness女仆#1 出现/消失/移动"）
- **多角色**：注册表为字典，可注册多个角色（女仆/头像/游戏角色），匹配返回 top-1 + 相似度
- **实测**：`detect {model:"m"}` 于 DSH 界面截图 → 人 0.8 => harness女仆 0.835；char_match 同值
- **待办**：ACG 角色专属模型（anime face cascade / 角色识别模型）——模型源受限（GitHub/HF 不可达），后续补

### 17.2 多角色注册与匹配回归（2026-10-23）

把"注册多个角色 + 匹配 top-1"从口头能力固化为可重跑测试（`tests/char-multi.mjs`），全部走 helper 的 stdio 命令，不直接改 `models/characters.json`。

**注册样本**（`char_register {name,path,x,y,w,h}`，768 维，写入 `helper/models/characters.json`）：

| 名称 | 截图 | 区域 x,y,w,h | 说明 |
|---|---|---|---|
| `harness女仆2` | `out/sa-view/mid.jpg`（1100×720） | 600,280,300,300 | DSH 界面女仆，头部+上半身 |
| `harness女仆3` | `out/sa-view/big.jpg`（1280×720） | 640,290,240,240 | 同一角色的不同姿势/尺寸（1280 窗口） |
| `白毛终端少女` | `out/sa-view/task-1787947113534-45.jpg`（900×506） | 260,72,160,160 | 终端窗口内的动漫角色（不同身份） |

基线 `harness女仆` 保持不动（注册前 1 个，注册后 4 个）。**区域要选方形头部+上半身**：`ClipScene.Embed` 对方框做 `min(w,h)` 中心方裁，长条框会裁掉脸；不传区域则走 YOLO 最大人物框（自动但不可控，ACG 角色 conf 0.5–0.8 时容易框到窗口背景）。

**实测**（DirectML，单进程；名称由 `char_match`/`detect` 返回）：

| 用例 | 结果 |
|---|---|
| 自匹配（注册截图的注册区域，3 个角色） | 名称 3/3 正确，score 1.000 |
| 跨截图同角色 `glance-1792733333431-3`（555,575,250,250） | top-1 `harness女仆2` 0.852 |
| 跨截图同角色 `task-1788460279425-1`（284,367,193,193） | top-1 `harness女仆2` 0.867 |
| `detect {model:"m"}` `mid.jpg`（2 个人物框） | top-1 `harness女仆2` 0.919 |
| `detect {model:"m"}` 白毛截图（1 个人物框） | top-1 `白毛终端少女` 0.897 |

注：`detect` 用的是 YOLO 自己的 person 框，和注册手选区域不完全相同，因此同一角色的多个注册之间 top-1 可能与注册截图不一致；自动化里对 detect 断言"女仆族 top-1"而非某个固定名字。

**自动化**：`node tests/char-multi.mjs`（退出码 0=全过，1=有失败）

覆盖与断言：`char_list` 基线 → 缺角色才补注册（幂等） → 自匹配（名称 + 相似度 ≥0.99）→ 跨截图（≥0.80）→ `detect {model:"m"}` 身份（≥0.80）→ `char_rename`（临时 ASCII 名，用唯一嵌入避免与既有角色并列 1.0 打平）→ `char_delete`（含重复删除返回 false）→ 基线仍在、临时角色清理（`finally` 清理，绝不碰 `harness女仆`）。**2026-10-23 实测：稳态 32/32 PASS（退出码 0），补注册分支 33/33；连续多次运行结果一致。**

**行为与局限（实测归纳）**
- 匹配 = 全表余弦取最大，阈值 0.70（低于则 `name:null`）；没有"未知/陌生人"类别，0.70 对同角色跨截图（0.85–0.92）宽裕、对陌生人也偏宽松（可加 top-2 margin 或自适应阈值）
- **进程级缓存**：`CharId` 首次访问后常驻内存；另一个进程写盘后，已在运行的进程看不到新注册 → 改注册表后需新进程/重启 helper（插件看门狗会自动拉起）
- **全表覆盖写**：`char_register/delete/rename` 把本进程内存的整表写回，多进程并发注册 = 最后写入者覆盖，会丢别的进程新加的项
- 同一角色多登记会互相竞争 top-1（同分时先插入者胜，严格 `>` 比较）；要区分不同角色就注册不同角色的截图
- `rename` 只改键不动嵌入；`delete`/`rename` 对不存在的名字返回 `ok:false`（不抛错）


## 18. 变化事件注入会话（方向3，2026-10-23）

把"内容变化 / 角色出现消失"事件按节流注入 DSH 会话，供 AI 实时感知，不刷屏。

### 开关（插件工具参数，默认关闭）
| 工具 | 参数 | 默认 | 说明 |
|---|---|---|---|
| `saperm_interpret_start` | `inject` | `false` | 是否注入内容变化事件 |
| | `minInjectMs` | `2000` | 两条注入的最小间隔（200–60000） |
| | `kinds` | `["content_change"]` | 注入哪些变化类型（另可选 `text_shift` / `dynamic_noise` / `idle`） |
| `saperm_infer_start` | `inject` | `false` | 是否注入角色出现/消失事件 |
| | `injectChanges` | `true` | false=不注入变化（`inject` 打开时仍可用它临时静音） |
| | `minInjectMs` | `2000` | 同上 |

### 机制
- 插件启动对应观察后，注册 `setInterval(1000)` 轮询 `interpret_status` / `infer_status`（helper 侧只是读内存状态，开销可忽略；`busy` 护栏防重入）
- **游标**：解读按历史最后一条时间戳 `t`，识别按帧 `seq`；**首轮只对齐游标不注入**（不补播历史），停止/重启后重新对齐
- **过滤**：解读只取 `kinds` 命中的且 `added` 非空的条目；识别只取 `changes` 中匹配 `^(.*?)#(\d+)\s*(出现|消失)$` 的角色事件（"移动"等噪声不注入），同角色同动作 10s 内去重
- **节流**：不满足 `minInjectMs` 时进待发队列（最多 6 条），到点合并成**一条**消息注入
- **注入**：复用 `injectToSession`（`source.kind='plugin'`，inject 不唤醒、不打断对话），每条 ≤200 字（超长截断加 …）
- 格式：
  - `[解读] content_change @(x,y,w,h)：新增文本`
  - `[角色] harness女仆 出现 @(x,y,w,h)` / `[角色] harness女仆 消失`
- `saperm_interpret_start/status/stop`、`saperm_infer_start/status/stop` 返回 `injection` 字段（inject/minInjectMs/kinds/pending/polling），`saperm_status` 同时汇总两者

### 停止与急停
- `saperm_interpret_stop` / `saperm_infer_stop`：清理轮询定时器 + 清空待发队列（不补播旧事件）
- helper 侧停止（`running=false`）被轮询发现时，也自动清理定时器
- **急停/锁（`saperm_stop` 或 Pause 热键）只停注入、不停观察**：轮询继续推进游标并丢弃待发，观察/OCR 不受影响；解除后从当前时刻继续

### 验证
- 语法：`node --input-type=module -e "import('file:///.../permission-plugin/lib/index.js')"` → SYNTAX OK（2026-10-23 实测通过）
- 逻辑：用模拟 helper 状态驱动该段真实代码，覆盖首轮对齐、kinds 过滤、节流合并、≤200 字、暂停丢弃、停止清理、seq 游标、角色去重/消失（全部 PASS）
- 真机：mock ctx + 真实 helper 跑 6s，会话侧收到 `[解读] content_change @(532,20,248,56)：…` 一条（节流生效）
- **生效需重启 DSH**（插件改动不热更）；重启后 `saperm_interpret_start {inject:true}`，用记事本/浏览器制造文本变化即可在会话里看到 `[解读]` 摘要

### 限制
- 角色事件依赖 `saperm_infer_start {objects:true}`（无 objects 时不会产生出现/消失事件；返回里带 `injectionNote` 提示）
- 1s 轮询粒度：两次轮询之间的极短变化仍会保留在历史里交由下一轮处理，但若历史窗口（解读 300 条 / 识别 300 条）在两次轮询间被冲掉，可能漏播（解读窗口 300 条按 800ms 间隔约 4 分钟，实际足够）
- 注入是否唤醒对话取决于 `injectToSession`（inject 不唤醒）；需要 AI 立即响应时配合 `saperm_observe_task` 的 reportEvery 或 `saperm_watch_link {wake:true}`

## 19. ACG 动漫脸检测模型（方向2，2026-10-23）

目标：为"动漫/游戏/视觉小说"画面找到可用的**动漫脸检测**模型，落地到 `helper/models/animeface/`，并在 `out/sa-view` 的实机截图上验证。结论：**已拿到并验证通过**（YOLOv8n ONNX 主模型 + OpenCV 级联兜底）。

### 19.1 网络与来源实测
| 目标 | 结果 |
|---|---|
| 代理 `127.0.0.1:7891` | **未监听**（clash-verge 未启动）；本次全程未依赖代理 |
| `modelscope.cn` / `modelscope.ai` | 直连 200 |
| ModelScope 搜索 API `/api/v1/models`、`/api/v1/dolphin/models` | 404（接口已变更）；`/api/v1/search/models` 503 |
| ModelScope 文件下载 API `/api/v1/models/{ns}/{model}/repo?Revision=master&FilePath=...` | **200，可用**（已用它验证 damo retinaface 的配置可下） |
| `github.com` / `huggingface.co` 直连 | 不可达（按环境说明） |
| `hf-mirror.com`（HF 国内镜像） | **200，可用**：HF API 搜索/文件下载均可走它 |
| `pypi.tuna.tsinghua.edu.cn` | 200 |
| `sourceforge.net`（lbpcascade 镜像） | 200 |
| `api.nuget.org` | 200（OpenCvSharp4 可获取，供集成备选） |

失败路径记录（避免后人重复踩）：
- PyPI `animeface`（nya3jp/python-animeface，libnvxs 封装）：2.0.3 只有 Linux/macOS wheel，Windows 需自行编译 vendored CLAPACK，**未采用**；其 sdist 里也**不含** cascade xml。
- PyPI `anime-face-detector`（hysts，YOLOv3/HRNet）：wheel 只有代码，权重在 GitHub Releases / HF，直连不可达；改为从 hf-mirror 取 deepghs 系列 ONNX。
- ModelScope 上未检索到公开的 `iic/cv_*anime*`、`damo/cv_*anime*` 动漫脸仓库（候选 id 均 404）；ModelScope 的动漫脸生态目前弱于 HF 镜像。

### 19.2 模型资产（helper/models/animeface/）
| 文件 | 大小 | 说明 |
|---|---|---|
| `model.onnx` | 12,102,558 B（sha256 FD860B65…EF04A9） | 主模型：`deepghs/anime_face_detection` → `face_detect_v1.4_n`，YOLOv8n 单类 `face`，F1 0.94 @ 阈值 0.278，MIT |
| `lbpcascade_animeface.xml` | 246,945 B（sha256 9376D30A…F1CE30） | 兜底：nagadomi lbpcascade（MIT），OpenCV 直接加载 |
| `labels.json` / `threshold.json` | 14 / 48 B | `["face"]` / `{"f1_score":0.94,"threshold":0.278}` |
| `README.md` / `UPSTREAM_README.md` | — | 来源、许可、下载 URL、用法 |

下载地址（可复现）：
- ONNX：`https://hf-mirror.com/deepghs/anime_face_detection/resolve/main/face_detect_v1.4_n/model.onnx`
- 级联：`https://sourceforge.net/projects/lbpcascade-animeface.mirror/files/latest/download`（zip 内含 xml）

模型 IO：输入 `images` float32 `[batch,3,H,W]`（动态，建议 640×640 letterbox，114 灰边）；输出 `output0` `[batch,5,8400]`，每列 `cx,cy,w,h,score`（需先转置为 `[N,5]`）。可选升级：同仓库 `face_detect_v1.4_s`（11.1M 参数，F1 0.95，ONNX 44.6MB）。

### 19.3 实机验证（脚本 tests/animface_test.py）
```
python tests/animface_test.py                     # 默认 onnx，扫 glance-*.jpg + task-*.jpg
python tests/animface_test.py --backend cascade   # 级联对照
python tests/animface_test.py --image x.jpg --conf 0.3 --top 4
```
2026-10-23 实测（162 张，1280×720 / 900×506 / 1920×1080 混合）：

| 后端 | 有脸图 | 检出框 | 总耗时 | 单张 |
|---|---|---|---|---|
| ONNX（v1.4_n） | 133 / 162 | 253 | 5.09 s | **≈31 ms** |
| 级联兜底 | 139 / 162 | 293 | 2.88 s | ≈18 ms |

质量（人工看图核对标注图 `tests/animface_previews/`）：
- ONNX 在 1280×720 的"双女仆"截图上稳定检出 2 张脸，框 `(287,327,44,45)`、`(1132,349,52,49)`，置信度均 0.72，**无 UI 误检**；
- 级联在同一张图上检出 3 个框，多出的 `(83,459,58,58)` 落在 DeepSeek 鲸鱼 logo 上 = 误检；文字/图标区域也会被级联误报；
- 级联多出的 40 个框基本都是这类噪声，因此**集成以 ONNX 为准**，级联仅作无 onnxruntime 时的兜底。

报告产物：`tests/animface_report_onnx.json`、`tests/animface_report_cascade.json`、`tests/animface_previews/preview_*.jpg`。

### 19.4 集成建议（本次不改 Program.cs / 不动插件）
与 helper 现有 ONNX Runtime 技术栈天然一致（无需 OpenCV、无需 Python 侧车）：

1. 新增 `helper/AnimeFaceDetect.cs`（参考 `YoloDetect.cs` 的 ORT 会话写法，独立文件避免冲突）：
   - 懒加载 `helper/models/animeface/model.onnx`，CPU/DirectML 均可；
   - letterbox 640（比例缩放 + 114 padding）→ RGB float32 /255 → NCHW；
   - 输出 `[1,5,8400]` 转置 → `conf >= 0.25`（或 0.278）过滤 → `cv2.dnn.NMSBoxes` 等价 IoU≥0.45 的 NMS → 反算回原图坐标并 clip；
   - 返回值：`count` + `faces[{x,y,w,h,conf}]` + 耗时。
2. `Program.cs` 增加命令（建议规格，待排期）：
   ```
   {"id":1,"cmd":"animeface","path":"C:\\...\\shot.jpg","conf":0.25,"maxFaces":20}
   → {"id":1,"ok":true,"count":2,"faces":[{"x":287,"y":327,"w":44,"h":45,"conf":0.72}],"ms":31}
   ```
   `path` 省略时与现有 `detect`/`scene` 一致取当前屏幕（GDI 采集后推理）；同时注册对应的 `saperm_animeface` 插件工具（L0 观察，无需审批）。
3. 与既有能力联动：
   - `infer_start {objects:true}` 可把该检测结果作为 `animeface` 类对象参与"角色出现/消失"事件（与 `char_id` 的角色匹配互补：先定脸、再识人）；
   - `scene` 判定为二次元/视觉小说类时，自动启用动漫脸检测，避免对普通桌面做无效推理（31ms/张可接受）。
4. 若暂不想动 C#：插件侧直接调用 Python 侧车
   `python tests/animface_test.py --image <path> --json <tmp>` → 读 JSON（约 0.6–1s 进程启动开销，适合低频巡检，不适合 infer 流）。

### 19.5 局限与后续
- 动漫脸检测**不区分角色**：只给框和置信度；角色身份仍靠 `char_id`（CLIP 嵌入）或后续上 `deepghs/anime_character_detection` 模型（同镜像可下）。
- 极小脸（<20px）、背脸、被 UI 遮挡时漏检；ONNX 阈值 0.25 偏召回，若误检多可提到 0.278~0.35。
- 模型输入动态，超大分辨率截图内部会缩到 640，极端小脸需先裁剪关注区域再推理（与 `interpret_*` 的变化区域天然契合）。
- 未做 GPU/DirectML 实测；CPU 31ms 已满足 700ms~1s 的解读节奏。



### 19.1 集成落地（lead，2026-10-23）
- 新增 `helper/AnimeFaceDetect.cs`（YOLOv8n face，letterbox 640 → 输出转置 → conf≥0.25 → NMS 0.45），走统一 `Providers`（TensorRT/CUDA > DirectML > CPU）
- helper 命令：`animeface {path?, conf?, maxFaces?}`（缺省=当前屏幕）
- 插件工具：`saperm_animeface`（L0）
- 实测（DirectML）：`glance-1792733333431-3.jpg` → face (630,677,105,105) conf 0.793；`mid.jpg` → (682,404,74,72) 0.791；热路径 65ms、冷启动 ~1.1s。与 Python 结果一致（629,676,105,105）
- 待重启 DSH 后插件侧生效


---

## 20. 逐帧综合理解（fusion understanding，2026-10-25）

**目标**：把 scene/objects/animeface/OCR 融合成一句话「画面理解」，随 infer_status 输出。

### 实现
- `infer_start` 新增 `animeface:true`（YOLOv8n face 逐帧，~28-37ms）
- infer 每帧产出 `understanding` 字段：
  ```
  视觉小说界面；物体:人×1(harness女仆3)；动漫脸×1；文字「deepseek | HARNESS | 控制中心 | 参数： | web | …」
  ```
- 组成：场景标签 → 物体（含注册身份）→ 动漫脸数 → 文本摘要（≤40 字）
- `infer_status.latest` 新增 `faces/faceCount/faceMs/understanding`；history 每帧记录 `faceCount/understanding`

### 实测（1920×1080，DirectML）
| 模态 | 耗时 |
|---|---|
| OCR（流式，P50/P90） | 100 / 112 ms |
| YOLOv8m 物体 | 33 ms |
| 动漫脸 | 28 ms |
| CLIP-L 场景 | 28 ms |
| **全模态稳态** | **4.6 fps**（主循环 ~190ms/帧，FrameHub 120 帧/12s，处理 35） |

### 后续可优化
- 自适应频率：场景/脸每 N 帧一次（场景变化慢），物体每帧
- 快速模式：YOLOv8n 替代 m（−30ms）
- 目标 6-8fps（当前 4.6）


---

## 21. 自适应采样频率（2026-10-25）

**问题**：全模态每帧都跑（OCR+物体+脸+场景）≈190ms/帧 → 4.6fps。

**方案**：OCR/物体每帧；场景/动漫脸按帧间隔采样（变化慢，缓存复用）。
- `infer_start` 新增 `sceneEvery`（默认 8）、`faceEvery`（默认 4）
- 采样帧才更新 latest；understanding 用缓存值组合
- 实测（1920×1080，DirectML，全部模态）：**4.6 → 6.7 fps**，OCR P50 70ms，processed 62 / 14s
- 调参建议：追求流畅用 sceneEvery=12/faceEvery=6（≈7.5fps）；追求准确用 4/2（≈5.5fps）

### 命令
```json
{"cmd":"infer_start","objects":true,"scene":true,"accurate":true,"animeface":true,"sceneEvery":8,"faceEvery":4}
```


---

## 22. 未知角色预案（2026-10-25）

**目标**：未来出现未注册角色时，系统不丢弃，而是自动登记为"未知角色#N"，可事后命名。

### 机制
- 匹配顺序：先查具名画廊（阈值 0.70）；未命中则查未知聚类（相似 ≥0.85 归入已有，否则新建"未知角色#N"）
- 未知聚类存储：`helper/models/characters-unknown.json`（refs ≤5、出现次数、首末次时间、最像具名分数）
- 输出：`detect`/`char_match` 人物框带 `unknownId`+`unknownScore`；infer 追踪身份也识别未知
- 事件：变化日志为"未知角色#N#id 出现/消失"，注入消息附提示"（未登记，可用 saperm_char_promote 命名）"
- 管理命令：`char_unknowns`（列出）、`char_promote {unknown,name}`（提升为具名）、`char_ignore {id}`（忽略误检）
- 插件工具：`saperm_char_unknowns` / `saperm_char_promote` / `saperm_char_ignore`

### 实测（2026-10-25）
```
char_match {x:300,y:200,w:300,h:300} → {name:null, unknownId:"未知角色#1", score:1}
char_unknowns → [{"id":"未知角色#1","refs":2,"count":2,"bestNamedScore":0.613}]
char_promote {unknown:"未知角色#1", name:"测试角色B"} → ok（refs 2 转入具名）
char_delete {name:"测试角色B"} → 清理完成
最终注册表：DeepSeek娘(refs=3) / 白毛终端少女(refs=1)；unknowns 空
```

### 阈值/调参
- `namedMin` 0.70：低于即视为未知
- `unknownMin` 0.85：未知聚类复用阈值（越低越易合并，越高越易分裂）
- 已知边界：并发多 helper 会整表覆盖（注册表写入非原子），建议通过插件单实例操作


### 22.1 角色知识可写 + 原子写（2026-10-25 续）
- 新命令 `char_note {name, note}`：设置/更新角色知识备注（识别时随 `note` 返回）
- `char_register` 支持可选 `note` 参数（注册即写知识）
- 插件工具：`saperm_char_note`；`saperm_char_register` 增加 `note`
- 注册表/未知表/知识文件改为**原子写**（temp + File.Replace），避免并发写损坏与 .tmp 残留
- 回归测试 `tests/char-multi.mjs` 适配改名（DeepSeek娘）与合并画廊：**26/26 通过**

实测：
```
char_note {name:"DeepSeek娘", note:"蓝发蓝眼女仆，DeepSeek 蓝鲸拟人（鲸鱼娘）；DSH 界面助手形象"} → ok:true
char_note {name:"不存在", ...} → ok:false
char_match → {name:"DeepSeek娘", score:0.867, note:"蓝发蓝眼女仆，…"}
注册表：DeepSeek娘(refs=3) / 白毛终端少女(refs=1)；无 .tmp 残留
```


---

## 23. 投产就绪度评估（2026-10-25）

### 发现的两个阻碍

**P0：helper 生命周期缺陷（已修复，待重启验证）**
- 现象：插件 refs 归零后 1s 内 shutdown helper → 长时观察（infer/interpret/watch）被打断
- 实测：`infer_start` 后 12s，`processed=0, fps=0`（helper 已退出）
- 修复：插件新增 hold 机制（`observationHolds`），infer/interpret/watch 启动时 holdHelper()，停止时 releaseHold()；helper 崩溃时清空 hold
- 生效条件：**重启 DSH**（插件不热更）

**P1：infer + interpret 并发不可用**
- 实测（20s 同跑）：`fps=0.6, processed=7, dropped=127, OCR P50=81ms / P90=5977ms`，understanding 空
- 原因：两引擎各自 OCR + 模型会话在 DirectML 上争用；helper RSS ~2.5GB
- 策略：**单模式运行**。默认 interpret（低耗监控 ~800ms/次），需要逐帧分析时再切 infer；不要同时开

### 已就绪
- 识别链路：OCR / YOLOv8m / CLIP-L 场景 / 动漫脸 / 人物身份 / 未知角色聚类
- 权限门：L0 观察、L2 会话授权、L3 逐次审批、急停
- 测试：char-multi 26/26、功能验收 12/12
- 注册表原子写、角色知识库、未知角色管理

### 资源
| 项 | 数值 |
|---|---|
| helper RSS（全模型） | ~2.5GB |
| 模型磁盘 | ~1.6GB（CLIP-L 1.2GB + YOLO + OCR + face） |
| 冷启动 | CLIP-L ~2.5s / 动漫脸 ~0.6-1.1s |
| 单模式 infer | 6.7-6.9 fps（修复后） |

### 投产步骤
1. 重启 DSH（加载 hold 修复与 saperm_char_note）
2. 自检：infer_start 12s → processed>0；interpret_start 10s → count>0
3. 使用策略：单模式；监控用 interpret，分析用 infer
4. 并发多 helper 会覆盖注册表 → 只走插件单实例

### 23.1 迭代修复（2026-10-25 续）
1. **helper 空闲保活**：refs 归零后不再立即 shutdown，改为空闲 `SA_HELPER_IDLE_MS`（默认 60s）后关闭；新调用取消空闲计时 → 连续调用不再每次付冷启动（CLIP-L ~2.5s）
2. **infer/interpret 互斥**：启动一个时若另一个在跑，自动先停另一个并返回 `autoStopped`（避免并发降级 fps 0.6/OCR P90 6s）
3. **投产自检** `saperm_health`：helper 在线/pid、模型文件（OCR/YOLO/face/CLIP）、注册表角色数、当前观察模式、holds、急停状态
4. helper 新增 `health` 命令（返回 models/clip/faceModel/characters/unknowns）

实测 health：
```json
{"ok":true,"models":{"ch_PP-OCRv4_det_infer":true,"ch_PP-OCRv4_rec_infer":true,"yolov8m":true,"animeface":true},"clip":"chinese-clip-large","faceModel":true,"characters":2,"unknowns":0}
```

**冒烟验证（mock ctx + 真实 helper，tests/plugin-smoke.mjs / plugin-holds.mjs）**
```
tools registered: 59
health: ok=true, helper online pid=39688, observation={mode:null,holds:0}, clip=chinese-clip-large, characters=2
helper alive after 3s idle: 2        ← 空闲保活生效（旧逻辑 1s 即退出）
infer_start → running=true
interpret_start → running=true, autoStopped="infer"   ← 互斥生效
while infer: {mode:"infer", holds:1} → after stop: {mode:null, holds:0}  ← hold 生命周期正确
```


---

## 24. 投产深度加固（2026-10-26）

1. **DirectML 线程安全串行化（`Providers.Gate`）**
   - 根因：`Infer.Loop` 后台线程、`ClipScene.WarmupAsync` 预热线程与主线程命令同时调用 DirectML `CreateSession` / `sess.Run` 时会触发 `DirectML.dll` 原生访问冲突（`Fatal error`）。
   - 修复：在 `Providers.CreateSession` 及 `PaddleOcr` / `YoloDetect` / `AnimeFaceDetect` / `ClipScene` 的 `Run` 入口统一加 `lock (Providers.Gate)`，同时启用 `ORT_ENABLE_BASIC` 与 `ORT_LOGGING_LEVEL_ERROR`，彻底消除并发崩溃与控制台告警刷屏。
2. **看门狗崩溃告警与观察状态自愈（B13 & C14）**
   - 幂等持有 `holdHelper(key)`（按 `'obs'` / `'watch'` 去重），修复重复 `start` 导致的 `refs` 泄漏。
   - `SaHelper.exe` 意外退出时自动写入 `helper_crash` 审计日志、向当前会话注入告警通知，并在重连 `ready` 后自动恢复 `infer` / `interpret` / `watch` 与急停状态（记录 `helper_recover`）。
3. **运动自适应首帧完备性与唤醒验证（B6）**
   - 修复首帧或场景未就绪时因取模跳过导致的 `scene: null` 问题；新增 `staticSkipped` 计数并保持静止心跳期间 `fps` 实时刷新。
4. **角色画廊防漂移自动学习与未知聚类治理（C4 / C6 / C15）**
   - 已知角色匹配度在 `[0.86, 0.965)` 且与锚点参考 `>= 0.78` 时自动扩充画廊（上限 8）。
   - 未知聚类新增参考需多样性 `< 0.96`、落盘 5s 节流、容量上限 50 自动淘汰单次噪声，并支持 `saperm_char_ignore({ id: "stale" | "*" })`。
   - 修复 `helperCall` 中 `{ id, cmd, ...args }` 被 `args.id` 覆盖导致 `saperm_char_ignore` 超时的协议缺陷。
   - 注册表/未知库/备注库支持基于文件 `mtime` 自动热重载，`char_rename` / `char_delete` 自动同步备注。
5. **投产回归套件 `tests/prod-readiness.mjs`（16/16 全绿）+ `tests/char-multi.mjs`（26/26 全绿）**。


---

## 25. AI 可读数据流 + 硬件效率（2026-10-31）

### 目标
把「图片流」实时压成 AI 能直接读的**紧凑文本行**，落盘为日志，并提供低成本读取接口；
同时让"屏幕没变"时几乎不烧 CPU/GPU。

### 数据流（stream）
- 每帧识别结果压成一行（实测 **111~117 字节/行**，对比 `saperm_infer_status` 全量 JSON **2574 字节/帧** → 约 **22× 压缩**）：
  ```
  19:14:29 #151 W=SA 注意力 SC=视觉小说界面 OB=人(DeepSeek娘)×2 F=2 TX== CH=窗口切换 -> ... 1.9fps 277ms
  ```
  - `W=` 前台窗口 / `SC=` 场景 / `OB=` 物体(含角色身份) / `F=` 动漫脸 / `CH=` 变化事件
  - `TX=` 文本摘要，**增量语义**：`=`(与上帧相同) / `+…`(尾部新增) / 全文(其它情况)
  - **CH 去重**：与上一行相同的变化事件不再重复携带
- 落盘：`out/logs/stream-YYYY-MM-DD.jsonl`（逐行 JSON）+ `out/logs/stream-latest.txt`（滚动最近 200 行，AI 可直接 read）
- 工具：`saperm_stream {limit,sinceSeq,format:lines|json,file}`、`saperm_stream_start/stop`
  - `sinceSeq` 支持增量拉取；`file:true` 返回 latest.txt 路径

### 硬件效率（实测同一 worker 单核百分比）
| 项 | 优化前 | 优化后 |
|---|---|---|
| 静止 CPU | 21.7% | **10.0%** |
| 活跃 CPU（全模型） | ~89% | 95.8%（把算力花在真有变化时） |
| 静止日志量 | 每帧一行 | 21 次采样仅落 3~4 行（`idleSkips` 记录节流数） |

实现要点：
1. **心跳零推理**：静止帧不再跑 YOLO/CLIP/人脸，仅每 6 次心跳（≈6s）刷新一次小预算 OCR（`staticHeartbeats` 计数）。
2. **采集自适应**：静止 ≥3s 时 FrameHub 采集从 100ms 降到 220ms；一旦有变化立刻回到 100ms；watch/小窗打开时恒为 100ms（保流畅）。
3. **worker 单实例互斥**：`Local\\SaHelperWorker_S{sessionId}` Mutex，重复 bridge 拉起的 worker 立即退出，杜绝多实例各自加载 ONNX 叠加 CPU/内存（曾实测出现 2 个 worker、各自 ~0.9/1.3GB）。
4. **流静止节流**：内容实质未变时每 5s 才落一行心跳。


### 25.1 闭环自测（操作 → 识别，2026-10-31）

全流程仅用插件自身能力完成（无外部 shell 干预）：

| 步骤 | 手段 | 识别侧记录 |
|---|---|---|
| 打开运行框 | `saperm_hotkey(["WIN","R"])` | `CH=窗口切换 -> 运行` |
| 输入并执行 | `saperm_type("notepad")` + `ENTER` | `CH=窗口切换 -> 记事本` → `-> 无标题 - Notepad` |
| 聚焦并输入 | `saperm_click(600,520)` + `saperm_type(中文)` | OCR 回读 **`屏幕闭环自测中文验证`**（一字不差） |
| 关闭 | `saperm_hotkey(["ALT","F4"])` | 窗口消失 |

两个真实缺陷被发现并修复：
1. **组合键无效**：`Hotkey()` 原先一次性发送所有 INPUT（同一时间戳），Notepad 等应用不响应 Alt+F4。改为**分段发送 + 28/38/22ms 帧间延迟**后生效。
2. **OCR 等宽小字退化**：记事本极窄等宽字体下 `DSH 2026 AB12` 被识别成 `2222222`（scale=1/2/3 均无效，非分辨率问题）；中文与常规 UI 文本识别正常。这是 PaddleOCR rec 在窄字形上的已知局限。
