# 重启 DSH 以加载新插件代码

插件 JS 改动**不会**随热重载进入运行中的进程（实测：schema/字段/新 action 都不更新），
必须重启一次；C# helper 不用重启——部署后插件下次调用会拉起新 exe。

## 步骤

1. 关掉 DSH：任务管理器结束所有 `node.exe`，或命令行

   ```
   taskkill /IM node.exe /F
   ```

2. 等 4 秒（端口 3080 释放）

3. 双击 `self-iterate\run-dsh.bat`（或重开 DshLauncher，它自己会重试拉起）

## 重启后自检（两条，各自都能判别新旧）

| 调用 | 新代码的特征 | 旧代码的表现 |
|---|---|---|
| `saperm_status {}` | `helper` 里有 `refs`、`holds` 字段 | 只有 `online`/`pid` |
| `saperm_overlay {"action":"status"}` | 返回 `{open, note, subtitle, frame}` | 报错 `action 必须是: open \| close \| set` |

两条都对，说明已加载当前版本；再跑 `node tests/run-all.mjs` 做一次全量回归。

## 为什么不能在会话里重启

DSH 会话里的命令跑在它自己的 Job Object 里，`taskkill /IM node.exe` 会把执行器（包括正在跑的那条命令）一起带走，
表现为 `Windows Job runner exited with exit code 4294967295`。所以重启动作必须由**你自己在会话外**执行。
