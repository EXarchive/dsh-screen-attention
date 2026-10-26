# 未完成事项清单（2026-10-25）

状态：✅完成 / 🟡部分 / 🔴未开始；优先级 P0 最高。

## A. 投产阻塞项（需重启 DSH 验证）

| # | 事项 | 状态 | 说明 | 验收 |
|---|---|---|---|---|
| A1 | P0 hold 修复真机验证 | ✅ 完成 | 插件 observationHolds 机制，实测 infer_start 12s holds=1 持续处理 | 实测通过 |
| A2 | P1 infer/interpret 互斥真机验证 | ✅ 完成 | 互斥自动切换，实测 interpret_start 自动返回 autoStopped:"infer" | 实测通过 |
| A3 | 空闲保活真机验证 | ✅ 完成 | SA_HELPER_IDLE_MS 默认 60s，连续调用无需重复经历 2.5s 冷启动 | 实测通过 |
| A4 | saperm_health 工具加载 | ✅ 完成 | 重启后已成功加载，提供端到端状态自检 | 实测通过 |
| A5 | README/部署说明 | ✅ 完成 | 项目根目录编写全量 README.md，包含架构/命令/策略/资源 | 文档齐全 |

## B. 功能缺口（BLUEPRINT 规划未实现）

| # | 事项 | 状态 | 缺口 | 建议 |
|---|---|---|---|---|
| B1 | GPU 采集 WGC/DXGI + 脏矩形（M2 #6/#7） | 🔴 | 仍用 GDI；脏矩形算法未设计 | Vortice.Windows 托管绑定 + 回退 GDI |
| B2 | GPU OCR 全链路（#5） | 🟡 | 推理走 DirectML，但 PaddleOCR 预处理/回退未专项验证 | 先跑通现有 DirectML 全链路基准 |
| B3 | 变化摘要生成算法（#8） | ✅ 完成 | 融合前台窗口标题 + 场景 + 物体(含角色身份) + 动漫脸 + 文本摘要 | tests/prod-readiness.mjs 验证通过 |
| B4 | "重要变化"语义阈值（#9） | ✅ 完成 | 支持前台窗口切换检测(窗口切换 -> ...) + 角色出现/消失 + 文本增量 | 已集成至 Program.cs & index.js |
| B5 | 注意力四路合并仲裁（#10） | 🔴 | AI 指定/用户/脏矩形/默认焦点优先级未实现 | AI 显式 > 用户 > 脏矩形 > 默认 |
| B6 | 频率调度器（#11） | ✅ 完成 | 静止画面自适应跳过推理降频（10 帧一次心跳），运动即刻恢复 | 已集成至 Program.cs |
| B7 | turn-stopping 续命策略（#12） | 🔴 | 未实现 | 观察模式+重要变化才续命，单轮≤3 |
| B8 | 看图触发规则（#13） | ✅ 完成 | pruneViewDir 自动保留最新 30 张截图/30min 生命周期（保护夹具） | tests/prod-readiness.mjs 验证通过 |
| B9 | 大图传输通道（#14） | 🟡 | 已用文件路径通道 | webServer 静态路由可选 |
| B10 | 后台输入模式（#15） | 🔴 | 仅前台 SendInput | PostMessage 按需 |
| B11 | 点击验证回看判定（#16） | ✅ 完成 | click 自动抓点击前后 96×64 局部块计算 diffRatio 与 verified | 已集成至 Program.cs |
| B12 | 多会话屏幕互斥仲裁（#17） | 🔴 | 未实现 | 先占先得 + 提示 |
| B13 | 看门狗重启状态恢复（#18） | ✅ 完成 | helper 异常退出后看门狗 800ms 自动拉起并恢复 infer/interpret/watch 及急停态 | tests/prod-readiness.mjs 验证通过 |
| B14 | 隐私黑名单拦截（#19） | ✅ 完成 | Gdi.Capture 自动涂黑黑名单窗口矩形(1Password/KeePass/SA_PRIVACY_TITLES) | tests/prod-readiness.mjs 验证通过 |
| B15 | 工具命名 schema 定稿（#20） | 🟡 | 实现为 saperm_*，BLUEPRINT 写 screen_attention_* | 统一文档命名 |

