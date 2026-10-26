# dsh-screen-attention 项目复盘（2026-10-25）

## 1. 项目全貌

    插件 permission-plugin/lib/index.js（59 工具）
      权限门: L0 观察 / L2 会话授权(点击/输入/滚动/Excel) / L3 逐次审批(拖拽/双击) / 急停
      会话能力: watch/observe/interpret/infer + overlay 注意力小窗 + 事件注入(inject)
      角色识别: char_register/match/list/rename/delete/merge/unknowns/promote/ignore/note + animeface
    ─── stdio JSON 行协议 ───
    SaHelper.exe（C# net9.0，7 文件 3356 行）
      采集: FrameHub 单流 100ms（GDI BitBlt ~17.8ms）
      OCR:  PaddleOCR PP-OCRv4 det+rec（流式每帧，rec 缓存按框签名）
      物体: YOLOv8n / m（ONNX）      场景: Chinese-CLIP ViT-L/14（1.16GB，768 维）
      脸:   YOLOv8n-face 单类（12MB）  身份: CLIP 裁剪嵌入 + 画廊 + 未知聚类
      推理: Providers 统一 EP 选择 TensorRT > CUDA > DirectML > CPU
      UI:   FlaUI UIA3 + SendInput（点击/拖拽/输入/热键/剪贴板/窗口）

- 代码：helper 3356 行（7 文件）、插件 59 个工具、文档 17+ 节、测试脚本 85 个（多为一次性验证）
- 模型：约 3.95GB（CLIP-L 1.16GB ONNX + 1.55GB pytorch、YOLOv8m 99MB+50MB、OCR 15MB、face 12MB 等）
- 角色注册表：helper/models/characters.json（画廊）、characters-unknown.json、characters-notes.json

## 2. 演进时间线（阶段）

| 阶段 | 内容 | 结果 |
|---|---|---|
| P0 权限与观察 | saperm_* 权限门、watch/observe、overlay、UIA | 可用 |
| P1 连续解读 | FrameHub 单流、interpret（区域 OCR + 变化分类） | 800ms/次 |
| P2 逐帧识别 | infer（每帧 OCR + 物体 + 场景 + 脸） | 单模式 6.7-6.9fps |
| P3 角色认知 | CLIP 裁剪嵌入 + 画廊合并 + 未知聚类 + 知识备注 | 26/26 回归 |
| P4 事件注入 | interpret/infer → 会话注入（节流/去重/停止清理） | 冒烟通过 |
| P5 ACG 适配 | Chinese-CLIP ViT-L、YOLOv8n-face、场景标签 | 28-32ms/模型 |
| P6 投产治理 | hold 保活、空闲保活、互斥、health、原子写 | 待重启验证 |

## 3. 关键决策与理由

| 决策 | 理由 |
|---|---|
| 本地模型（PaddleOCR/YOLO/CLIP）而非云端视觉 API | 用户否决云端方案（延迟 2.4-4.3s、成本） |
| 统一 Providers（TensorRT>CUDA>DirectML>CPU） | 有显卡自动用显存，无卡回退 DirectML |
| FrameHub 单流 + 引用计数 | 多引擎共享采集，避免重复 BitBlt |
| 流式 OCR（rec 缓存按框 FNV 签名） | 每帧 OCR 从 ~300ms 降到 ~70-110ms |
| CLIP 画廊（一个角色多参考）+ 未知聚类 | 同一角色多姿势无需重复命名；未见角色自动登记 |
| 自适应采样（场景 8 帧/脸 4 帧） | 4.6fps → 6.7-6.9fps |
| 注入而非唤醒（inject 不打断对话） | 长任务不中断用户 |
| 插件 hold + 空闲保活 | 长时观察不被打断；避免每次调用 2.5s 冷启动 |

## 4. 踩坑与修复（可复用）

| # | 问题 | 根因 | 修复 |
|---|---|---|---|
| 1 | SendInput err=87 | INPUT 结构体大小/union 不对 | 显式 40B union |
| 2 | 拖拽/双击无效 | 未拆 MouseDown/Up | 分开调用 |
| 3 | UIA 前台判断错误 | USER_ACTIVE 误判 | wait-idle 轮询 |
| 4 | SetForegroundWindow 失败 | 系统前台锁 | Alt 键解锁 + SwitchToThisWindow |
| 5 | Excel 单格 Ctrl+C 只复制一格 | 复制范围未指定 | 按范围回读验证 |
| 6 | transformers 需 torch≥2.6 | 版本不匹配 | 锁 transformers 4.40 + torch 2.2 |
| 7 | numpy 2.x 破坏 torch 2.2 | ABI 不兼容 | 锁 numpy 1.26.4；opencv 4.10 |
| 8 | HF/GitHub 不可达 | 网络封锁 | hf-mirror/ModelScope 下载 API/代理 |
| 9 | YOLOv8m 静默回退 n | 补丁上下文不匹配未生效 | 逐字匹配重打 + 实测置信度 |
| 10 | C# 未赋值局部变量 | 短路求值 out 变量 | 拆开逐个 TryGetProperty |
| 11 | 长时观察 1s 后中断（P0） | helper refs 归零即 shutdown | 插件 hold 机制 |
| 12 | infer+interpret 并发降级（P1） | 模型/DirectML 争用 | 互斥自动切换 |
| 13 | 注册表并发覆盖 | 整表非原子写 | temp+File.Replace 原子写 |
| 14 | 字符串替换只命中第一处 | JS String.replace 语义 | 定位唯一上下文/二次替换 |
| 15 | 生成脚本路径被转义破坏 | 模板字符串反斜杠 | 统一正斜杠 |
| 16 | 同角色多注册抢 top-1 | 画廊重复参考 | char_merge 合并 |
| 17 | 测试污染注册表 | 夹具未清理 | 临时名 + 结束清理 + 断言容差 |

