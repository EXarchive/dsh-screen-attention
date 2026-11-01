// dsh-screen-attention-perm v0.2
// 权限模块：L2/L3 均会话级一次性授权（L3 独立于 L2）/ 急停锁 / 恢复需用户确认。
// 门控全部走 DSH 原生审批链（tools/pre-execute ask）；注入执行接 SaHelper 进程。
// 审计对 approval/asked + approval/decided 由框架自动落会话日志。

import { spawn } from 'node:child_process'
import { existsSync, writeFileSync, mkdirSync, readdirSync, readFileSync, unlinkSync, appendFileSync, statSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

export const name = 'dsh-screen-attention-perm'
export const inject = ['tools']

const PACKAGE_ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const HELPER_EXE =
  process.env.SA_HELPER_EXE ||
  join(PACKAGE_ROOT, '..', 'helper', 'bin', 'Release', 'net9.0-windows10.0.19041.0', 'SaHelper.exe')

const L2_TOOLS = ['saperm_click', 'saperm_scroll', 'saperm_type', 'saperm_hotkey', 'saperm_clipboard', 'saperm_focus', 'saperm_excel_goto', 'saperm_excel_write', 'saperm_excel_read', 'saperm_fill_write']
const L3_TOOLS = ['saperm_drag', 'saperm_doubleclick']
const INPUT_TOOLS = [...L2_TOOLS, ...L3_TOOLS]

// AI 视角截图交付目录（工作区文件 + 路径返回，图片不进对话，按需用视觉工具查看）
const VIEW_DIR = join(PACKAGE_ROOT, '..', 'out', 'sa-view')
const LOGS_DIR = join(PACKAGE_ROOT, '..', 'out', 'logs')
const MAX_VIEW_FILES = 30
const MAX_VIEW_AGE_MS = 30 * 60 * 1000
const MAX_LOG_DAYS = 7
const MAX_LOG_BYTES = 2 * 1024 * 1024
let lastLogPruneAt = 0

export function pruneLogsDir(now = Date.now()) {
  try {
    if (!existsSync(LOGS_DIR)) return 0
    let removed = 0
    const cutoff = now - MAX_LOG_DAYS * 86400 * 1000
    for (const f of readdirSync(LOGS_DIR)) {
      if (!f.endsWith('.jsonl')) continue
      const full = join(LOGS_DIR, f)
      try {
        const st = statSync(full)
        if (st.mtimeMs < cutoff) {
          unlinkSync(full)
          removed++
        } else if (st.size > MAX_LOG_BYTES) {
          const lines = readFileSync(full, 'utf8').split('\n').filter(Boolean)
          writeFileSync(full, lines.slice(-1500).join('\n') + '\n', 'utf8')
        }
      } catch {}
    }
    return removed
  } catch { return 0 }
}

function appendObservationLog(entry) {
  try {
    if (!existsSync(LOGS_DIR)) mkdirSync(LOGS_DIR, { recursive: true })
    const now = Date.now()
    if (now - lastLogPruneAt > 600000) {
      lastLogPruneAt = now
      pruneLogsDir(now)
    }
    const today = new Date(now).toISOString().slice(0, 10)
    const file = join(LOGS_DIR, `observation-${today}.jsonl`)
    const line = JSON.stringify({ ts: new Date(now).toISOString(), ...entry }) + '\n'
    appendFileSync(file, line, 'utf8')
  } catch {}
}

const PINNED_VIEW_FILES = new Set([
  'glance-1792733333431-3.jpg',
  'glance-1792734281648-5.jpg',
  'glance-1792733014102-2.jpg',
  'task-1788460279425-1.jpg',
  'glance-1792676987464-0.jpg',
  'task-1787945408781-0.jpg',
])

export function pruneViewDir(maxFiles = MAX_VIEW_FILES, maxAgeMs = MAX_VIEW_AGE_MS) {
  try {
    if (!existsSync(VIEW_DIR)) return 0
    const now = Date.now()
    const files = readdirSync(VIEW_DIR)
      .filter((f) => (f.endsWith('.jpg') || f.endsWith('.png')) && !PINNED_VIEW_FILES.has(f))
      .map((f) => {
        const full = join(VIEW_DIR, f)
        try { return { full, mtimeMs: statSync(full).mtimeMs } } catch { return null }
      })
      .filter(Boolean)
      .sort((a, b) => b.mtimeMs - a.mtimeMs)
    let removed = 0
    for (let i = 0; i < files.length; i++) {
      if (i >= maxFiles || (now - files[i].mtimeMs > maxAgeMs && i >= 5)) {
        try { unlinkSync(files[i].full); removed++ } catch {}
      }
    }
    return removed
  } catch { return 0 }
}

let viewSeq = 0
function saveShot(jpegB64, tag) {
  try { mkdirSync(VIEW_DIR, { recursive: true }) } catch {}
  const p = join(VIEW_DIR, tag + '-' + Date.now() + '-' + (viewSeq++) + '.jpg')
  writeFileSync(p, Buffer.from(jpegB64, 'base64'))
  if (viewSeq % 5 === 0) pruneViewDir()
  return p
}

// ============ helper 客户端（进程级单例 + 事件广播 + 看门狗状态恢复） ============
const helperSingleton = {
  proc: null,
  pid: null,
  online: false,
  version: null,
  refs: 0,
  nextId: 1,
  pending: new Map(),
  buf: '',
  listeners: new Set(),
  restarting: false,
  expectedShutdown: false,
  needsRecovery: false,
  recovering: false,
  crashes: 0,
  recoveries: 0,
  lastCrashAt: null,
  lastCrashCode: null,
}

let onHelperExit = null
let onHelperReady = null

const activeHolds = new Set()
let observationHolds = 0

// 幂等持有：按 key（'obs' | 'watch'）去重，重复 start 不会泄漏 refs
function holdHelper(key = 'obs') {
  clearTimeout(helperSingleton.idleTimer)
  if (!activeHolds.has(key)) {
    activeHolds.add(key)
    observationHolds = activeHolds.size
    helperSingleton.refs++
  }
}

function releaseHold(key = 'obs') {
  if (activeHolds.delete(key)) {
    observationHolds = activeHolds.size
    releaseHelper()
  }
}

function clearAllHolds() {
  const n = activeHolds.size
  activeHolds.clear()
  observationHolds = 0
  helperSingleton.refs = Math.max(0, helperSingleton.refs - n)
}

function spawnHelper() {
  if (!existsSync(HELPER_EXE)) {
    console.log(`[sa-perm] helper 未找到：${HELPER_EXE}`)
    return false
  }
  console.log(`[sa-perm] 启动 helper：${HELPER_EXE}`)
  helperSingleton.expectedShutdown = false
  const proc = spawn(HELPER_EXE, [], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  helperSingleton.proc = proc
  helperSingleton.pid = proc.pid

  proc.stdout.on('data', (d) => {
    helperSingleton.buf += d.toString('utf8')
    let i
    while ((i = helperSingleton.buf.indexOf('\n')) >= 0) {
      const line = helperSingleton.buf.slice(0, i).trim()
      helperSingleton.buf = helperSingleton.buf.slice(i + 1)
      if (!line) continue
      let msg
      try { msg = JSON.parse(line) } catch { continue }
      if (msg.event) {
        if (msg.event === 'ready') {
          helperSingleton.online = true
          console.log('[sa-perm] helper ready')
          if (helperSingleton.needsRecovery && onHelperReady) {
            helperSingleton.needsRecovery = false
            Promise.resolve().then(() => onHelperReady()).catch(() => {})
          }
        }
        if (msg.event === 'hotkey') {
          for (const fn of helperSingleton.listeners) {
            try { fn('hotkey', msg.data) } catch {}
          }
        }
        continue
      }
      const p = helperSingleton.pending.get(msg.id)
      if (p) { helperSingleton.pending.delete(msg.id); p.resolve(msg) }
    }
  })
  proc.stderr.on('data', (d) => console.log(`[sa-helper] ${d.toString('utf8').trim()}`))
  proc.on('exit', (code) => {
    const unexpected = !helperSingleton.expectedShutdown
    console.log(`[sa-perm] helper 退出 code=${code} unexpected=${unexpected}`)
    helperSingleton.online = false
    clearAllHolds()
    if (unexpected) {
      helperSingleton.crashes++
      helperSingleton.lastCrashAt = new Date().toISOString()
      helperSingleton.lastCrashCode = code == null ? -1 : code
      helperSingleton.needsRecovery = true
    }
    if (onHelperExit) { try { onHelperExit({ unexpected, code }) } catch {} }
    for (const [, p] of helperSingleton.pending) p.reject(new Error('helper 进程已退出'))
    helperSingleton.pending.clear()
    if (helperSingleton.proc === proc) { helperSingleton.proc = null; helperSingleton.pid = null }
    if (helperSingleton.refs > 0 && !helperSingleton.restarting) {
      helperSingleton.restarting = true
      setTimeout(() => { helperSingleton.restarting = false; if (!helperSingleton.proc) spawnHelper() }, 800)
    }
  })
  return true
}

function acquireHelper() {
  clearTimeout(helperSingleton.idleTimer)
  helperSingleton.refs++
  if (!helperSingleton.proc) spawnHelper()
}

// 按需重连：离线时尝试拉起 helper 并等待 ready（≤4s），成功返回 true
async function ensureHelper() {
  clearTimeout(helperSingleton.idleTimer)
  if (helperSingleton.proc && helperSingleton.online) return true
  helperSingleton.refs = Math.max(1, helperSingleton.refs)
  if (!helperSingleton.proc && !helperSingleton.restarting) spawnHelper()
  const t0 = Date.now()
  while (Date.now() - t0 < 4000) {
    if (helperSingleton.online) return true
    await new Promise((res) => setTimeout(res, 100))
  }
  return helperSingleton.online
}

const HELPER_IDLE_MS = Math.max(5000, Number(process.env.SA_HELPER_IDLE_MS || 60000))

function armIdleShutdown() {
  clearTimeout(helperSingleton.idleTimer)
  helperSingleton.idleTimer = setTimeout(() => {
    if (helperSingleton.refs > 0 || observationHolds > 0) return
    const p = helperSingleton.proc
    if (!p) return
    helperSingleton.expectedShutdown = true
    helperSingleton.proc = null
    helperSingleton.online = false
    try { p.stdin.write(JSON.stringify({ id: 0, cmd: 'shutdown' }) + '\n') } catch {}
    setTimeout(() => { try { p.kill() } catch {} }, 1000)
  }, HELPER_IDLE_MS)
  if (helperSingleton.idleTimer.unref) helperSingleton.idleTimer.unref()
}

function releaseHelper() {
  helperSingleton.refs = Math.max(0, helperSingleton.refs - 1)
  if (helperSingleton.refs === 0 && helperSingleton.proc) armIdleShutdown()
}

function helperCall(cmd, args = {}, timeoutMs = 35000) {
  return ensureHelper().then((ok) => {
    if (!ok) throw new Error('helper 离线（SaHelper.exe 未运行；期望路径 ' + HELPER_EXE + '）')
    return new Promise((resolve, reject) => {
      if (!helperSingleton.proc || !helperSingleton.online) {
        reject(new Error('helper 离线'))
        return
      }
      const id = helperSingleton.nextId++
      const t = setTimeout(() => {
        helperSingleton.pending.delete(id)
        reject(new Error('helper 命令 ' + cmd + ' 超时'))
      }, timeoutMs)
      helperSingleton.pending.set(id, {
        resolve: (m) => {
          clearTimeout(t)
          if (m.ok) resolve(m.data)
          else reject(new Error(friendlyHelperError(m.error)))
        },
        reject: (err) => {
          clearTimeout(t)
          reject(err)
        },
      })
      helperSingleton.proc.stdin.write(JSON.stringify({ ...args, id, cmd }) + '\n')
    })
  })
}

function friendlyHelperError(e) {
  const s = String(e || '')
  if (s.startsWith('USER_ACTIVE')) return '用户正在操作鼠标/键盘，注入自动让位；稍后重试'
  if (s.startsWith('LOCKED')) return '注入已锁定（急停/热键），需用户确认解除'
  return s
}

export function sanitizeJson(v) {
  if (v === undefined || typeof v === 'function' || typeof v === 'symbol') return null
  if (typeof v === 'number') return Number.isFinite(v) ? v : 0
  if (Array.isArray(v)) return v.map(sanitizeJson)
  if (v && typeof v === 'object') {
    const out = {}
    for (const [k, val] of Object.entries(v)) {
      if (val !== undefined) out[k] = sanitizeJson(val)
    }
    return out
  }
  return v
}

// ============ 插件主体 ============
export function apply(ctx) {
  const state = {
    l2: new Map(),
  l3: new Map(),
    pending: new Map(),
    paused: false,
    pausedBy: null,
    link: false,        // change 事件 → 自动注入摘要到会话（对话不中断）
    linkWake: false,    // 需要唤醒时用 steer 而非 inject
    lastLinkAt: 0,
    lastLinkText: '',
    autoWatch: false,   // watch 是否随小窗自动启动（关闭小窗时停）
    overlayOpen: false, // 注意力小窗是否已打开
    obsTtlTimer: null,  // 观察/小窗无操作自动关闭定时器
    obsTtlMs: 0,        // 当前自动关闭 TTL 毫秒数
    linkBudget: { count: 0, windowStart: 0 },  // 联动分钟配额（60s ≤6 次，防刷屏）
    interruptProtect: false,   // 防打断状态：识图/观察任务开启时打开，结束自动关闭（inject 不中断对话）
    observeTask: null,         // 持续观察任务：{ startedAt, seconds, reportEvery, shotEvery, lastReport, lastShot, timer, endTimer }
    taskAgentId: null,         // 发起观察任务的 agent（会话）id：汇报定向注入到该会话，而非依赖 currentInitiator
    interpretInject: null,     // 连续解读 → 会话注入配置（inject/minInjectMs/kinds + 轮询游标）
    inferInject: null,         // 逐帧识别 → 会话注入配置（inject/injectChanges/minInjectMs + seq 游标）
    stream: null,              // AI 可读数据流：帧 → 紧凑行 → JSONL/latest.txt
    obsMode: null,             // 当前长时观察模式：'infer' | 'interpret' | null（互斥，防并发降级）
    approvalPolicy: null,      // 最近一次门控观测到的全局审批策略（'ask' | 'never'），仅用于诊断展示
    obsConfig: null,           // 看门狗恢复配置：{ mode, cmd, args }
    watchConfig: null,         // 看门狗 watch 恢复配置：{ active, intervalMs, focus }
  }

  function armAutoCleanup(ttlSec = 120) {
    clearTimeout(state.obsTtlTimer)
    const ms = Math.max(2000, Math.min(3600000, Number(ttlSec || 120) * 1000))
    state.obsTtlMs = ms
    state.obsTtlTimer = setTimeout(async () => {
      try {
        stopInject('infer'); stopInject('interpret')
        state.obsConfig = null; state.watchConfig = null; state.autoWatch = false
        if (state.obsMode === 'infer') { try { await helperCall('infer_stop', {}) } catch {} }
        if (state.obsMode === 'interpret') { try { await helperCall('interpret_stop', {}) } catch {} }
        try { await helperCall('watch_stop', {}) } catch {}
        try { await helperCall('overlay_close', {}) } catch {}
        state.obsMode = null; state.overlayOpen = false
        stopStream()
        state.obsTtlTimer = null; state.obsTtlMs = 0
        clearAllHolds()
        appendObservationLog({ type: 'auto_cleanup', reason: 'ttl_expired', ttlMs: ms })
      } catch {}
    }, ms)
    if (state.obsTtlTimer.unref) state.obsTtlTimer.unref()
  }

  function refreshAutoCleanup() {
    if (state.obsMode || state.overlayOpen || (state.watchConfig && state.watchConfig.active)) {
      armAutoCleanup(state.obsTtlMs / 1000 || 120)
    }
  }

  function clearAutoCleanupIfIdle() {
    if (state.obsMode === null && !state.overlayOpen && !(state.watchConfig && state.watchConfig.active)) {
      clearTimeout(state.obsTtlTimer)
      state.obsTtlTimer = null
      state.obsTtlMs = 0
    }
  }

  // B13 & C14: helper 异常退出时记录审计日志 + 会话告警；重连 ready 后自动恢复观察与急停态
  onHelperExit = ({ unexpected, code } = {}) => {
    const prevMode = state.obsMode
    state.obsMode = null
    if (unexpected) {
      appendObservationLog({ type: 'helper_crash', code: code == null ? -1 : code, prevMode, crashes: helperSingleton.crashes })
      if (prevMode || (state.watchConfig && state.watchConfig.active)) {
        injectToSession('[sa-perm 告警] helper 进程异常退出(code=' + code + ')，看门狗正在自动重连并恢复 ' + (prevMode || 'watch') + ' 观察模式')
      }
    } else {
      state.obsConfig = null
      state.watchConfig = null
    }
  }

  onHelperReady = async () => {
    helperSingleton.recovering = true
    try {
      if (state.paused) {
        try { await helperCall('stop', {}) } catch {}
      }
      if (state.obsConfig && state.obsConfig.cmd) {
        const cfg = state.obsConfig
        if (cfg.mode === 'infer' && state.inferInject) {
          state.inferInject.primed = false
          state.inferInject.lastSeq = 0
        }
        if (cfg.mode === 'interpret' && state.interpretInject) {
          state.interpretInject.primed = false
          state.interpretInject.lastT = null
        }
        const r = await helperCall(cfg.cmd, cfg.args)
        if (r && r.running) {
          holdHelper('obs')
          state.obsMode = cfg.mode
          helperSingleton.recoveries++
          appendObservationLog({ type: 'helper_recover', mode: cfg.mode, pid: helperSingleton.pid, recoveries: helperSingleton.recoveries })
        }
      }
      if (state.watchConfig && state.watchConfig.active) {
        if (state.watchConfig.focus) {
          try { await helperCall('watch_focus', state.watchConfig.focus) } catch {}
        }
        const wr = await helperCall('watch_start', { intervalMs: state.watchConfig.intervalMs || 1000 })
        if (wr && wr.watching) holdHelper('watch')
      }
    } catch (e) {
      console.log('[sa-perm] 看门狗恢复失败: ' + ((e && e.message) || e))
    } finally {
      helperSingleton.recovering = false
    }
  }

  const onHelperEvent = (kind, data) => {
    if (kind === 'hotkey' && data && typeof data.locked === 'boolean') {
      state.paused = data.locked
      state.pausedBy = data.locked ? 'hotkey' : null
      console.log('[sa-perm] Pause 热键：注入' + (data.locked ? '锁定' : '解锁'))
    }
    if (kind === 'change' && data && state.link) {
      // 变化事件 → 拉取 OCR 摘要 → 注入会话（节流 2s + 文本去重）
      const now = Date.now()
      if (now - state.lastLinkAt < 2000) return
      state.lastLinkAt = now
      helperCall('watch_snapshot').then((snap) => {
        if (!snap) return
        const rect = snap.rect || {}
        const text = String(snap.text || '').trim()
        const added = String(snap.added || '').trim()
        if (!snap.motion && !text) return
        const body = '[屏幕变化通知] 屏幕运动区域 (' + rect.x + ',' + rect.y + ' ' + rect.w + 'x' + rect.h + ')，变化率 ' + (snap.ratio || 0) + '，变化文本: ' + (added ? added : (text ? text : '(无文本)')) + '。若与当前任务相关可继续处理；无关则忽略。'
        if (text === state.lastLinkText && text !== '') return
        state.lastLinkText = text
        if (!linkBudgetOk()) return
        injectToSession(body)
      }).catch(() => {})
    }
  }

  // 联动分钟配额：60 秒窗口最多 6 次实际注入（预算防刷屏）
  function linkBudgetOk() {
    const now = Date.now()
    if (now - state.linkBudget.windowStart > 60000) {
      state.linkBudget = { count: 0, windowStart: now }
    }
    if (state.linkBudget.count >= 6) return false
    state.linkBudget.count++
    return true
  }

  // 变化摘要 → 注入当前发起方 agent（inject 不唤醒：对话不中断；steer 唤醒：空闲同步开轮次）
  function injectToSession(body) {
    try {
      // 优先定向到发起观察任务的 agent（会话），避免依赖 currentInitiator（观察场景可能取空）
      let initiator = undefined
      if (state.taskAgentId && ctx.agents && typeof ctx.agents.get === 'function') {
        try { initiator = ctx.agents.get(state.taskAgentId) } catch {}
      }
      if (!initiator) {
        initiator = ctx.agents && ctx.agents.currentInitiator ? ctx.agents.currentInitiator() : undefined
      }
      if (!initiator || typeof initiator.inject !== 'function') {
        console.log('[sa-perm] 无发起方 agent，跳过变化注入:', body.slice(0, 60))
        return
      }
      appendObservationLog({ type: 'inject', body })
      const message = {
        id: (globalThis.crypto && crypto.randomUUID) ? crypto.randomUUID() : 'sa-change-' + Date.now() + '-' + Math.random().toString(16).slice(2),
        role: 'user',
        content: [{ type: 'text', text: body }],
        source: { kind: 'plugin', plugin: 'dsh-screen-attention-perm', form: 'notice', summary: '屏幕变化摘要' },
      }
      if (state.linkWake) {
        initiator.steer(message)
        console.log('[sa-perm] 变化已 steer 唤醒')
      } else {
        initiator.inject(message)
        console.log('[sa-perm] 变化已 inject（不中断）')
      }
    } catch (e) {
      console.log('[sa-perm] 注入失败: ' + ((e && e.message) || e))
    }
  }
  // ===== 变化事件注入（方向3）：1s 轮询 helper status → kinds 过滤 + minInjectMs 节流 → injectToSession =====
  // 目标：把"内容变化/角色出现消失"事件按节流注入会话（≤200 字/条），供 AI 实时感知，不刷屏。
  const INJECT_MAX = 200
  const INTERPRET_KINDS = ['content_change', 'text_shift', 'dynamic_noise', 'idle']
  const clampInt = (v, def, min, max) => {
    const n = Number(v)
    if (!Number.isFinite(n)) return def
    return Math.min(max, Math.max(min, Math.round(n)))
  }
  const oneLine = (s) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim()
  const injectLine = (s) => {
    const t = oneLine(s)
    return t.length > INJECT_MAX ? t.slice(0, INJECT_MAX - 1) + '…' : t
  }
  const rectText = (r) => (r && Number.isFinite(Number(r.x)))
    ? ' @(' + Number(r.x) + ',' + Number(r.y) + ',' + Number(r.w) + ',' + Number(r.h) + ')'
    : ''
  const injectCfg = (kind) => (kind === 'interpret' ? state.interpretInject : state.inferInject)

  // 只清定时器：停止时调用（停止观察本身不在这里做）
  function stopInject(kind) {
    const c = injectCfg(kind)
    if (!c) return
    if (c.timer) { clearInterval(c.timer); c.timer = null }
    c.pending = []          // 停止后不再补播旧事件（重启时游标重新对齐）
  }

  // 待发队列 → 节流后合并成一条注入（≤200 字）；急停/锁时丢弃待发（只停注入，观察继续）
  function flushInject(c) {
    if (!c || !c.inject) return
    if (state.paused) { c.pending = []; return }
    if (Date.now() - (c.lastInjectAt || 0) < c.minInjectMs) return
    const lines = (c.pending || []).splice(0)
    if (!lines.length) return
    c.lastInjectAt = Date.now()
    injectToSession(injectLine(lines.join('；')))
  }
  function queueInject(c, line) {
    if (!c || !c.inject || !line) return
    c.pending = c.pending || []
    c.pending.push(line)
    if (c.pending.length > 6) c.pending.splice(0, c.pending.length - 6)
    flushInject(c)
  }

  // 1s 轮询连续解读历史：按 kinds 取新增条目（游标=最后一条时间戳）
  async function pollInterpretInject() {
    const c = state.interpretInject
    if (!c || !c.timer || !c.inject) return
    if (!helperSingleton.online || helperSingleton.recovering || helperSingleton.needsRecovery || c.busy) return
    c.busy = true
    try {
      let st
      try { st = await helperCall('interpret_status', { limit: 12 }) } catch { return }
      if (!st) return
      if (st.running === false) { stopInject('interpret'); return }
      const hist = Array.isArray(st.history) ? st.history.filter((h) => h && h.t) : []
      if (!hist.length) { flushInject(c); return }
      const newest = hist[hist.length - 1]
      if (!c.primed) { c.primed = true; c.lastT = newest.t; return }   // 首轮只对齐游标，不注入历史
      let fresh = []
      let idx = -1
      for (let i = hist.length - 1; i >= 0; i--) { if (hist[i].t === c.lastT) { idx = i; break } }
      if (idx >= 0) fresh = hist.slice(idx + 1)
      else if (newest.t !== c.lastT) fresh = [newest]                  // 游标滑出窗口：只取最新一条，防补播旧事件
      c.lastT = newest.t
      const kinds = Array.isArray(c.kinds) && c.kinds.length ? c.kinds : ['content_change']
      for (const h of fresh) {
        const added = oneLine(h.added)
        if (!kinds.includes(h.kind) || !added) continue
        appendObservationLog({ type: 'interpret', kind: h.kind, rect: h.rect, added })
        queueInject(c, '[解读] ' + h.kind + rectText(h.rect) + '：' + added)
      }
      flushInject(c)
    } finally { c.busy = false }
  }

  // 1s 轮询逐帧识别历史：只取角色出现/消失（游标=帧 seq），10s 同角色同动作去重
  async function pollInferInject() {
    const c = state.inferInject
    if (!c || !c.timer || !c.inject) return
    if (!helperSingleton.online || helperSingleton.recovering || helperSingleton.needsRecovery || c.busy) return
    c.busy = true
    try {
      let st
      try { st = await helperCall('infer_status', { limit: 12 }) } catch { return }
      if (!st) return
      if (st.running === false) { stopInject('infer'); return }
      const hist = Array.isArray(st.history) ? st.history.filter((h) => h && Number.isFinite(Number(h.seq))) : []
      if (!hist.length) { flushInject(c); return }
      const newestSeq = Math.max(...hist.map((h) => Number(h.seq)))
      if (!c.primed) { c.primed = true; c.lastSeq = newestSeq; return }  // 首轮只对齐游标
      if (newestSeq <= c.lastSeq) { flushInject(c); return }
      const fresh = hist.filter((h) => Number(h.seq) > c.lastSeq)
      c.lastSeq = newestSeq
      const objs = (st.latest && Array.isArray(st.latest.objects)) ? st.latest.objects : []
      const rectById = new Map()
      for (const o of objs) { if (o && o.id != null) rectById.set(Number(o.id), o) }
      const now = Date.now()
      c.recent = (c.recent instanceof Map) ? c.recent : new Map()
      for (const [k, ts] of c.recent) { if (now - ts > 10000) c.recent.delete(k) }
      for (const h of fresh) {
        const changes = Array.isArray(h.changes) ? h.changes : []
        for (const raw of changes) {
          const s = oneLine(raw)
          if (s.startsWith('窗口切换 -> ')) {
            if (!c.injectChanges) continue
            const winKey = 'win|' + s
            if (c.recent.has(winKey)) continue
            c.recent.set(winKey, now)
            appendObservationLog({ type: 'window_switch', title: s.slice(8), understanding: (st.latest && st.latest.understanding) || '' })
            queueInject(c, '[窗口] ' + s)
            continue
          }
          const m = /^(.*?)#(\d+)\s*(出现|消失)$/.exec(s)
          if (!m) continue                                  // 只注入角色出现/消失或窗口切换；"移动"等噪声不注入
          if (!c.injectChanges) continue
          const key = m[1] + '|' + m[3]
          if (c.recent.has(key)) continue                    // 10s 内同角色同动作去重（防抖动刷屏）
          c.recent.set(key, now)
          const o = rectById.get(Number(m[2]))
          const unknown = m[1].startsWith('未知角色#')
          const hint = unknown ? '（未登记，可用 saperm_char_promote 命名）' : ''
          appendObservationLog({ type: 'infer', role: m[1], action: m[3], rect: o || null, understanding: (st.latest && st.latest.understanding) || '' })
          queueInject(c, '[角色] ' + m[1] + ' ' + m[3] + (o ? rectText(o) : '') + hint)
        }
      }
      flushInject(c)
    } finally { c.busy = false }
  }

  function injectSummary(c) {
    if (!c) return null
    return {
      inject: !!c.inject,
      minInjectMs: c.minInjectMs,
      ...(c.kinds ? { kinds: c.kinds } : {}),
      ...(c.injectChanges !== undefined ? { injectChanges: c.injectChanges } : {}),
      pending: (c.pending || []).length,
      polling: !!c.timer,
    }
  }

  // 启动注入：先停旧定时器（重复调用安全）；inject=false 时只记录配置
  function startInterpretInject(opts) {
    stopInject('interpret')
    let kinds = Array.isArray(opts.kinds) && opts.kinds.length ? opts.kinds.map(String) : ['content_change']
    kinds = kinds.filter((k) => INTERPRET_KINDS.includes(k))
    if (!kinds.length) kinds = ['content_change']
    const c = {
      inject: !!opts.inject,
      minInjectMs: clampInt(opts.minInjectMs, 2000, 200, 60000),
      kinds,
      timer: null, primed: false, lastT: null, lastInjectAt: 0, pending: [],
    }
    state.interpretInject = c
    if (c.inject) c.timer = setInterval(() => { pollInterpretInject().catch(() => {}) }, 1000)
    return c
  }
  function startInferInject(opts) {
    stopInject('infer')
    const c = {
      inject: !!opts.inject,
      injectChanges: opts.injectChanges !== false,
      minInjectMs: clampInt(opts.minInjectMs, 2000, 200, 60000),
      timer: null, primed: false, lastSeq: 0, lastInjectAt: 0, pending: [], recent: new Map(),
    }
    state.inferInject = c
    if (c.inject) c.timer = setInterval(() => { pollInferInject().catch(() => {}) }, 1000)
    return c
  }

  // ===== AI 可读数据流（图片流 → 紧凑文本行 → JSONL / latest.txt） =====
  // 目标：每帧识别结果压成一行（~100-160 字节），AI 直接读行即可理解屏幕，
  // 无需解析 infer_status 的大 JSON；文本变体走增量（= 相同 / +尾部新增）。
  const STREAM_MAX_RING = 600
  const STREAM_LATEST_KEEP = 200
  const STREAM_TX_MAX = 140
  const streamDay = (ts) => new Date(ts).toISOString().slice(0, 10)
  const streamFile = (d) => join(LOGS_DIR, 'stream-' + d + '.jsonl')
  const streamLatest = () => join(LOGS_DIR, 'stream-latest.txt')

  // 2s 预算守门：区域越大，OCR 越容易超时 → 自动降采样（调用方可用 scale 显式覆盖下限）
  function pickScale(rect, requested) {
    const base = Number.isFinite(requested) ? Number(requested) : 1
    if (!rect) return Math.min(base, 0.7)          // 无区域=全屏
    const area = Math.max(1, (rect.w || 0) * (rect.h || 0))
    if (area > 2500000) return Math.min(base, 0.7)
    if (area > 1200000) return Math.min(base, 0.85)
    return base
  }

  function compactText(s0, max) {
    let t = String(s0 == null ? '' : s0).replace(/\s+/g, ' ').trim()
    if (!t) return ''
    const parts = t.split('|').map((x) => x.trim()).filter(Boolean)
    const uniq = []
    const seen = new Set()
    for (const p of parts) { if (!seen.has(p)) { seen.add(p); uniq.push(p) } }
    t = uniq.join(' | ')
    return t.length > max ? t.slice(0, max - 1) + '…' : t
  }

  function objSummary(objects) {
    if (!Array.isArray(objects) || !objects.length) return ''
    const agg = new Map()
    for (const o of objects) {
      if (!o) continue
      const label = String(o.labelZh || o.label || '?')
      const tag = o.identity ? '(' + o.identity + ')' : (o.unknownId ? '(未登记)' : '')
      const key = label + tag
      agg.set(key, (agg.get(key) || 0) + 1)
    }
    const out = []
    for (const [k, n] of agg) out.push(n > 1 ? k + '×' + n : k)
    const joined = out.join(',')
    return joined.length > 80 ? joined.slice(0, 79) + '…' : joined
  }

  function textDelta(prev, cur) {
    if (!cur) return ''
    if (cur === prev) return '='
    if (prev && cur.startsWith(prev)) return '+' + cur.slice(prev.length)
    return cur
  }

  function formatStreamLine(r) {
    let out = r.t + ' #' + r.s + ' W=' + (r.w || '-') + ' SC=' + (r.sc || '-')
    if (r.ob) out += ' OB=' + r.ob
    if (r.fa) out += ' F=' + r.fa
    if (r.tx) out += ' TX=' + r.tx
    if (Array.isArray(r.ch) && r.ch.length) out += ' CH=' + r.ch.join(';')
    out += ' ' + r.fps + 'fps ' + r.ocr + 'ms'
    return out
  }

  function flushStreamLatest() {
    const st = state.stream
    if (!st) return
    try {
      mkdirSync(LOGS_DIR, { recursive: true })
      const lines = st.ring.slice(-STREAM_LATEST_KEEP).map(formatStreamLine)
      writeFileSync(streamLatest(), lines.join('\n') + (lines.length ? '\n' : ''), 'utf8')
    } catch {}
  }

  async function pollStream() {
    const st = state.stream
    if (!st || !st.timer || st.busy) return
    if (!helperSingleton.online || helperSingleton.recovering || helperSingleton.needsRecovery) return
    st.busy = true
    try {
      let r
      try { r = await helperCall('infer_status', { limit: 1 }) } catch { return }
      if (!r || !r.latest) return
      const L = r.latest
      const seq = Number(L.seq || 0)
      if (!seq) return
      st.polls++
      if (seq === st.lastSeq) { st.dups++; return }
      st.lastSeq = seq
      const now = Date.now()
      const tx = compactText(L.text, STREAM_TX_MAX)
      const row = {
        t: new Date(now).toTimeString().slice(0, 8),
        s: seq,
        w: String(L.fgTitle || '').slice(0, 40),
        sc: String(L.scene || ''),
        ob: objSummary(L.objects),
        fa: Number(L.faceCount || 0),
        tx: textDelta(st.lastText, tx),
        ch: [...new Set((Array.isArray(L.changes) ? L.changes : []).map((x) => oneLine(x)).filter(Boolean))].slice(0, 3),
        fps: Number(r.fps || 0),
        ocr: Number(L.ocrMs || 0),
      }
      st.lastText = tx
      // 静止节流：与上一行实质相同（窗口/场景/物体/脸/文本均未变且无变化事件）时，
      // 每 5s 才落一行心跳，避免静止画面把日志和带宽刷满。
      const sameAsLast = st.lastRow
        && st.lastRow.w === row.w && st.lastRow.sc === row.sc && st.lastRow.ob === row.ob
        && st.lastRow.fa === row.fa && row.tx === '=' && row.ch.length === 0
      if (sameAsLast && (now - (st.lastWriteAt || 0)) < (st.idleWriteMs || 5000)) {
        st.idleSkips = (st.idleSkips || 0) + 1
        return
      }
      // CH 去重：与上一行完全相同的变化事件不再重复携带，避免每行都背 3 条陈旧 CH
      const chKey = row.ch.join('|')
      if (chKey && chKey === (st.lastChKey || '')) row.ch = []
      st.lastChKey = chKey
      if (row.ch.length === 0) st.lastChKey = ''
      st.lastRow = row
      st.lastWriteAt = now
      st.rows++
      st.ring.push(row)
      if (st.ring.length > STREAM_MAX_RING) st.ring.splice(0, st.ring.length - STREAM_MAX_RING)
      try {
        mkdirSync(LOGS_DIR, { recursive: true })
        appendFileSync(streamFile(streamDay(now)), JSON.stringify(row) + '\n', 'utf8')
      } catch {}
      st.sinceFlush = (st.sinceFlush || 0) + 1
      if (st.sinceFlush >= 15) { st.sinceFlush = 0; flushStreamLatest() }
    } finally { st.busy = false }
  }

  function startStream(opts) {
    const intervalMs = clampInt(opts && opts.intervalMs, 1000, 200, 10000)
    if (state.stream && state.stream.timer) {
      if (state.stream.intervalMs === intervalMs) return state.stream
      stopStream()
    }
    const st = {
      timer: null, intervalMs, ring: [], lastSeq: 0, lastText: '',
      rows: 0, polls: 0, dups: 0, busy: false, sinceFlush: 0, startedAt: Date.now(),
    }
    state.stream = st
    st.timer = setInterval(() => { pollStream().catch(() => {}) }, intervalMs)
    if (st.timer.unref) st.timer.unref()
    pollStream().catch(() => {})
    return st
  }

  function stopStream() {
    const st = state.stream
    if (!st) return null
    if (st.timer) { clearInterval(st.timer); st.timer = null }
    flushStreamLatest()
    const summary = { rows: st.rows, polls: st.polls, dups: st.dups }
    return summary
  }

  function streamStatus() {
    const st = state.stream
    if (!st) return { running: false, rows: 0, lastSeq: 0, file: streamLatest() }
    return {
      running: !!st.timer, intervalMs: st.intervalMs, rows: st.rows,
      polls: st.polls, dups: st.dups, idleSkips: st.idleSkips || 0, lastSeq: st.lastSeq, file: streamLatest(),
    }
  }

  helperSingleton.listeners.add(onHelperEvent)
  acquireHelper()
  ctx.effect(() => () => {
    clearTimeout(state.obsTtlTimer)
    state.obsTtlTimer = null
    stopInject('interpret')
    stopInject('infer')
    helperSingleton.listeners.delete(onHelperEvent)
    releaseHelper()
  })

  // ---- 读取 DSH 全局审批策略 ----
  // 'never' 表示用户已在全局预设中选择「不审批 / 全权（danger-full-access 等）」。
  // 此时 DSH 会把任何 { kind: 'ask' } 直接判定为 rejected（见 dsh-user-approval decide()），
  // 若插件仍然 ask，鼠标/键盘注入就会永久不可用 —— 必须识别该策略并直接放行。
  function approvalPolicyOf(agent) {
    try {
      const approval = ctx.get ? ctx.get('approval') : undefined
      if (!approval) return 'ask'
      const session = agent && agent.session
      if (session && typeof approval.effectivePolicy === 'function') {
        const p = approval.effectivePolicy(session)
        if (p) return String(p)
      }
      if (approval.config && approval.config.policy) return String(approval.config.policy)
    } catch {}
    return 'ask'
  }

  // ---- 统一门：pre-execute waterfall（急停锁 + L2/L3/resume 审批） ----
  ctx.on('tools/pre-execute', (exec, next) => {
    if (exec.agent === undefined) return next()
    const agentId = exec.agent.id
    // 记录发起观察任务的 agent（会话）id → 汇报定向注入到该会话（防中断：汇报不会因 currentInitiator 变化而丢失）
    if (exec.name === 'saperm_observe_task') {
      state.taskAgentId = agentId
    }
    // 急停锁最高优先级：任何策略下都拦截注入
    if (state.paused && INPUT_TOOLS.includes(exec.name)) {
      return { kind: 'deny', reason: `急停已激活（${state.pausedBy || 'unknown'}），注入已锁定；用 saperm_resume 恢复（需用户确认）` }
    }
    // 全局 never（全权）模式：不再 ask（ask 必被自动拒绝），直接放行并留审计
    const policy = approvalPolicyOf(exec.agent)
    state.approvalPolicy = policy
    if (policy === 'never') {
      if (INPUT_TOOLS.includes(exec.name)) state.l2.set(agentId, 'granted')
      if (L3_TOOLS.includes(exec.name)) state.l3.set(agentId, 'granted')
      if (INPUT_TOOLS.includes(exec.name) || exec.name === 'saperm_resume') {
        try { appendObservationLog({ type: 'auto_grant', tool: exec.name, policy }) } catch {}
      }
      return next()
    }
    if (exec.name === 'saperm_resume') {
      return { kind: 'ask', reason: '恢复输入注入需要用户明确确认（AI 不能自行解除急停）' }
    }
    if (L3_TOOLS.includes(exec.name)) {
      // L3 高危：本会话首次审批一次，通过后本会话内免审（与 L2 授权相互独立，L2 通过不连带放开 L3）
      if (state.l3.get(agentId) === 'granted') return next()
      state.pending.set(agentId, true)
      return { kind: 'ask', reason: '高危动作（拖拽/双击）：批准本次执行并授权本会话内后续高危操作（急停 saperm_stop 仍可随时锁定）' }
    }
    if (L2_TOOLS.includes(exec.name)) {
      const g = state.l2.get(agentId)
      if (g === 'granted') return next()
      // 曾经的 'denied' 不再永久封锁：拒绝可能来自应答者不可用（unavailable）或策略切换，
      // 允许下一次重新发起审批，避免会话被永久卡成「不能用鼠标」。
      state.pending.set(agentId, true)
      return { kind: 'ask', reason: '首次普通鼠标输入：批准本次执行并授权本会话内后续普通点击/滚轮/文本输入（拖拽、连击等高危动作仍逐次审批）' }
    }
    return next()
  })

  // ---- 结果观察：成功 → 升级会话级授权；审批被拒 → fail-closed；
  //      执行期错误（USER_ACTIVE 让位 / helper 离线 / 超时）→ 保持未决，下次调用重新 ask ----
  const isApprovalRejection = (result) => {
    const msg = String((result && (result.error || result.message || result.reason)) || '')
    return /已拒绝|被拒绝|denied|rejected|refused|declined|not approved|cancelled|已取消/.test(msg)
  }

  ctx.on('tools/result', (exec, result) => {
    if (exec.agent === undefined) return
    const agentId = exec.agent.id
    if ((L2_TOOLS.includes(exec.name) || L3_TOOLS.includes(exec.name)) && state.pending.get(agentId)) {
      const isL3 = L3_TOOLS.includes(exec.name)
      const bucket = isL3 ? state.l3 : state.l2
      const tag = isL3 ? 'L3' : 'L2'
      state.pending.delete(agentId)
      if (result.isError === false) {
        bucket.set(agentId, 'granted')
        console.log('[sa-perm] ' + tag + ' 会话级授权已授予 agent=' + agentId)
      } else if (isApprovalRejection(result)) {
        // 仅做可观测标记，不再永久封锁（下一次调用会重新审批）
        bucket.set(agentId, 'denied')
        console.log('[sa-perm] 审批被拒（下次调用会重新发起审批）agent=' + agentId)
      } else {
        bucket.delete(agentId)
        console.log('[sa-perm] ' + tag + ' 执行期错误（保持未决，可重试）agent=' + agentId + ': ' + String((result && (result.error || result.message)) || '').slice(0, 120))
      }
    }
  })

  function register(def) {
    return ctx.tools.register({
      ...def,
      execute: async (args, ...rest) => sanitizeJson(await def.execute(args || {}, ...rest)),
      output: {
        schema: { type: 'object', additionalProperties: true },
        render: (_args, value) => [{ type: 'text', text: JSON.stringify(value) }],
      },
      isConcurrencySafe: () => false,
    })
  }

  const empty = { type: 'object', properties: {} }
  const point = (required) => ({
    type: 'object',
    properties: {
      x: { type: 'integer', description: '屏幕 x 坐标' },
      y: { type: 'integer', description: '屏幕 y 坐标' },
    },
    required: required || ['x', 'y'],
  })

  register({
    name: 'saperm_status',
    description: '查看权限模块状态：会话级授权、急停状态、helper 进程状态（观察类，无需审批）',
    parameters: empty,
    execute: async () => {
      const l2 = {}
      for (const k of state.l2.keys()) l2[k] = state.l2.get(k)
      const l3 = {}
      for (const k of state.l3.keys()) l3[k] = state.l3.get(k)
      return {
        paused: state.paused,
        pausedBy: state.pausedBy,
        approvalPolicy: state.approvalPolicy || null,
        l2ByAgent: l2,
        l3ByAgent: l3,
        interruptProtect: state.interruptProtect,
        observeTask: state.observeTask ? { startedAt: state.observeTask.startedAt, seconds: state.observeTask.seconds, elapsedMs: Date.now() - state.observeTask.startedAt } : null,
        injection: { interpret: injectSummary(state.interpretInject), infer: injectSummary(state.inferInject) },
        helper: { online: helperSingleton.online, pid: helperSingleton.pid },
      }
    },
  })

  // ============ AI 自主视角：多尺度观察（L1 观察类，免审） ============
  register({
    name: 'saperm_view',
    description: 'AI 自主设置观察视角：截取指定屏幕区域（省略 x/y/w/h=全屏），scale>1 拉近放大，返回图片文件路径+该区域在大图中的屏幕坐标 rect（用于多尺度对应：大图=全屏截图，小图=本区域裁剪，rect 即对应关系）。同时把该视角标记到注意力小窗（红框+note）。观察类，无需审批',
    parameters: {
      type: 'object',
      properties: {
        x: { type: 'integer', description: '区域左上 x（屏幕坐标）' },
        y: { type: 'integer', description: '区域左上 y' },
        w: { type: 'integer', description: '区域宽' },
        h: { type: 'integer', description: '区域高' },
        scale: { type: 'number', description: '放大倍数（默认 1；2=2x 放大细看）' },
        maxW: { type: 'integer', description: '输出图最大宽度（默认 0=不限制）' },
        note: { type: 'string', description: '视野说明（显示在小窗底部，如"查看的是按钮区"）' },
      },
    },
    execute: async (args) => {
      const shot = await helperCall('screenshot', {
        x: args.x, y: args.y, w: args.w, h: args.h, scale: args.scale, maxW: args.maxW,
      })
      const path = saveShot(shot.jpegB64, args.w ? 'focus' : 'glance')
      const rect = shot.rect || null
      state.overlayOpen = true
      refreshAutoCleanup()
      if (rect) {
        try { await helperCall('overlay_set', { region: rect, note: args.note || 'AI 视角' }) } catch {}
      }
      return { path, rect, imageW: shot.bw || shot.w, imageH: shot.bh || shot.h, note: args.note || '' }
    },
  })

  register({
    name: 'saperm_glance',
    description: '全屏大图（glance 快速语义，默认最大宽 1280）：返回图片文件路径+全屏 rect。配合 saperm_view(小区域) 构成多尺度对应：小图 rect 是大图的子区域。观察类，无需审批',
    parameters: {
      type: 'object',
      properties: { maxW: { type: 'integer', description: '输出最大宽度（默认 1280）' } },
    },
    execute: async (args) => {
      const shot = await helperCall('screenshot', { maxW: args.maxW || 1920 })
      const path = saveShot(shot.jpegB64, 'glance')
      return { path, rect: shot.rect || null, imageW: shot.bw || shot.w, imageH: shot.bh || shot.h, note: '全屏' }
    },
  })
  // ============ 注意力可视化小窗（L1 观察类，免审） ============
  register({
    name: 'saperm_overlay_open',
    description: '打开注意力可视化悬浮小窗：实时显示采集的屏幕画面、AI 注意力区域（红框）、注视点（十字）与注意点文字说明。小窗与观察引擎捆绑：若 watch 未运行则自动以低频(1000ms,运动自动提速)启动同一采集管线。默认 480x270 位于屏幕右下角；小窗可拖动、双击关闭（观察类，无需审批）',
    parameters: {
      type: 'object',
      properties: {
        x: { type: 'integer', description: '窗口左上角屏幕 x 坐标（默认屏幕右侧）' },
        y: { type: 'integer', description: '窗口左上角屏幕 y 坐标（默认屏幕下部）' },
        w: { type: 'integer', description: '窗口宽度（默认 480）' },
        h: { type: 'integer', description: '窗口高度（默认 270）' },
        ttlSec: { type: 'integer', description: '无操作自动关闭秒数（默认 120 秒，到期自动停止观察并关闭小窗）' },
      },
    },
    execute: async (args) => {
      state.overlayOpen = true
      const defaultTtl = state.obsMode ? (state.obsTtlMs / 1000 || 120) : 60
      armAutoCleanup(args.ttlSec || defaultTtl)
      const r = await helperCall('overlay_open', { x: args.x, y: args.y, w: args.w, h: args.h })
      // 捆绑：小窗画面依赖 watch 帧源——未运行则自动低频启动（单一采集引擎）
      if (r && r.open) {
        try {
          const st = await helperCall('watch_status')
          if (st && !st.watching) {
            await helperCall('watch_start', { intervalMs: 1000 })
            state.autoWatch = true
          }
        } catch {}
      }
      return { ...(r || {}), autoWatch: state.autoWatch }
    },
  })

  register({
    name: 'saperm_overlay_close',
    description: '关闭注意力可视化悬浮小窗：若 watch 是随小窗自动启动的（autoWatch），一并停止采集以省资源；显式启动的 watch 保留（观察类，无需审批）',
    parameters: empty,
    execute: async () => {
      state.overlayOpen = false
      const r = await helperCall('overlay_close')
      if (state.autoWatch) {
        state.autoWatch = false
        try { await helperCall('watch_stop') } catch {}
        releaseHold('watch')
      }
      clearAutoCleanupIfIdle()
      return { ...(r || {}), autoWatchStopped: true }
    },
  })


  register({
    name: 'saperm_overlay_set',
    description: '设置 AI 注意力标记并显示在小窗上：注意力区域（红框）、注视点（十字）、注意点文字说明。可单独清除区域或注视点（观察类，无需审批）',
    parameters: {
      type: 'object',
      properties: {
        region: { type: 'object', description: '注意力区域 {x,y,w,h}（屏幕坐标）', properties: { x: { type: 'integer' }, y: { type: 'integer' }, w: { type: 'integer' }, h: { type: 'integer' } } },
        gaze: { type: 'object', description: '注视点 {x,y}（屏幕坐标）', properties: { x: { type: 'integer' }, y: { type: 'integer' } } },
        clearRegion: { type: 'boolean', description: '清除区域红框' },
        clearGaze: { type: 'boolean', description: '清除注视点十字' },
        note: { type: 'string', description: 'AI 注意点说明文字（显示在小窗底部）' },
      },
    },
    execute: async (args) => {
      state.overlayOpen = true
      refreshAutoCleanup()
      return helperCall('overlay_set', { region: args.region, gaze: args.gaze, clearRegion: args.clearRegion, clearGaze: args.clearGaze, note: args.note })
    },
  })

  register({
    name: 'saperm_watch_start',
    description: '启动持续屏幕采集（AI 控频：intervalMs 最小 50ms，默认 1000ms）。采集帧实时显示在小窗上（观察类，无需审批）',
    parameters: {
      type: 'object',
      properties: {
        intervalMs: { type: 'integer', description: '采集间隔毫秒（默认 1000，最小 50）' },
        ttlSec: { type: 'integer', description: '无操作自动关闭秒数（默认 120 秒，到期自动停止观察并关闭小窗）' },
      },
    },
    execute: async (args) => {
      state.autoWatch = false
      armAutoCleanup(args.ttlSec || 120)
      const r = await helperCall('watch_start', { intervalMs: args.intervalMs })
      if (r && r.watching) {
        holdHelper('watch')
        state.watchConfig = { active: true, intervalMs: args.intervalMs || 1000, focus: (state.watchConfig && state.watchConfig.focus) || null }
      }
      return r
    },
  })

  register({
    name: 'saperm_watch_stop',
    description: '停止持续屏幕采集（观察类，无需审批）',
    parameters: {
      type: 'object',
      properties: {
        keepOverlay: { type: 'boolean', description: '是否保留注意力小窗（默认 false 自动关闭）' },
      },
    },
    execute: async (args) => {
      state.autoWatch = false
      state.watchConfig = null
      const r = await helperCall('watch_stop')
      releaseHold('watch')
      let overlayClosed = false
      if (!args || !args.keepOverlay) {
        try { await helperCall('overlay_close', {}) } catch {}
        if (state.autoWatch) { state.autoWatch = false; try { await helperCall('watch_stop', {}) } catch {}; releaseHold('watch') }
        state.overlayOpen = false
        overlayClosed = true
      }
      clearAutoCleanupIfIdle()
      return overlayClosed ? { ...(r || {}), overlayClosed: true } : r
    },
  })

  register({
    name: 'saperm_observe',
    description: '取当前屏幕变化快照：运动区域坐标、变化率、区域 OCR 识别文本（≤600 字符）。静止时返回 motion=false。屏幕无变化时该工具几乎瞬时返回（观察类，无需审批）',
    parameters: empty,
    execute: async () => {
      refreshAutoCleanup()
      return helperCall('watch_snapshot')
    },
  })

  register({
    name: 'saperm_watch_link',
    description: '开启屏幕持续观察并联动：启动 watch（默认 800ms，运动时自动加速到 100ms），屏幕变化时自动把变化摘要注入当前会话——inject 模式不打断对话（默认），wake 模式用 steer 唤醒 AI 立即响应。可选 focus 限定关注区域（如对话窗口），此时以区域内 OCR 文本增量为变化摘要。返回当前运动状态',
    parameters: {
      type: 'object',
      properties: {
        intervalMs: { type: 'integer', description: '静止时采集间隔毫秒（默认 800，最小 100）' },
        wake: { type: 'boolean', description: 'true=变化时 steer 唤醒 AI 立即响应；false(默认)=inject 排队不打断' },
        focus: { type: 'object', description: '关注区域 {x,y,w,h}（屏幕坐标），限定观察范围（如对话窗口区域，用于感知对话输出）', properties: { x: { type: 'integer' }, y: { type: 'integer' }, w: { type: 'integer' }, h: { type: 'integer' } } },
      },
    },
    execute: async (args) => {
      const iv = Number.isFinite(args.intervalMs) ? args.intervalMs : 800
      state.link = true
      state.linkWake = !!args.wake
      state.autoWatch = false   // 显式启动 → 关闭小窗时不停 watch
      if (args.focus && Number.isFinite(args.focus.x)) {
        await helperCall('watch_focus', args.focus)
      }
      const r = await helperCall('watch_start', { intervalMs: iv })
      const st = await helperCall('watch_status')
      return { ...r, linked: true, wake: state.linkWake, ...(st || {}) }
    },
  })

  register({
    name: 'saperm_watch_focus',
    description: '设置/清除观察焦点区域：只对指定屏幕区域做 OCR 文本增量感知（如对话窗口——实时察觉 AI 自己输出的对话内容）；clear=true 恢复全屏。观察类，无需审批',
    parameters: {
      type: 'object',
      properties: {
        x: { type: 'integer', description: '区域左上 x（屏幕坐标）' },
        y: { type: 'integer', description: '区域左上 y（屏幕坐标）' },
        w: { type: 'integer', description: '区域宽' },
        h: { type: 'integer', description: '区域高' },
        clear: { type: 'boolean', description: 'true=清除焦点，恢复全屏观察' },
      },
    },
    execute: async (args) => {
      if (args.clear) return helperCall('watch_focus', { clear: true })
      return helperCall('watch_focus', { x: args.x, y: args.y, w: args.w, h: args.h })
    },
  })

  register({
    name: 'saperm_watch_unlink',
    description: '关闭屏幕变化自动注入联动（可继续 watch 或停止观察）',
    parameters: empty,
    execute: async () => { state.link = false; return { linked: false } },
  })

  register({
    name: 'saperm_observe_task',
    description: '启动持续观察任务（防打断模式）：自动开启防打断状态 + 持续采集 + 打开注意力小窗，按秒级周期汇报屏幕变化摘要、按图级周期抓取屏幕图像（生成图路径注入会话），到任务时长后自动关闭防打断并收尾。时长由 AI 按当前任务决定（seconds 入参），默认 180 秒。观察类机制，无需审批',
    parameters: {
      type: 'object',
      properties: {
        seconds: { type: 'integer', description: '任务时长（秒），AI 按任务需要决定；默认 180' },
        intervalMs: { type: 'integer', description: '采集间隔毫秒（默认 800，运动自动提速）' },
        reportEveryMs: { type: 'integer', description: '文字汇报间隔毫秒（默认 10000）' },
        shotEveryMs: { type: 'integer', description: '屏幕图像抓取间隔毫秒（默认 30000）' },
        focus: { type: 'object', description: '关注区域 {x,y,w,h}（屏幕坐标），只感知该区域 OCR 增量', properties: { x: { type: 'integer' }, y: { type: 'integer' }, w: { type: 'integer' }, h: { type: 'integer' } } },
      },
    },
    execute: async (args) => {
      const secs = Math.min(86400, Math.max(5, Number(args.seconds) || 180))
      const interval = Math.min(5000, Math.max(100, Number(args.intervalMs) || 800))
      const reportEvery = Math.min(120000, Math.max(2000, Number(args.reportEveryMs) || 10000))
      const shotEvery = Math.min(3600000, Math.max(5000, Number(args.shotEveryMs) || 30000))
      // 先清理可能残留的旧任务
      if (state.observeTask) {
        const old = state.observeTask
        if (old.timer) clearInterval(old.timer)
        if (old.endTimer) clearTimeout(old.endTimer)
        state.observeTask = null
      }
      // 开防打断 + 联动（inject 不中断对话）
      state.interruptProtect = true
      state.link = true
      state.linkWake = false
      if (args.focus && Number.isFinite(args.focus.x)) {
        try { await helperCall('watch_focus', args.focus) } catch {}
      }
      try { await helperCall('watch_start', { intervalMs: interval }) } catch {}
      try { await helperCall('overlay_open', {}) } catch {}
      const task = { startedAt: Date.now(), seconds: secs, reportEvery, shotEvery, lastReport: 0, lastShot: 0, timer: null, endTimer: null, inFlight: false }
      state.observeTask = task
      injectToSession('[观察任务启动] 防打断已开启，持续识别 ' + secs + ' 秒。每 ' + Math.round(reportEvery / 1000) + 's 汇报屏幕变化摘要，每 ' + Math.round(shotEvery / 1000) + 's 抓取图像。')
      task.timer = setInterval(async () => {
        if (task.inFlight) return
        task.inFlight = true
        setTimeout(() => { task.inFlight = false }, 800)
        const t = Date.now()
        const elapsed = Math.round((t - task.startedAt) / 1000)
        try {
          if (t - task.lastReport >= task.reportEvery) {
            task.lastReport = t
            const snap = await helperCall('watch_snapshot')
            const rect = (snap && snap.rect) || {}
            const text = String((snap && snap.text) || '').trim()
            const added = String((snap && snap.added) || '').trim()
            const note = '已防打断）'
            injectToSession('[识图 ' + elapsed + 's/' + secs + 's] 运动=' + (snap && snap.motion) + ' 变化率=' + ((snap && snap.ratio) || 0) + ' 区域(' + rect.x + ',' + rect.y + ' ' + rect.w + 'x' + rect.h + ') 文本: ' + String(added || text || '(无变化)').slice(0, 220))
          }
          if (t - task.lastShot >= task.shotEvery) {
            task.lastShot = t
            const shot = await helperCall('screenshot', { maxW: 900 })
            if (shot && shot.jpegB64) {
              const path = saveShot(shot.jpegB64, 'task')
              injectToSession('[识图图像 ' + elapsed + 's] 屏幕快照已生成: ' + path)
            }
          }
        } catch (e) {
          console.log('[sa-perm] 观察任务汇报失败: ' + ((e && e.message) || e))
        }
      }, 1000)
      task.endTimer = setTimeout(async () => {
        if (state.observeTask === task) state.observeTask = null
        state.interruptProtect = false
        state.link = false
        if (task.timer) clearInterval(task.timer)
        try { await helperCall('watch_stop') } catch {}
        try { await helperCall('overlay_close') } catch {}
        injectToSession('[观察任务完成] 已持续识别 ' + secs + ' 秒，防打断已自动关闭。')
      }, secs * 1000)
      return { started: true, seconds: secs, intervalMs: interval, reportEveryMs: reportEvery, shotEveryMs: shotEvery, interruptProtect: true, linked: true }
    },
  })

  register({
    name: 'saperm_observe_task_stop',
    description: '手动停止当前持续观察任务并关闭防打断（AI 可结束任务时调用）',
    parameters: empty,
    execute: async () => {
      const task = state.observeTask
      if (task) {
        if (task.timer) clearInterval(task.timer)
        if (task.endTimer) clearTimeout(task.endTimer)
        state.observeTask = null
      }
      state.interruptProtect = false
      state.link = false
      try { await helperCall('watch_stop') } catch {}
      try { await helperCall('overlay_close') } catch {}
      return { stopped: true, interruptProtect: false }
    },
  })

  register({
    name: 'saperm_locate',
    description: '按文字定位屏幕元素：本地 OCR 全屏扫描，返回含 keyword 的文字词与坐标{text,x,y,w,h}。用于找到屏幕上的文字/按钮/图标名（如"哔哩哔哩"）。本地 Windows 识别，不受外部 vision 限流影响。观察类，无需审批',
    parameters: {
      type: 'object',
      properties: {
        keyword: { type: 'string', description: '要查找的文字（必填）' },
        scale: { type: 'number', description: 'OCR 放大倍数（默认 1；2 更清晰但更慢）' },
        full: { type: 'boolean', description: 'true=全屏扫描；默认只扫前台窗口（快 3~10 倍）' },
        ocrOnly: { type: 'boolean', description: 'true=跳过 UIA 通道，只做 OCR' },
        maxMs: { type: 'integer', description: '耗时预算毫秒（默认 1900）' },
      },
      required: ['keyword'],
    },
    execute: async (args) => {
      const t0 = Date.now()
      const kw = String(args.keyword || '').trim()
      if (!kw) return { found: 0, keyword: '', items: [], elapsedMs: 0 }
      const k = kw.replace(/\s+/g, '')
      const budgetMs = clampInt(args.maxMs, 1900, 300, 8000)
      const scale = Number.isFinite(args.scale) ? Number(args.scale) : 1
      const results = []
      const seen = new Set()
      const push = (it) => { const key = it.text + '@' + it.x + ',' + it.y; if (!seen.has(key)) { seen.add(key); results.push(it) } }
      const stages = {}
      let winList = []
      try { const w = await helperCall('windows'); winList = (w && w.windows) || [] } catch {}
      const fg = winList.find((x) => x.fg)
      const pg = winList.find((x) => x.cls === 'Progman')
      try {
        // 通道1：UIA（最快；桌面图标 + 前台控件）
        const tU = Date.now()
        if (!args.ocrOnly) {
          const hwnds = pg ? [pg.hwnd, 0] : [0]
          for (const hw of hwnds) {
            try {
              const u = await helperCall('uia', hw === 0 ? {} : { hwnd: hw })
              for (const it of (u && u.items) || []) {
                if (String(it.text || '').replace(/\s+/g, '').includes(k)) {
                  push({ text: it.text, x: it.rect.x, y: it.rect.y, w: it.rect.w, h: it.rect.h, source: 'uia' })
                }
              }
            } catch {}
          }
        }
        stages.uia = Date.now() - tU
        // 通道2：OCR —— 只扫前台窗口区域（原来全屏 + scale=2 要 3s）
        const tO = Date.now()
        let rect = null
        if (!args.full && fg && fg.w >= 160 && fg.h >= 120) rect = { x: fg.x, y: fg.y, w: fg.w, h: fg.h }
        const effScale = pickScale(rect, scale)
        stages.scale = effScale
        const o = rect ? await helperCall('ocr', { x: rect.x, y: rect.y, w: rect.w, h: rect.h, scale: effScale }) : await helperCall('ocr', { scale: effScale })
        const dx = rect ? rect.x : 0, dy = rect ? rect.y : 0
        for (const it of (o && o.items) || []) {
          if (String(it.text || '').replace(/\s+/g, '').includes(k)) {
            push({ text: it.text, x: dx + it.rect.x, y: dy + it.rect.y, w: it.rect.w, h: it.rect.h, source: 'ocr' })
          }
        }
        stages.ocr = Date.now() - tO
        stages.region = rect
      } catch (e) {
        return { found: 0, keyword: kw, error: ((e && e.message) || String(e)).slice(0, 120), elapsedMs: Date.now() - t0 }
      }
      return { found: results.length, keyword: kw, items: results, elapsedMs: Date.now() - t0, budgetMs, stages }
    },
  })

  register({
    name: 'saperm_watch_status',
    description: '查看采集与运动状态：是否采集中、采集间隔、是否运动、运动区域（观察类，无需审批）',
    parameters: empty,
    execute: async () => {
      refreshAutoCleanup()
      const st = await helperCall('watch_status')
      return { ...(st || {}), linked: state.link, wake: state.linkWake }
    },
  })
  register({
    name: 'saperm_stop',
    description: '急停：立即锁定全部鼠标/键盘注入工具（锁操作永远可用，恢复才需用户确认）',
    parameters: empty,
    execute: async () => {
      state.paused = true
      state.pausedBy = 'tool'
      try { await helperCall('stop') } catch {}
      return { paused: true, note: '注入已锁定；识别观察不受影响' }
    },
  })

  register({
    name: 'saperm_resume',
    description: '恢复输入注入（每次调用都会弹出用户确认；AI 无法自行解除急停）',
    parameters: empty,
    execute: async () => {
      state.paused = false
      state.pausedBy = null
      try { await helperCall('resume') } catch (e) { throw new Error('helper 解锁失败：' + e.message) }
      return { paused: false }
    },
  })

  register({
    name: 'saperm_click',
    description: '点击屏幕坐标（L2 普通输入：本会话首次调用需用户授权，之后免审）。holdMs 可控制按住时长（默认 25ms，长按菜单/拖拽起点用）',
    parameters: {
      type: 'object',
      properties: {
        x: { type: 'integer', description: '屏幕 x 坐标' },
        y: { type: 'integer', description: '屏幕 y 坐标' },
        button: { type: 'string', description: 'left（默认）| right | middle' },
        holdMs: { type: 'integer', description: '按下到抬起的保持时间（默认 25，范围 0~10000）' },
      },
      required: ['x', 'y'],
    },
    execute: async (args) => {
      const r = await helperCall('click', { x: args.x, y: args.y, button: args.button || 'left', holdMs: clampInt(args.holdMs, 0, 0, 10000) })
      return { injected: true, helper: true, x: args.x, y: args.y, button: args.button || 'left', holdMs: (r && r.holdMs) ?? 25, clickMs: (r && r.clickMs) ?? null, verified: (r && r.verified) ?? null }
    },
  })

  register({
    name: 'saperm_press',
    description: '长按（按住不放）：target=mouse 长按鼠标按钮，target=key 长按按键（游戏移动/连续输入/长按菜单）。holdMs 默认 500，范围 10~10000。L2 输入，会话级授权',
    parameters: {
      type: 'object',
      properties: {
        target: { type: 'string', description: 'mouse（默认）| key' },
        holdMs: { type: 'integer', description: '按住时长毫秒（默认 500，范围 10~10000）' },
        x: { type: 'integer', description: 'target=mouse 时必填：屏幕 x' },
        y: { type: 'integer', description: 'target=mouse 时必填：屏幕 y' },
        button: { type: 'string', description: 'left（默认）| right | middle' },
        key: { type: 'string', description: 'target=key 时必填：按键名，如 SHIFT / A / F5 / LEFT' },
      },
    },
    execute: async (args) => {
      const target = String(args.target || 'mouse')
      const holdMs = clampInt(args.holdMs, 500, 10, 10000)
      if (target === 'key') {
        const key = String(args.key || 'SHIFT')
        const r = await helperCall('press', { target: 'key', key, holdMs })
        return { injected: true, helper: true, target: 'key', key, holdMs, elapsedMs: (r && r.elapsedMs) ?? null }
      }
      const x = Number(args.x), y = Number(args.y)
      if (!Number.isFinite(x) || !Number.isFinite(y)) throw new Error('target=mouse 需要 x 与 y 坐标')
      const button = String(args.button || 'left')
      const r2 = await helperCall('press', { target: 'mouse', x, y, button, holdMs })
      return { injected: true, helper: true, target: 'mouse', x, y, button, holdMs, elapsedMs: (r2 && r2.elapsedMs) ?? null }
    },
  })

  register({
    name: 'saperm_scroll',
    description: '滚轮滚动（L2 普通输入，会话级授权；正=上，负=下）。滚轮只作用于光标下方窗口，因此会自动先把光标移到前台窗口中心（或传入的 x,y），保证滚的是目标页面',
    parameters: {
      type: 'object',
      properties: {
        amount: { type: 'integer', description: '滚动量，正=上，负=下' },
        x: { type: 'integer', description: '可选：先移到该屏幕 x 再滚动' },
        y: { type: 'integer', description: '可选：先移到该屏幕 y 再滚动' },
      },
      required: ['amount'],
    },
    execute: async (args) => {
      const r = await helperCall('scroll', args.x != null && args.y != null
        ? { amount: args.amount, x: args.x, y: args.y }
        : { amount: args.amount })
      return { injected: true, helper: true, amount: args.amount, cursor: (r && r.cursor) || null }
    },
  })

  register({
    name: 'saperm_topmost',
    description: '临时把窗口抬到最高层（或复原）：当目标网页被最大化窗口/置顶窗口遮挡、导致截图截到别的窗口时使用。on=true 置顶并激活，on=false 复原。L2 输入，会话级授权',
    parameters: {
      type: 'object',
      properties: {
        hwnd: { type: 'integer', description: '目标窗口句柄' },
        on: { type: 'boolean', description: 'true=置顶（默认），false=取消置顶' },
      },
      required: ['hwnd'],
    },
    execute: async (args) => {
      const r = await helperCall('topmost', { hwnd: args.hwnd, on: args.on !== false })
      return { injected: true, helper: true, hwnd: args.hwnd, topmost: args.on !== false, ok: (r && r.ok) === true }
    },
  })

  register({
    name: 'saperm_type',
    description: '向当前焦点输入文本（L2 普通输入，会话级授权；Unicode 注入）',
    parameters: {
      type: 'object',
      properties: { text: { type: 'string', description: '要输入的文本' } },
      required: ['text'],
    },
    execute: async (args) => {
      await helperCall('type', { text: args.text })
      return { injected: true, helper: true, chars: args.text.length }
    },
  })

  register({
    name: 'saperm_drag',
    description: '从 (x1,y1) 拖拽到 (x2,y2)（L3 高危：本会话首次需授权，之后免审）',
    parameters: {
      type: 'object',
      properties: {
        x1: { type: 'integer' }, y1: { type: 'integer' },
        x2: { type: 'integer' }, y2: { type: 'integer' },
      },
      required: ['x1', 'y1', 'x2', 'y2'],
    },
    execute: async (args) => {
      await helperCall('drag', { x1: args.x1, y1: args.y1, x2: args.x2, y2: args.y2 })
      return { injected: true, helper: true, from: [args.x1, args.y1], to: [args.x2, args.y2] }
    },
  })

  register({
    name: 'saperm_doubleclick',
    description: '双击屏幕坐标（L3 高危：本会话首次需授权，之后免审）',
    parameters: point(),
    execute: async (args) => {
      await helperCall('doubleclick', { x: args.x, y: args.y })
      return { injected: true, helper: true, x: args.x, y: args.y }
    },
  })

  // ===== 状态机/录入自动化原语（hotkey / clipboard / focus / windows / recognize） =====
  register({
    name: 'saperm_hotkey',
    description: '发送组合键（L2 普通输入，会话级授权）。如 keys=["CTRL","C"]、["F5"]、["CTRL","SHIFT","S"]',
    parameters: {
      type: 'object',
      properties: { keys: { type: 'array', items: { type: 'string' }, description: '按键名数组（修饰键在前，主键最后）' } },
      required: ['keys'],
    },
    execute: async (args) => {
      await helperCall('hotkey', { keys: args.keys })
      return { injected: true, helper: true, keys: args.keys }
    },
  })

  register({
    name: 'saperm_clipboard',
    description: '剪贴板读写（L2，会话级授权）。action=get 读取文本；action=set 写入文本（可配合 hotkey Ctrl+C/V 精确回读）',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ['get', 'set'] },
        text: { type: 'string', description: 'action=set 时写入的文本' },
      },
      required: ['action'],
    },
    execute: async (args) => {
      if (args.action === 'set') {
        await helperCall('clipboard_set', { text: String(args.text == null ? '' : args.text) })
        return { ok: true, action: 'set', chars: String(args.text || '').length }
      }
      const r = await helperCall('clipboard_get', {})
      return { ok: true, action: 'get', text: (r && r.text) || '' }
    },
  })

  register({
    name: 'saperm_focus',
    description: '激活（置前）窗口（L2，会话级授权）。按 hwnd 或 title 子串定位；用于 Excel/资源管理器等跨窗口录入',
    parameters: {
      type: 'object',
      properties: {
        hwnd: { type: 'integer', description: '窗口句柄（可选）' },
        title: { type: 'string', description: '窗口标题子串（hwnd 为空时使用）' },
      },
    },
    execute: async (args) => {
      const a = {}
      if (args.hwnd) a.hwnd = args.hwnd
      else if (args.title) a.title = args.title
      const r = await helperCall('focus', a)
      return { done: !!(r && r.done), hwnd: r && r.hwnd, helper: true }
    },
  })

  register({
    name: 'saperm_windows',
    description: '列出可见窗口（标题/类名/矩形/pid/前台标志）。观察类，无需审批；用于找 Excel、资源管理器等目标窗口',
    parameters: empty,
    execute: async () => {
      const r = await helperCall('windows', {})
      return { windows: (r && r.windows) || [] }
    },
  })

  // ===== Excel 录入组合工具（F5 GoTo + 写入 + 剪贴板回读） =====
  async function excelFocus(args) {
    const a = {}
    if (args && args.hwnd) a.hwnd = args.hwnd
    else if (args && args.title) a.title = args.title
    else a.title = 'Excel'
    const r = await helperCall('focus', a)
    if (!r || !r.done) throw new Error('无法激活 Excel 窗口: ' + JSON.stringify(r && (r.error || r)))
    return r.hwnd
  }
  async function excelGoto(ref) {
    await helperCall('hotkey', { keys: ['F5'] })
    await new Promise(r => setTimeout(r, 700))
    await helperCall('type', { text: String(ref) })
    await new Promise(r => setTimeout(r, 300))
    await helperCall('key', { name: 'ENTER' })
    await new Promise(r => setTimeout(r, 700))
  }

  register({
    name: 'saperm_excel_goto',
    description: 'Excel 单元格定位（L2）：激活 Excel → F5 → 输入引用（如 "Sheet1!B5"）→ Enter。先 saperm_focus 或传 title/hwnd',
    parameters: {
      type: 'object',
      properties: {
        ref: { type: 'string', description: '单元格引用，如 Sheet1!B5 或 B5' },
        hwnd: { type: 'integer' }, title: { type: 'string' },
      },
      required: ['ref'],
    },
    execute: async (args) => {
      const hwnd = await excelFocus(args)
      await excelGoto(args.ref)
      return { done: true, ref: args.ref, hwnd }
    },
  })

  register({
    name: 'saperm_excel_write',
    description: 'Excel 写单元格（L2）：激活 → GoTo 引用 → 输入值（多行/超长自动剪贴板粘贴）→ 可选回读校验',
    parameters: {
      type: 'object',
      properties: {
        ref: { type: 'string' },
        value: { type: 'string' },
        verify: { type: 'boolean', description: '写后 Ctrl+C 回读校验（默认 true）' },
        hwnd: { type: 'integer' }, title: { type: 'string' },
      },
      required: ['ref', 'value'],
    },
    execute: async (args) => {
      const hwnd = await excelFocus(args)
      await excelGoto(args.ref)
      const v = String(args.value == null ? '' : args.value)
      if (v.indexOf('\n') >= 0 || v.length > 200) {
        await helperCall('clipboard_set', { text: v })
        await helperCall('hotkey', { keys: ['CTRL', 'V'] })
      } else {
        await helperCall('type', { text: v })
      }
      await new Promise(r => setTimeout(r, 400))
      await helperCall('key', { name: 'ENTER' })
      await new Promise(r => setTimeout(r, 400))
      let readback = null
      if (args.verify !== false) {
        try {
          await excelGoto(args.ref)
          await helperCall('hotkey', { keys: ['CTRL', 'C'] })
          await new Promise(r => setTimeout(r, 700))
          const cg = await helperCall('clipboard_get', {})
          readback = String((cg && cg.text) || '')
          while (readback.endsWith(String.fromCharCode(10)) || readback.endsWith(String.fromCharCode(13))) readback = readback.slice(0, -1)
        } catch { }
      }
      return { done: true, ref: args.ref, written: v.length, readback, verified: readback === null ? null : readback === v }
    },
  })

  register({
    name: 'saperm_excel_read',
    description: 'Excel 读单元格（L2）：激活 → GoTo 引用 → Ctrl+C → 读剪贴板返回文本。用于回读验证',
    parameters: {
      type: 'object',
      properties: { ref: { type: 'string' }, hwnd: { type: 'integer' }, title: { type: 'string' } },
      required: ['ref'],
    },
    execute: async (args) => {
      const hwnd = await excelFocus(args)
      await excelGoto(args.ref)
      await helperCall('hotkey', { keys: ['CTRL', 'C'] })
      await new Promise(r => setTimeout(r, 700))
      const cg = await helperCall('clipboard_get', {})
      return { done: true, ref: args.ref, text: String((cg && cg.text) || ''), hwnd }
    },
  })

  register({
    name: 'saperm_excel_cells',
    description: 'Excel 可见单元格网格（UIA DataItem，L0 观察）：返回单元格名+屏幕坐标，自动过滤虚拟化的离屏假坐标',
    parameters: {
      type: 'object',
      properties: { hwnd: { type: 'integer' }, maxCells: { type: 'integer' } },
    },
    execute: async (args) => {
      const r = await helperCall('uia', args.hwnd ? { hwnd: args.hwnd } : {})
      const all = (r && r.items) || []
      const cells = all.filter(x => /^[A-Z]{1,3}[0-9]+$/.test(String(x.text || ''))
        && x.rect && x.rect.x >= 0 && x.rect.y >= 0 && x.rect.w > 0 && x.rect.h > 0)
        .map(x => ({ cell: x.text, x: x.rect.x, y: x.rect.y, w: x.rect.w, h: x.rect.h }))
      return { cells: cells.slice(0, args.maxCells || 100), total: cells.length }
    },
  })

  register({
    name: 'saperm_recognize',
    description: '统一识别：一次返回文字+坐标（PaddleOCR）与控件元素+坐标（UIA 前台窗口）。观察类，无需审批',
    parameters: {
      type: 'object',
      properties: {
        maxTexts: { type: 'integer', description: '最多返回文字条数（默认 60）' },
        maxElements: { type: 'integer', description: '最多返回 UIA 元素条数（默认 40）' },
        full: { type: 'boolean', description: 'true=全屏 OCR；默认只扫前台窗口（快 3~10 倍）' },
        x: { type: 'integer' }, y: { type: 'integer' }, w: { type: 'integer' }, h: { type: 'integer' },
      },
    },
    execute: async (args) => {
      const t0 = Date.now()
      const out = { texts: [], elements: [], stages: {} }
      let rect = null
      try {
        if (args.x != null) rect = { x: args.x, y: args.y, w: args.w, h: args.h }
        else if (!args.full) {
          const w = await helperCall('windows')
          const fg = ((w && w.windows) || []).find((x) => x.fg)
          if (fg && fg.w >= 160 && fg.h >= 120) rect = { x: fg.x, y: fg.y, w: fg.w, h: fg.h }
        }
      } catch {}
      try {
        const tO = Date.now()
        const effScale = pickScale(rect, undefined)
        const o = rect ? await helperCall('ocr', { ...rect, scale: effScale }) : await helperCall('ocr', { scale: effScale })
        out.stages.scale = effScale
        const dx = rect ? rect.x : 0, dy = rect ? rect.y : 0
        out.texts = ((o && o.items) || []).slice(0, args.maxTexts || 60).map(h => ({ text: h.text, x: dx + h.rect.x, y: dy + h.rect.y, w: h.rect.w, h: h.rect.h }))
        out.stages.ocr = Date.now() - tO
      } catch (e) { out.ocrError = ((e && e.message) || String(e)).slice(0, 100) }
      try {
        const tU = Date.now()
        const u = await helperCall('uia', {})
        out.elements = ((u && u.items) || []).filter(x => x.text).slice(0, args.maxElements || 40)
          .map(x => ({ text: x.text, role: x.role, x: x.rect.x, y: x.rect.y, w: x.rect.w, h: x.rect.h }))
        out.stages.uia = Date.now() - tU
      } catch (e) { out.uiaError = ((e && e.message) || String(e)).slice(0, 100) }
      out.region = rect
      out.elapsedMs = Date.now() - t0
      return out
    },
  })

  register({
    name: 'saperm_frames',
    description: '连续帧流水线：以目标帧率（默认 60fps）持续采集屏幕，逐帧记录时间戳/采集耗时/空洞(gap)，保证信息流不丢帧（60fps × 60s = 3600 帧）。action=start 开始、stats 查看、stop 结束并返回统计。观察类，无需审批',
    parameters: {
      type: 'object',
      properties: {
        action: { type: 'string', description: 'start | stats | stop（默认 stats）' },
        fps: { type: 'integer', description: '目标帧率（默认 60，范围 1~240）' },
        path: { type: 'string', description: '可选：逐帧日志路径（默认 out/logs/frames-<时间>.jsonl）' },
      },
    },
    execute: async (args) => {
      const action = String(args.action || 'stats')
      if (action === 'start') {
        const fps = clampInt(args.fps, 60, 1, 240)
        try { mkdirSync(LOGS_DIR, { recursive: true }) } catch {}
        const path = args.path || join(LOGS_DIR, 'frames-' + Date.now() + '.jsonl')
        const r = await helperCall('frames_start', { fps, path })
        return { ...(r || {}), action, fps, path, hint: '用 action=stats 查看进度，action=stop 结束' }
      }
      if (action === 'stop') {
        const r = await helperCall('frames_stop')
        return { ...(r || {}), action }
      }
      const s = await helperCall('frames_stats')
      return { ...(s || {}), action: 'stats' }
    },
  })

  register({
    name: 'saperm_scan',
    description: '一次识别调用完成多路感知（2s 预算内）：只截一张图 → OCR 文字+坐标，可选 scene 场景 / objects 物体，返回合并结果与分阶段耗时 elapsedMs/withinBudget。默认扫前台窗口（快 3~10 倍），full=true 全屏。观察类，无需审批',
    parameters: {
      type: 'object',
      properties: {
        x: { type: 'integer' }, y: { type: 'integer' }, w: { type: 'integer' }, h: { type: 'integer' },
        full: { type: 'boolean', description: 'true=整屏识别；默认只扫前台窗口' },
        scene: { type: 'boolean', description: '附带 CLIP 场景分类（约 +50ms）' },
        objects: { type: 'boolean', description: '附带 YOLO 物体检测（约 +100~500ms）' },
        maxTexts: { type: 'integer', description: '返回文字条数（默认 80，最大 200）' },
        scale: { type: 'number', description: 'OCR 放大倍数（默认 1）' },
      },
    },
    execute: async (args) => {
      const t0 = Date.now()
      const budget = 1900
      const stages = {}
      let rect = null
      if (args.x != null) rect = { x: args.x, y: args.y, w: args.w, h: args.h }
      else if (!args.full) {
        try {
          const w = await helperCall('windows')
          const fg = ((w && w.windows) || []).find((x) => x.fg)
          if (fg && fg.w >= 160 && fg.h >= 120) rect = { x: fg.x, y: fg.y, w: fg.w, h: fg.h }
        } catch {}
      }
      const tShot = Date.now()
      const shot = await helperCall('screenshot', rect ? { ...rect } : {})
      const path = saveShot(shot.jpegB64, 'scan')
      stages.shot = Date.now() - tShot
      const tOcr = Date.now()
      let texts = []
      try {
        const effScale = pickScale(rect, args.scale)
        const o = await helperCall('ocr_file', { path, scale: effScale })
        stages.scale = effScale
        const dx = rect ? rect.x : 0, dy = rect ? rect.y : 0
        texts = ((o && o.items) || []).slice(0, clampInt(args.maxTexts, 80, 1, 200))
          .map((it) => ({ text: it.text, x: dx + (it.x || 0), y: dy + (it.y || 0), w: it.w || 0, h: it.h || 0 }))
      } catch (e) { stages.ocrError = ((e && e.message) || String(e)).slice(0, 80) }
      stages.ocr = Date.now() - tOcr
      let scene = null, objects = null
      if (args.scene) {
        const t = Date.now()
        try { const r = await helperCall('scene', { path, topK: 3 }); scene = { top: r.top, labels: r.labels } } catch {}
        stages.scene = Date.now() - t
      }
      if (args.objects) {
        const t = Date.now()
        try { const r = await helperCall('detect', { path, model: 'n' }); objects = { count: r.count, items: (r.items || []).slice(0, 10) } } catch {}
        stages.detect = Date.now() - t
      }
      const elapsedMs = Date.now() - t0
      return { elapsedMs, withinBudget: elapsedMs < budget, budgetMs: budget, region: rect, path, count: texts.length, texts, scene, objects, stages }
    },
  })

  register({
    name: 'saperm_ocr_file',
    description: '对图片文件做 PaddleOCR（本地，中文/数字）：返回文字+坐标。用于从文件夹数据图片提取数据。L0 观察，无需审批',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: '图片文件绝对路径' },
        scale: { type: 'number', description: '放大倍数（小字用 2，默认 1）' },
      },
      required: ['path'],
    },
    execute: async (args) => {
      const r = await helperCall('ocr_file', { path: args.path, scale: args.scale || 1 })
      return { file: args.path, imageW: r.imageW, imageH: r.imageH, ms: r.ms, items: (r.items || []).map(h => ({ text: h.text, x: h.rect.x, y: h.rect.y, w: h.rect.w, h: h.rect.h, score: h.score })) }
    },
  })

  register({
    name: 'saperm_fill_start',
    description: '创建填表任务：递归扫描文件夹图片，生成 checkpoint（out/fill/<id>.json）。columns 指定要填的列，startRow 默认 2。L1 观察（读盘）',
    parameters: {
      type: 'object',
      properties: {
        sourceDir: { type: 'string', description: '数据图片文件夹（可含子目录）' },
        columns: { type: 'array', items: { type: 'string' }, description: '要填写的字段/列名' },
        excelPath: { type: 'string', description: '目标 Excel 路径（记录用，可空）' },
        sheet: { type: 'string', description: '工作表名（可空）' },
        startRow: { type: 'integer', description: '起始行（默认 2）' },
      },
      required: ['sourceDir'],
    },
    execute: async (args) => {
      const dir = args.sourceDir
      if (!existsSync(dir)) throw new Error('目录不存在: ' + dir)
      const files = walkImages(dir).sort()
      const job = {
        id: 'fill-' + Date.now().toString(36),
        createdAt: new Date().toISOString(),
        sourceDir: dir,
        columns: args.columns || [],
        excelPath: args.excelPath || '',
        sheet: args.sheet || '',
        nextRow: args.startRow || 2,
        files: files.map((fp, i) => ({ id: 'img' + (i + 1), path: fp, status: 'pending', fields: null, row: null })),
      }
      fillSave(job)
      return { job: job.id, total: files.length, columns: job.columns, nextRow: job.nextRow, files: files.slice(0, 5) }
    },
  })

  register({
    name: 'saperm_fill_status',
    description: '查看填表任务进度（pending/extracted/written/verified/failed + nextRow）。L0 观察',
    parameters: { type: 'object', properties: { job: { type: 'string' } } },
    execute: async (args) => {
      const job = fillLoad(fillResolve(args.job))
      const stats = { pending: 0, extracted: 0, written: 0, verified: 0, failed: 0 }
      for (const f of job.files) stats[f.status] = (stats[f.status] || 0) + 1
      return { job: job.id, total: job.files.length, stats, nextRow: job.nextRow, columns: job.columns, done: stats.pending === 0 && stats.extracted === 0 }
    },
  })

  register({
    name: 'saperm_fill_next',
    description: '取下一张待提取图片（本地路径），供 AI 提取字段后 saperm_fill_submit。L0 观察',
    parameters: { type: 'object', properties: { job: { type: 'string' } } },
    execute: async (args) => {
      const job = fillLoad(fillResolve(args.job))
      const f = job.files.find(x => x.status === 'pending')
      if (!f) return { job: job.id, done: true, message: '没有待提取图片' }
      return { job: job.id, imageId: f.id, path: f.path, index: job.files.indexOf(f) + 1, total: job.files.length }
    },
  })

  register({
    name: 'saperm_fill_submit',
    description: '提交某张图片的提取字段（{字段名:值}），标记 extracted。L0（只改 checkpoint）',
    parameters: {
      type: 'object',
      properties: {
        job: { type: 'string' },
        imageId: { type: 'string' },
        fields: { type: 'object', description: '字段对象' },
      },
      required: ['imageId', 'fields'],
    },
    execute: async (args) => {
      const job = fillLoad(fillResolve(args.job))
      const f = job.files.find(x => x.id === args.imageId)
      if (!f) throw new Error('图片不存在: ' + args.imageId)
      f.fields = args.fields || {}
      f.status = 'extracted'
      fillSave(job)
      return { job: job.id, imageId: f.id, status: 'extracted' }
    },
  })

  register({
    name: 'saperm_fill_write',
    description: '把已提取的图片写入 Excel（每图一行，按 columns 顺序 TSV 粘贴到 A{row}，写后回读校验）。L2（写单元格）',
    parameters: {
      type: 'object',
      properties: {
        job: { type: 'string' },
        imageId: { type: 'string', description: '只写这一张（可选；缺省写全部 extracted）' },
        hwnd: { type: 'integer' }, title: { type: 'string' },
        verify: { type: 'boolean', description: '回读校验（默认 true）' },
      },
    },
    execute: async (args) => {
      const job = fillLoad(fillResolve(args.job))
      if (!job.columns.length) throw new Error('任务未定义 columns，无法写表')
      const targets = args.imageId
        ? job.files.filter(x => x.id === args.imageId)
        : job.files.filter(x => x.status === 'extracted')
      if (!targets.length) return { job: job.id, written: [], message: '没有待写入的 extracted 图片' }
      const hwnd = await excelFocus(args)
      const results = []
      for (const f of targets) {
        const row = f.row || job.nextRow
        const vals = job.columns.map(c => {
          const v = f.fields ? f.fields[c] : ''
          return v == null ? '' : String(v)
        })
        const ref = (job.sheet ? job.sheet + '!' : '') + 'A' + row
        await excelGoto(ref)
        await helperCall('clipboard_set', { text: vals.join(String.fromCharCode(9)) })
        await helperCall('hotkey', { keys: ['CTRL', 'V'] })
        await sleepMs(500)
        let readback = null
        if (args.verify !== false) {
          try {
            const lastCol = colLetter(job.columns.length)
            const rangeRef = (job.sheet ? job.sheet + '!' : '') + 'A' + row + ':' + lastCol + row
            await excelGoto(rangeRef)
            await helperCall('hotkey', { keys: ['CTRL', 'C'] })
            await sleepMs(700)
            const cg = await helperCall('clipboard_get', {})
            readback = String((cg && cg.text) || '')
            while (readback.endsWith(String.fromCharCode(10)) || readback.endsWith(String.fromCharCode(13))) readback = readback.slice(0, -1)
          } catch { }
        }
        const expected = vals.join(String.fromCharCode(9))
        const ok = readback === null ? null : readback === expected
        f.row = row
        f.status = ok === false ? 'written' : 'verified'
        job.nextRow = row + 1
        results.push({ imageId: f.id, row, values: vals, readback, verified: ok })
      }
      fillSave(job)
      return { job: job.id, hwnd, written: results }
    },
  })

  register({
    name: 'saperm_fill_abort',
    description: '删除填表任务 checkpoint（不删除图片/Excel）。L0',
    parameters: { type: 'object', properties: { job: { type: 'string' } } },
    execute: async (args) => {
      const id = fillResolve(args.job)
      try { unlinkSync(fillJobFile(id)) } catch { }
      return { job: id, aborted: true }
    },
  })

  // ===== 连续解读（持续获取 + 变化区域 OCR + 内容/噪声分类） =====
  register({
    name: 'saperm_interpret_start',
    description: '启动连续解读：持续采集屏幕，自动检测变化区域并 OCR，分类"内容变化/动态噪声"，维护滚动历史。inject=true 时把"内容变化"事件按 minInjectMs 节流注入当前会话（≤200 字/条，不刷屏）。L0 观察，无需审批',
    parameters: {
      type: 'object',
      properties: {
        intervalMs: { type: 'integer', description: '解读间隔毫秒（默认 800，最小 100）' },
        x: { type: 'integer' }, y: { type: 'integer' },
        w: { type: 'integer' }, h: { type: 'integer' },
        inject: { type: 'boolean', description: '是否把变化事件注入会话（默认 false）' },
        minInjectMs: { type: 'integer', description: '注入最小间隔毫秒（默认 2000，范围 200-60000）' },
        kinds: { type: 'array', items: { type: 'string' }, description: '注入的变化类型（默认 ["content_change"]，可选 content_change/text_shift/dynamic_noise/idle）' },
        ttlSec: { type: 'integer', description: '无操作自动关闭秒数（默认 120 秒，到期自动停止观察并关闭小窗）' },
      },
    },
    execute: async (args) => {
      const a = { intervalMs: args.intervalMs || 800 }
      if (args.x != null && args.y != null && args.w != null && args.h != null) { a.x = args.x; a.y = args.y; a.w = args.w; a.h = args.h }
      let autoStopped = null
      if (state.obsMode === 'infer') {
        try { stopInject('infer'); await helperCall('infer_stop', {}); releaseHold('obs'); autoStopped = 'infer' } catch {}
      }
      armAutoCleanup(args.ttlSec || 120)
      const r = await helperCall('interpret_start', a)
      if (r && r.running) {
        holdHelper('obs')
        state.obsMode = 'interpret'
        state.obsConfig = { mode: 'interpret', cmd: 'interpret_start', args: a }
      }
      const c = startInterpretInject({ inject: args.inject, minInjectMs: args.minInjectMs, kinds: args.kinds })
      return autoStopped ? { ...r, autoStopped, injection: injectSummary(c) } : { ...r, injection: injectSummary(c) }
    },
  })

  register({
    name: 'saperm_interpret_status',
    description: '读取连续解读状态：运行中/解读次数/平均 OCR 耗时/当前变化类型/滚动历史（内容变化 vs 动态噪声）+ 当前注入配置。L0 观察',
    parameters: {
      type: 'object',
      properties: { limit: { type: 'integer', description: '返回历史条数（默认 20）' } },
    },
    execute: async (args) => {
      refreshAutoCleanup()
      const r = await helperCall('interpret_status', { limit: args.limit || 20 })
      return { ...r, injection: injectSummary(state.interpretInject) }
    },
  })

  register({
    name: 'saperm_interpret_stop',
    description: '停止连续解读（同时清理注入轮询定时器）。L0 观察，无需审批',
    parameters: {
      type: 'object',
      properties: {
        keepOverlay: { type: 'boolean', description: '是否保留注意力小窗（默认 false 自动关闭）' },
      },
    },
    execute: async (args) => {
      stopInject('interpret')
      state.obsConfig = null
      const r = await helperCall('interpret_stop', {})
      releaseHold('obs')
      state.obsMode = null
      let overlayClosed = false
      if (!args || !args.keepOverlay) {
        try { await helperCall('overlay_close', {}) } catch {}
        if (state.autoWatch) { state.autoWatch = false; try { await helperCall('watch_stop', {}) } catch {}; releaseHold('watch') }
        state.overlayOpen = false
        overlayClosed = true
      }
      clearAutoCleanupIfIdle()
      return { ...r, injection: injectSummary(state.interpretInject), ...(overlayClosed ? { overlayClosed: true } : {}) }
    },
  })

  // ===== 实时逐帧识别（每帧都送识别，结果流式输出） =====
  register({
    name: 'saperm_infer_start',
    description: '启动逐帧实时识别：每一帧都做检测+识别（rec 缓存复用只重识别变化框），结果流式输出。inject=true 时把角色出现/消失事件按 minInjectMs 节流注入当前会话（需 objects=true）。L0 观察，无需审批',
    parameters: {
      type: 'object',
      properties: {
        maxBoxes: { type: 'integer', description: '每帧最多识别框数（默认 60）' },
        objects: { type: 'boolean', description: '逐帧目标检测（物体描述，YOLOv8n）' },
        scene: { type: 'boolean', description: '逐帧场景语义分类（Chinese-CLIP，ACG 标签）' },
        accurate: { type: 'boolean', description: '精确物体检测（YOLOv8m，更准但稍慢）' },
        animeface: { type: 'boolean', description: '逐帧动漫脸检测（YOLOv8n face，ACG 场景用，~65ms/帧）' },
        sceneEvery: { type: 'integer', description: '场景语义采样间隔帧数（默认 8，0/省略=8）' },
        faceEvery: { type: 'integer', description: '动漫脸采样间隔帧数（默认 4）' },
        inject: { type: 'boolean', description: '是否把角色出现/消失事件注入会话（默认 false）' },
        injectChanges: { type: 'boolean', description: '注入角色出现/消失变化（默认 true；false=不注入变化）' },
        minInjectMs: { type: 'integer', description: '注入最小间隔毫秒（默认 2000，范围 200-60000）' },
        ttlSec: { type: 'integer', description: '无操作自动关闭秒数（默认 120 秒，到期自动停止观察并关闭小窗）' },
        streamMs: { type: 'integer', description: 'AI 可读数据流采样间隔毫秒（默认 1000，范围 200-10000）' },
        fullRate: { type: 'boolean', description: 'true=不做静止节流，每帧都识别（追 30fps 高速识别）；默认 false 省电' },
      },
    },
    execute: async (args) => {
      let autoStopped = null
      if (state.obsMode === 'interpret') {
        try { stopInject('interpret'); await helperCall('interpret_stop', {}); releaseHold('obs'); autoStopped = 'interpret' } catch {}
      }
      armAutoCleanup(args.ttlSec || 120)
      const inferArgs = { maxBoxes: args.maxBoxes || 60, objects: !!args.objects, scene: !!args.scene, accurate: !!args.accurate, animeface: !!args.animeface, sceneEvery: args.sceneEvery || 8, faceEvery: args.faceEvery || 4, fullRate: args.fullRate === true }
      const r = await helperCall('infer_start', inferArgs)
      if (r && r.running) {
        holdHelper('obs')
        state.obsMode = 'infer'
        state.obsConfig = { mode: 'infer', cmd: 'infer_start', args: inferArgs }
      }
      const c = startInferInject({ inject: args.inject, injectChanges: args.injectChanges, minInjectMs: args.minInjectMs })
      if (r && r.running) startStream({ intervalMs: args.streamMs || 1000 })
      const out = { ...r, injection: injectSummary(c), stream: streamStatus() }
      if (autoStopped) out.autoStopped = autoStopped
      if (c.inject && !args.objects) out.injectionNote = '角色变化依赖 objects=true 的目标检测/身份；当前 objects=false 时不会产生出现/消失事件'
      return out
    },
  })

  register({
    name: 'saperm_infer_status',
    description: '读取逐帧识别状态：fps/已处理帧/丢弃帧/OCR耗时P50-P90/最新文本/rec缓存命中 + 当前注入配置。L0 观察',
    parameters: {
      type: 'object',
      properties: { limit: { type: 'integer', description: '返回历史条数（默认 10）' } },
    },
    execute: async (args) => {
      refreshAutoCleanup()
      const r = await helperCall('infer_status', { limit: args.limit || 10 })
      return { ...r, injection: injectSummary(state.inferInject), stream: streamStatus() }
    },
  })

  register({
    name: 'saperm_stream',
    description: '读取 AI 可读数据流：每帧识别结果已压成一行紧凑文本（W=前台窗口 SC=场景 OB=物体 F=动漫脸 TX=文本,增量语义 =相同/\u002B新增 CH=变化事件 fps/ms）。默认返回 text 多行，token 极省；file=true 返回 latest.txt 路径（可用 read 工具分页读）。L0 观察',
    parameters: {
      type: 'object',
      properties: {
        limit: { type: 'integer', description: '返回最近行数（默认 40，最大 600）' },
        sinceSeq: { type: 'integer', description: '只返回帧号 seq 大于该值的行（增量拉取）' },
        format: { type: 'string', description: 'lines=紧凑文本(默认) | json=结构化数组' },
        file: { type: 'boolean', description: 'true=只返回 latest.txt 路径与状态' },
      },
    },
    execute: async (args) => {
      const limit = clampInt(args.limit, 40, 1, 600)
      if (args.file) return { file: streamLatest(), status: streamStatus() }
      const st = state.stream
      let rows = []
      if (st && st.ring.length) rows = st.ring.slice()
      else {
        try {
          const f = streamFile(streamDay(Date.now()))
          if (existsSync(f)) {
            const lines = readFileSync(f, 'utf8').split('\n').filter(Boolean)
            for (const ln of lines.slice(-limit)) { try { rows.push(JSON.parse(ln)) } catch {} }
          }
        } catch {}
      }
      if (args.sinceSeq) rows = rows.filter((r) => Number(r.s) > Number(args.sinceSeq))
      rows = rows.slice(-limit)
      if (String(args.format || 'lines') === 'json') return { count: rows.length, status: streamStatus(), rows }
      return { count: rows.length, status: streamStatus(), text: rows.map(formatStreamLine).join('\n') }
    },
  })

  register({
    name: 'saperm_stream_start',
    description: '单独开启 AI 可读数据流（需 infer 在跑才有数据）。默认 1000ms 采样，带 ttlSec 时到期自动停止。L0 观察',
    parameters: {
      type: 'object',
      properties: {
        intervalMs: { type: 'integer', description: '采样间隔毫秒（默认 1000，范围 200-10000）' },
        ttlSec: { type: 'integer', description: '无操作自动停止秒数（可选）' },
      },
    },
    execute: async (args) => {
      const st = startStream({ intervalMs: args.intervalMs || 1000 })
      if (args.ttlSec) { try { armAutoCleanup(args.ttlSec) } catch {} }
      return { running: !!st.timer, intervalMs: st.intervalMs, file: streamLatest() }
    },
  })

  register({
    name: 'saperm_stream_stop',
    description: '停止 AI 可读数据流（saperm_infer_stop 会自动停止，通常无需手动调用）。L0 观察',
    parameters: empty,
    execute: async () => {
      const summary = stopStream()
      return { stopped: true, ...(summary || {}), file: streamLatest() }
    },
  })

  register({
    name: 'saperm_infer_stop',
    description: '停止逐帧实时识别（同时清理注入轮询定时器）。L0 观察，无需审批',
    parameters: {
      type: 'object',
      properties: {
        keepOverlay: { type: 'boolean', description: '是否保留注意力小窗（默认 false 自动关闭）' },
      },
    },
    execute: async (args) => {
      stopInject('infer')
      stopStream()
      state.obsConfig = null
      const r = await helperCall('infer_stop', {})
      releaseHold('obs')
      state.obsMode = null
      let overlayClosed = false
      if (!args || !args.keepOverlay) {
        try { await helperCall('overlay_close', {}) } catch {}
        if (state.autoWatch) { state.autoWatch = false; try { await helperCall('watch_stop', {}) } catch {}; releaseHold('watch') }
        state.overlayOpen = false
        overlayClosed = true
      }
      clearAutoCleanupIfIdle()
      return { ...r, injection: injectSummary(state.inferInject), ...(overlayClosed ? { overlayClosed: true } : {}) }
    },
  })

  register({
    name: 'saperm_detect',
    description: '目标检测（YOLOv8n，本地）：返回画面中的物体（中英文标签+置信度+坐标）。不传 path=当前屏幕；传 path=图片文件。L0 观察，无需审批',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: '图片文件路径（可选，缺省=当前屏幕）' },
        model: { type: 'string', description: '检测模型：n=快速(默认) / m=精确' },
      },
    },
    execute: async (args) => {
      const r = await helperCall('detect', Object.assign(args.path ? { path: args.path } : {}, args.model ? { model: args.model } : {}))
      return { count: r.count, summary: r.summary, items: r.items, ms: r.ms, backend: r.backend, engine: r.engine, rect: r.rect }
    },
  })

  register({
    name: 'saperm_scene',
    description: '场景语义分类（Chinese-CLIP 零样本，含 ACG 标签：二次元游戏/视觉小说/VTuber/动漫等）：返回 top 标签+分数。不传 path=当前屏幕。L0 观察',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: '图片路径（可选，缺省=当前屏幕）' },
        topK: { type: 'integer', description: '返回标签数（默认 5）' },
      },
    },
    execute: async (args) => {
      const r = await helperCall('scene', { path: args.path, topK: args.topK || 5 })
      return { top: r.top, labels: r.labels, ms: r.ms, backend: r.backend, rect: r.rect }
    },
  })

  register({
    name: 'saperm_char_register',
    description: '注册人物/角色（CLIP 裁剪嵌入，用于识别 Harness 中的人物）：不传区域则自动检测最大人物框。L0 观察，无需审批',
    parameters: {
      type: 'object',
      properties: {
        name: { type: 'string', description: '角色名（如 harness女仆）' },
        note: { type: 'string', description: '角色知识说明（可选，识别人物时一并返回）' },
        path: { type: 'string', description: '图片路径（可选）' },
        x: { type: 'integer' }, y: { type: 'integer' },
        w: { type: 'integer' }, h: { type: 'integer' },
      },
      required: ['name'],
    },
    execute: async (args) => {
      const a = { name: args.name }
      if (args.note) a.note = args.note
      if (args.path) a.path = args.path
      if (args.x != null) { a.x = args.x; a.y = args.y; a.w = args.w; a.h = args.h }
      return await helperCall('char_register', a)
    },
  })

  register({
    name: 'saperm_char_match',
    description: '识别人物/角色：检测画面中的人物并匹配已注册角色（返回角色名+相似度+框）。L0 观察',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: '图片路径（可选，缺省=当前屏幕）' },
        x: { type: 'integer' }, y: { type: 'integer' },
        w: { type: 'integer' }, h: { type: 'integer' },
      },
    },
    execute: async (args) => {
      const a = {}
      if (args.path) a.path = args.path
      if (args.x != null) { a.x = args.x; a.y = args.y; a.w = args.w; a.h = args.h }
      return await helperCall('char_match', a)
    },
  })

  register({
    name: 'saperm_char_list',
    description: '列出已注册的角色（名称/维度）。L0 观察',
    parameters: empty,
    execute: async () => await helperCall('char_list', {}),
  })

  register({
    name: 'saperm_char_delete',
    description: '删除已注册角色。L0（只改注册表）',
    parameters: {
      type: 'object',
      properties: { name: { type: 'string' } },
      required: ['name'],
    },
    execute: async (args) => await helperCall('char_delete', { name: args.name }),
  })

  register({
    name: 'saperm_char_rename',
    description: '重命名已注册角色。L0（只改注册表）',
    parameters: {
      type: 'object',
      properties: { from: { type: 'string' }, to: { type: 'string' } },
      required: ['from', 'to'],
    },
    execute: async (args) => await helperCall('char_rename', { from: args.from, to: args.to }),
  })

  register({
    name: 'saperm_animeface',
    description: 'ACG 动漫脸检测（YOLOv8n face，本地）：返回画面中动漫脸的框+置信度。不传 path=当前屏幕。L0 观察，无需审批',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: '图片路径（可选，缺省=当前屏幕）' },
        conf: { type: 'number', description: '置信度阈值（默认 0.25）' },
        maxFaces: { type: 'integer', description: '最多返回人脸数（默认 20）' },
      },
    },
    execute: async (args) => {
      const a = {}
      if (args.path) a.path = args.path
      if (args.conf != null) a.conf = args.conf
      if (args.maxFaces != null) a.maxFaces = args.maxFaces
      const r = await helperCall('animeface', a)
      return { count: r.count, faces: r.faces, ms: r.ms, backend: r.backend, engine: r.engine, rect: r.rect }
    },
  })

  register({
    name: 'saperm_char_merge',
    description: '合并两个角色的参考嵌入（同一角色的多份注册合并为一个名字，匹配取全部参考最大值）。L0（只改注册表）',
    parameters: {
      type: 'object',
      properties: { from: { type: 'string' }, to: { type: 'string' } },
      required: ['from', 'to'],
    },
    execute: async (args) => await helperCall('char_merge', { from: args.from, to: args.to }),
  })

  register({
    name: 'saperm_char_unknowns',
    description: '列出未知角色聚类（自动登记但未命名的角色：id/参考数/出现次数/首次末次时间）。L0 观察',
    parameters: empty,
    execute: async () => await helperCall('char_unknowns', {}),
  })

  register({
    name: 'saperm_char_promote',
    description: '把未知角色聚类提升为具名角色（转移其参考嵌入到已有/新名字）。L0（只改注册表）',
    parameters: {
      type: 'object',
      properties: { unknown: { type: 'string', description: '未知角色 id（如 未知角色#1）' }, name: { type: 'string', description: '正式角色名' } },
      required: ['unknown', 'name'],
    },
    execute: async (args) => await helperCall('char_promote', { unknown: args.unknown, name: args.name }),
  })

  register({
    name: 'saperm_char_ignore',
    description: '忽略/删除未知角色聚类（误检或不再关心）。L0（只改注册表）',
    parameters: {
      type: 'object',
      properties: { id: { type: 'string' } },
      required: ['id'],
    },
    execute: async (args) => await helperCall('char_ignore', { unknown: args.id, targetId: args.id }),
  })

  register({
    name: 'saperm_char_note',
    description: '设置/更新角色知识备注（识别人物时随 note 返回）。L0（只改知识文件）',
    parameters: {
      type: 'object',
      properties: { name: { type: 'string' }, note: { type: 'string' } },
      required: ['name', 'note'],
    },
    execute: async (args) => await helperCall('char_note', { name: args.name, note: args.note }),
  })

  register({
    name: 'saperm_history',
    description: '读取观察审计历史日志（持久化落盘 JSONL，支持跨重启追溯）。L0 观察',
    parameters: {
      type: 'object',
      properties: {
        date: { type: 'string', description: '日期 YYYY-MM-DD（默认今天）' },
        limit: { type: 'integer', description: '返回最新记录条数（默认 30）' },
        type: { type: 'string', description: '过滤类型（inject / interpret / infer）' },
      },
    },
    execute: async (args) => {
      try {
        const today = new Date().toISOString().slice(0, 10)
        const date = (args.date && /^\d{4}-\d{2}-\d{2}$/.test(args.date)) ? args.date : today
        const file = join(LOGS_DIR, `observation-${date}.jsonl`)
        if (!existsSync(file)) return { date, total: 0, count: 0, items: [] }
        const raw = readFileSync(file, 'utf8')
        const lines = raw.split('\n').filter(Boolean)
        const limit = clampInt(args.limit, 30, 1, 200)
        let entries = []
        for (let i = lines.length - 1; i >= 0 && entries.length < limit; i--) {
          try {
            const item = JSON.parse(lines[i])
            if (args.type && item.type !== args.type) continue
            entries.push(item)
          } catch {}
        }
        return { date, total: lines.length, count: entries.length, items: entries }
      } catch (e) {
        return { error: String((e && e.message) || e), items: [] }
      }
    },
  })

  register({
    name: 'saperm_health',
    description: '投产自检：helper 在线/模型文件/角色注册表/当前观察模式/急停状态。L0 观察',
    parameters: empty,
    execute: async () => {
      let h = null
      try { h = await helperCall('health', {}) } catch (e) { h = { ok: false, error: e.message } }
      return {
        helper: { online: !!helperSingleton.online, pid: helperSingleton.pid || null, sessionId: (h && typeof h.sessionId === 'number') ? h.sessionId : null },
        paused: !!state.paused,
        l3: Array.from(state.l3.entries()).map(([agent, st]) => ({ agent, status: st })),
        l2: Array.from(state.l2.entries()).map(([agent, st]) => ({ agent, status: st })),
        overlayOpen: !!state.overlayOpen,
        autoCleanupTtlMs: state.obsTtlMs || 0,
        observation: { mode: state.obsMode || null, holds: Number(observationHolds) || 0, overlayOpen: !!state.overlayOpen, autoCleanupTtlMs: state.obsTtlMs || 0 },
        models: (h && h.models) ? h.models : null,
        clip: (h && h.clip) ? h.clip : null,
        faceModel: (h && typeof h.faceModel === 'boolean') ? h.faceModel : false,
        characters: (h && typeof h.characters === 'number') ? h.characters : 0,
        unknowns: (h && typeof h.unknowns === 'number') ? h.unknowns : 0,
        autoLearned: (h && typeof h.autoLearned === 'number') ? h.autoLearned : 0,
        workingSetMB: (h && typeof h.workingSetMB === 'number') ? h.workingSetMB : 0,
        providers: (h && Array.isArray(h.providers)) ? h.providers : [],
        secureDesktop: !!(h && h.secureDesktop),
        privacyKeywords: (h && Array.isArray(h.privacyKeywords)) ? h.privacyKeywords : [],
        privacyRedactions: (h && typeof h.privacyRedactions === 'number') ? h.privacyRedactions : 0,
        fgTitle: (h && typeof h.fgTitle === 'string') ? h.fgTitle : '',
        storage: {
          viewFiles: existsSync(VIEW_DIR) ? readdirSync(VIEW_DIR).filter((f) => f.endsWith('.jpg') || f.endsWith('.png')).length : 0,
          logFiles: existsSync(LOGS_DIR) ? readdirSync(LOGS_DIR).filter((f) => f.endsWith('.jsonl')).length : 0,
        },
        stream: streamStatus(),
        warm: { warming: (h && h.warming) === true, done: (h && h.warmDone) === true, ms: (h && h.warmMs) || null },
        watchdog: {
          crashes: helperSingleton.crashes,
          recoveries: helperSingleton.recoveries,
          lastCrashAt: helperSingleton.lastCrashAt,
          lastCrashCode: helperSingleton.lastCrashCode,
        },
        ok: !!(h && h.ok && helperSingleton.online),
        error: (h && h.error) ? String(h.error) : null,
      }
    },
  })

  console.log(`[sa-perm] 权限门已加载（helper=${HELPER_EXE}）。L2=${JSON.stringify(L2_TOOLS)} L3=${JSON.stringify(L3_TOOLS)}`)
}