## C. 本会话新增的未完成项

| # | 事项 | 状态 | 说明 |
|---|---|---|---|
| C1 | ACG 角色专属识别模型 | 🔴 | deepghs/anime_character_detection 可下（同镜像），未接 |
| C2 | 动漫脸属性（关键点/表情/发色） | 🔴 | 当前只给框+置信度 |
| C3 | 观察历史落盘/回放 | ✅ 完成 | appendObservationLog 持久化至 out/logs/，新增 saperm_history 工具查询 | 已集成 |
| C4 | 画廊自动学习（防漂移） | ✅ 完成 | 已知角色高置信(0.86~0.965)且与首张锚点>=0.78时自动补充多样参考（上限 8） | 已集成至 CharId.cs |
| C5 | 多角色批量导入 | 🔴 | 需逐张 register |
| C6 | 未知聚类治理 | ✅ 完成 | 多样性过滤(<0.96)、落盘节流(5s)、容量上限(50)淘汰及 char_ignore("stale"/"*") 清理 | tests/prod-readiness.mjs 验证通过 |
| C7 | 角色知识自动获取 | 🟡 | 仅手工 char_note |
| C8 | OCR 日文支持 | 🔴 | 仅中英（PP-OCRv4 ch） |
| C9 | 多显示器/DPI | 🔴 | 仅主屏 1920×1080 实测 |
| C10 | 锁屏/安全桌面处理 | ✅ 完成 | SessionBridge 穿透 Session 0->1 + Gdi.LastSecureDesktop 检测容错 | tests/prod-readiness.mjs 验证通过 |
| C11 | 模型清理 | 🟡 | pytorch_model.bin 718MB+1.55GB 运行时不需要 |
| C12 | 打包分发 | 🔴 | 无 npm 包/模型下载脚本 |
| C13 | 插件日志落盘/轮转 | ✅ 完成 | out/logs/observation-YYYY-MM-DD.jsonl 7天自动轮转与 2MB 截断 | tests/prod-readiness.mjs 验证通过 |
| C14 | helper 崩溃通知 | ✅ 完成 | 异常退出时自动注入会话告警并落盘 helper_crash / helper_recover 日志 | tests/prod-readiness.mjs 验证通过 |
| C15 | 注册表多实例事务 | ✅ 完成 | 基于文件 mtime 自动感知外部修改重载 + PID/TID 隔离原子写 + rename/delete 同步备注 | tests/prod-readiness.mjs 验证通过 |

## D. 工程/质量

| # | 事项 | 状态 | 说明 |
|---|---|---|---|
| D1 | 统一回归套件 | ✅ 完成 | 新增 tests/prod-readiness.mjs (16/16) + tests/char-multi.mjs (26/26) 双套件全绿 | 42/42 全通过 |
| D2 | CI | 🔴 | 无 |
| D3 | 版本号/CHANGELOG | ✅ 完成 | 升级至 v1.0.0 并编写根目录 CHANGELOG.md | 已完成 |
| D4 | 错误码/诊断 | ✅ 完成 | saperm_health 含 sessionId/wsMB/providers/privacy/storage/watchdog | tests/prod-readiness.mjs 验证通过 |
| D5 | 文档与实现同步 | 🟡 | BLUEPRINT 命名/状态滞后 |

## E. 本机无法验证（需目标环境）

| # | 事项 | 说明 |
|---|---|---|
| E1 | TensorRT/CUDA 路径 | 本机无独显，仅 DirectML 验证 |
| E2 | 高分辨率/多屏 | 仅 1920×1080 |
| E3 | 长时稳定性 | 未跑 >30min 浸泡 |
| E4 | 真实 GPU 显存占用 | 无法测 |

## 建议顺序
1. 重启 DSH → 验证 A1-A4 → 补 A5 README
2. D1 统一回归 + C11 模型清理（低风险高收益）
3. C3 历史落盘 + C6 未知治理（可观测性）
4. B6 动态频率 + B3 摘要模板（性能/理解质量）
5. B1 GPU 采集 + C1 ACG 角色模型（进阶）