## 5. 性能基线（1920×1080，DirectML）

| 项 | 数值 |
|---|---|
| GDI 采集 | ~17.8ms/帧 |
| OCR 流式 | P50 70-112ms / P90 112ms |
| YOLOv8n / m | 22-41ms / 55-100ms |
| 动漫脸 | 28-37ms（冷启 0.6-1.1s） |
| CLIP-L 场景 | 28-32ms（冷启 ~2.5s） |
| infer 单模式（scene8/face4） | 6.7-6.9 fps |
| infer+interpret 并发 | 0.6 fps（不可用，已互斥） |
| helper RSS | ~2.5GB（全模型） |

## 6. 经验教训

1. 先验证再集成：模型先用 Python 验证输出，再写 C# 并逐项对比（动漫脸 C# 与 Python 同图框一致）。
2. 统一推理入口：所有 ONNX 走同一 Providers，避免每个模型各写一套 EP 逻辑。
3. 长任务必须有生命周期设计：hold/pin、空闲保活、崩溃清理，否则"能跑一次"≠"能持续跑"。
4. 资源争用要串行化：多引擎同开必须互斥，指标会断崖（4.6→0.6fps）。
5. 观察是异步的：状态接口要含 running/fps/error/p50/p90，便于判断"真在跑"还是"假活着"。
6. 数据文件原子写：JSON 注册表并发写会丢数据，temp+replace 成本极低。
7. 测试要自净：夹具注册/删除成对，断言留容差（同角色多参考会抢 top-1）。
8. 插件改动不热更：改完必须重启，验证脚本用 mock ctx 与运行环境解耦。
9. 诚实报告：并发降级、冷启动、内存占用都要给数字，不用"应该可以"糊弄。
10. 知识要多源交叉：萌娘百科/知乎被墙时，用 DSH Hub 插件描述 + 视觉特征 + 标题交叉确认。

## 7. 当前状态与风险

已就绪：识别链路完整、权限门、回归 26/26、插件冒烟（59 工具/health/互斥/hold）
待验证：P0 hold 修复需重启 DSH 后真机确认（冒烟已验证代码逻辑）
风险：
- P1 并发（已用互斥规避，但切换有 1-3s 空窗）
- 注册表多实例覆盖（原子写仍非事务）
- 无安装包/README（需手工配置模型路径）
- 冷启动 CLIP-L ~2.5s（空闲保活缓解）

## 8. 后续建议（按优先级）

1. README/部署文档（模型下载、目录、重启、自检 saperm_health）
2. 观察历史落盘（回放/诊断，JSONL 按天）
3. 自适应频率（按屏幕运动率动态调 sceneEvery/faceEvery）
4. 自动学习画廊（高置信匹配补充参考，设上限防漂移）
5. 多角色批量导入（从文件夹截图批量注册 + 自动聚类）
6. 打包分发（npm 插件 + 模型可选下载脚本）

## 9. 投产实战新增经验（2026-10-26~27）

11. **经验 11（SYSTEM 权限与 Session 0 隔离穿透）**：宿主以 SYSTEM 运行在 Session 0 且位于单会话 Job Object 内时，使用 `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` 借用 Session 1 宿主进程跨会话拉起 Worker 并通过本地回环流桥接 stdio，直接命中真实桌面与硬件 GPU DirectML（内存从 2.5GB 降至 770MB）。
12. **经验 12（高分屏 Per-Monitor DPI V2 物理分辨率对齐）**：2560x1600 (150% 缩放) 下须开启 `SetProcessDpiAwarenessContext(-4)` 并结合 `GetDeviceCaps(118/117)` 获取真实物理分辨率，避免 `BitBlt` 仅截取左上角 1707x1067。
13. **经验 13（DirectML 全局串行化与 Lossless JSON 防护）**：所有 ONNX 会话创建与 `Run` 统一加 `lock (Providers.Gate)` 杜绝 D3D12 并发崩溃；插件注册层统一做 Lossless JSON 清洗，RPC 协议确保 `id/cmd` 不被业务参数覆盖。
14. **经验 14（对标 DSH 原生工具的用完即净生命周期）**：停止观察自动联动关闭悬浮小窗，且支持 `ttlSec` 无操作超时自动回收（自动停 infer/interpret/watch + 关 overlay + 释放 hold），杜绝任务结束后残留窗口或后台空转。
