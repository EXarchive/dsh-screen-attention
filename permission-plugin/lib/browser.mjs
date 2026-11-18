// 后台静默浏览器：常驻 headless Chromium，CDP 走 stdio pipe（不占端口、不建窗口、不抢焦点）。
//
// 为什么不用 CLI `--screenshot`：
//   - 每次都冷启一个浏览器（~1s 起），且拿不到 DOM 文本、不能注入 cookie、不能续用登录态；
//   - `--screenshot` 只能抓视口，长页面被截断；
//   - 无法处理"页面在等 XHR"这类 SPA。
// 为什么走 pipe 而不是 --remote-debugging-port：
//   - 不监听任何 TCP，本机其他进程摸不到这个浏览器；
//   - 端口会撞（控制口占用 / 策略拦截），pipe 不会。
//
// 铁律：user-data-dir 永远是插件自己的隔离目录，绝不指向用户真实 profile。
import { spawn, execFileSync } from 'node:child_process'
import { existsSync, mkdirSync, writeFileSync, unlinkSync } from 'node:fs'
import { join } from 'node:path'

const CANDIDATES = [
  process.env.SA_BROWSER_EXE,
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
].filter(Boolean)

export function findBrowserExe() {
  for (const p of CANDIDATES) { try { if (existsSync(p)) return p } catch {} }
  return null
}

// 跨模块实例共享：热加载会造新实例，但子进程和它的管道 reader 属于旧实例。
const S = globalThis.__saPermBrowser || (globalThis.__saPermBrowser = {
  child: null, toChild: null, fromChild: null, seq: 0, pending: new Map(), buf: '',
  bootMs: 0, product: '', ua: '', isoDir: '', exe: '', lastError: '', booting: null,
  tabs: new Map(), tabSeq: 0,
})

const REAL_PROFILE_HINTS = ['\\user data', '/user data', '\\appdata\\local\\microsoft\\edge', '\\appdata\\local\\google\\chrome']

/**
 * 隔离 profile 目录护栏：这个目录会被 --headless 打开，指到用户真实 profile 上等于
 * 用无头实例去动真实 cookie 库 —— 实测那样会把解不开的 v20 行直接删光（1073→0）。
 */
function assertSafeIsoDir(dir) {
  const lower = String(dir || '').toLowerCase().replace(/\//g, '\\')
  for (const hint of REAL_PROFILE_HINTS) {
    if (lower.includes(hint.replace(/\//g, '\\'))) {
      throw new Error('拒绝把后台浏览器指向真实浏览器 profile：' + dir + '（SA_WEB_ISO 请指到插件自己的目录）')
    }
  }
  return dir
}

function pickIsoDir(isoDir) {
  if (isoDir) return isoDir
  if (process.env.SA_WEB_ISO) return process.env.SA_WEB_ISO
  if (S.isoDir) return S.isoDir
  return join(process.env.TEMP || '.', 'sa-web-iso')
}

/** 把所有在飞请求落地：只 clear 不 settle 会让调用方永久挂住（实测 HUNG>12s）。 */
function failAllPending(reason) {
  for (const [, p] of S.pending) {
    try { clearTimeout(p.timer) } catch {}
    try { p.res({ error: { message: reason } }) } catch {}
  }
  S.pending.clear()
}

function attachReader(fromChild) {
  S.fromChild = fromChild
  fromChild.setEncoding('utf8')
  fromChild.on('data', (chunk) => {
    S.buf += chunk
    let nul
    while ((nul = S.buf.indexOf('\u0000')) >= 0) {
      const raw = S.buf.slice(0, nul)
      S.buf = S.buf.slice(nul + 1)
      let msg
      try { msg = JSON.parse(raw) } catch { continue }
      if (msg.id && S.pending.has(msg.id)) {
        const { res, timer } = S.pending.get(msg.id)
        S.pending.delete(msg.id)
        clearTimeout(timer)
        res(msg)
      }
    }
  })
  fromChild.on('error', () => {})
}

function send(method, params = {}, sessionId, timeoutMs = 20000) {
  return new Promise((resolve, reject) => {
    // pipe 挂在 fd 3/4 上，不叫 stdin/stdout —— 用 child.stdin 会永远是 null
    if (!S.child || !S.toChild || S.child.killed) return reject(new Error('浏览器未启动'))
    const id = ++S.seq
    const timer = setTimeout(() => {
      if (S.pending.has(id)) { S.pending.delete(id); reject(new Error('CDP 超时: ' + method)) }
    }, timeoutMs)
    S.pending.set(id, { res: resolve, timer })
    try {
      S.toChild.write(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }) + '\u0000')
    } catch (e) {
      clearTimeout(timer); S.pending.delete(id); reject(e)
    }
  })
}

/**
 * 起（或复用）常驻后台浏览器。返回 { ok, product, bootMs, reused, isoDir }
 * 冷启动必须串行：两个调用同时进来会各自 spawn，后者覆盖 S.child 并清掉前者的握手请求，
 * 实测结果两边全失败且拖到 21.9s。用单个在飞 promise 收口。
 */
export async function ensureBrowser(opts = {}) {
  if (S.child && !S.child.killed && S.fromChild) {
    return { ok: true, product: S.product, ua: S.ua, bootMs: S.bootMs, reused: true, isoDir: S.isoDir }
  }
  if (S.booting) return S.booting
  S.booting = doBoot(opts).finally(() => { S.booting = null })
  return S.booting
}

async function doBoot({ isoDir, exe, bootRetried = false } = {}) {
  const bin = exe || findBrowserExe()
  if (!bin) return { ok: false, error: '没找到 Edge/Chrome（可用 SA_BROWSER_EXE 指定路径）' }
  let iso
  try { iso = assertSafeIsoDir(pickIsoDir(isoDir)) } catch (e) { return { ok: false, error: String(e.message || e) } }
  try { mkdirSync(iso, { recursive: true }) } catch {}
  S.isoDir = iso
  S.exe = bin
  S.buf = ''
  failAllPending('浏览器重启中，先前请求已失效')

  const t0 = Date.now()
  let child
  try {
    child = spawn(bin, [
      '--headless=new',
      '--disable-gpu',
      '--hide-scrollbars',
      '--no-first-run',
      '--no-default-browser-check',
      '--disable-features=Translate,MediaRouter',
      '--mute-audio',
      '--user-data-dir=' + iso,   // 隔离 profile（铁律：绝不指向真实浏览器 profile）
      '--remote-debugging-pipe',  // CDP over stdio，不监听端口
      'about:blank',
    ], { stdio: ['ignore', 'ignore', 'ignore', 'pipe', 'pipe'], windowsHide: true })
  } catch (e) {
    return { ok: false, error: '启动失败: ' + String(e.message || e) }
  }

  S.child = child
  S.childPid = child.pid || 0
  S.toChild = child.stdio[3]
  attachReader(child.stdio[4])
  child.on('exit', () => {
    S.child = null; S.toChild = null; S.fromChild = null; S.product = ''
    failAllPending('浏览器已退出')
  })
  child.on('error', (e) => { S.lastError = String(e.message || e) })

  // 等握手：不停重试 Browser.getVersion，直到管道就绪
  let lastErr = null
  for (let i = 0; i < 40; i++) {
    // 子进程一退出就立刻收手：不检查这一条会白等满 10s 才进重试（实测冷启并发 12.1s 就是这么来的）
    if (S.child !== child) { lastErr = '浏览器启动后立刻退出'; break }
    try {
      const r = await send('Browser.getVersion', {}, undefined, 4000)
      if (r && r.result) {
        S.product = r.result.product || ''
        S.ua = (r.result.userAgent || '').replace('/Headless', '')
        S.bootMs = Date.now() - t0
        return { ok: true, product: S.product, ua: S.ua, bootMs: S.bootMs, reused: false, isoDir: iso }
      }
      lastErr = (r && r.error && r.error.message) || '握手失败'
    } catch (e) { lastErr = String(e.message || e) }
    await new Promise((r) => setTimeout(r, 250))
  }
  // 握手失败十有八九是上一代的子进程还攥着 profile 锁：杀掉整棵树再试一次
  try {
    if (child.pid) execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, timeout: 10000, stdio: 'ignore' })
  } catch {}
  S.child = null; S.toChild = null; S.fromChild = null
  if (!bootRetried) {
    clearStaleProfileLock(iso)
    await new Promise((r) => setTimeout(r, 700))
    // 直接走 doBoot：再进 ensureBrowser 会撞上自己这个在飞 promise 而自锁
    return doBoot({ isoDir: iso, exe: bin, bootRetried: true })
  }
  return { ok: false, error: '后台浏览器起不来：' + (lastErr || '超时') }
}

export function browserStatus() {
  const running = !!(S.child && !S.child.killed)
  return { running, product: S.product, bootMs: S.bootMs, isoDir: S.isoDir, exe: S.exe, lastError: S.lastError }
}

/**
 * 关掉常驻浏览器。必须 /T 杀进程树：Edge 根进程下面还有 renderer/GPU/utility 一堆子进程，
 * 只 kill 根进程会把 profile 的 SingletonLock 留给下一次启动 —— 实测重启握手直接超时。
 */
export function closeBrowser() {
  const c = S.child
  S.child = null; S.toChild = null; S.fromChild = null; S.product = ''
  // 在飞请求必须落地（resolve 带 error），否则调用方永远等下去
  failAllPending('浏览器已关闭')
  if (!c) return { closed: false }
  try {
    if (c.pid) execFileSync('taskkill', ['/PID', String(c.pid), '/T', '/F'], { windowsHide: true, timeout: 10000, stdio: 'ignore' })
  } catch {}
  try { if (!c.killed) c.kill() } catch {}
  // 根进程要真死透：否则下一次 spawn 会被 profile 锁挡回来（实测表现为握手成功后立刻退出）。
  // closeBrowser 必须保持同步（dispose / killAllRuntime 路径都是同步调用），所以用 Atomics.wait 睡。
  if (c.pid) {
    const deadline = Date.now() + 2500
    while (Date.now() < deadline) {
      let alive = false
      try { process.kill(c.pid, 0); alive = true } catch {}
      if (!alive) break
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 60)
    }
  }
  clearStaleProfileLock(S.isoDir)
  return { closed: true }
}

/**
 * 清掉上一代留下的 profile 锁。Chromium 的 SingletonLock 在进程被杀后不会自己消失，
 * 下一个实例看到它就以为"已有实例在跑"，自己退出 —— 实测表现为新建标签页时报"浏览器已退出"。
 * 只在确认根进程确实不存在时才删，避免误伤真在跑的实例。
 */
function clearStaleProfileLock(iso) {
  if (!iso) return false
  const pid = S.childPid
  S.childPid = 0
  if (pid) {
    try { process.kill(pid, 0); return false } catch {}
  }
  let removed = false
  for (const f of ['SingletonLock', 'SingletonCookie', 'SingletonSocket']) {
    try { if (existsSync(join(iso, f))) { unlinkSync(join(iso, f)); removed = true } } catch {}
  }
  return removed
}

async function withPageOnce(fn, { w = 1400, h = 900, url = 'about:blank', timeoutMs = 30000 } = {}) {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  let targetId = null
  try {
    const t = await send('Target.createTarget', { url: 'about:blank' }, undefined, 10000)
    if (!t.result) return { ok: false, error: '建标签页失败: ' + JSON.stringify(t.error || {}), browserGone: !S.child }
    targetId = t.result.targetId
    const at = await send('Target.attachToTarget', { targetId, flatten: true }, undefined, 10000)
    if (!at.result) return { ok: false, error: '挂载标签页失败: ' + JSON.stringify(at.error || {}), browserGone: !S.child }
    const ss = at.result.sessionId
    await send('Page.enable', {}, ss)
    await send('Runtime.enable', {}, ss)
    try { await send('Network.enable', {}, ss) } catch {}
    if (boot.ua) { try { await send('Emulation.setUserAgentOverride', { userAgent: boot.ua }, ss) } catch {} }
    try { await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile: false }, ss) } catch {}
    if (url && url !== 'about:blank') {
      const nav = await send('Page.navigate', { url }, ss, timeoutMs)
      if (nav.error) return { ok: false, error: '导航失败: ' + (nav.error.message || ''), browserGone: !S.child }
      // 连不上/被拦截时 CDP 不报协议错，而是把 errorText 放进 result（页面变成错误页）。
      // 不取这个字段，调用方会把"DNS 挂了"当成"页面看完了"（实测 127.0.0.1:9 返回 ok=true）。
      if (nav.result && nav.result.errorText) return { ok: false, error: '导航失败: ' + nav.result.errorText, navError: true, url }
    }
    const out = await fn(ss, boot)
    return { ...out, bootMs: boot.bootMs, reused: boot.reused }
  } catch (e) {
    return { ok: false, error: String(e.message || e), browserGone: !S.child }
  } finally {
    if (targetId) { try { await send('Target.closeTarget', { targetId }, undefined, 5000) } catch {} }
  }
}

/**
 * 带一次自愈重试：上一代浏览器刚被杀、新实例又被 profile 锁挡下时，
 * 会出现"握手成功但立刻退出"，此时 createTarget 拿到「浏览器已退出」。
 * 这不是页面问题，重起一次就好，不该把错误抛给调用方。
 */
async function withPage(fn, opts = {}) {
  const first = await withPageOnce(fn, opts)
  if (first.ok || !first.browserGone) return first
  closeBrowser()
  await new Promise((r) => setTimeout(r, 500))
  const second = await withPageOnce(fn, opts)
  return second.ok ? { ...second, retried: true } : second
}

async function evalJs(sessionId, expression, timeoutMs = 15000) {
  const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }, sessionId, timeoutMs)
  if (r.error) return { error: r.error.message }
  const res = r.result && r.result.result
  if (r.result && r.result.exceptionDetails) return { error: (r.result.exceptionDetails.text || 'evaluate 异常') }
  return { value: res ? res.value : undefined }
}

/**
 * 打开网页：后台渲染 → 取标题/文本 → 截图（视口或整页）。
 * waitMs 是给 SPA 的额外等待；readyState 先到 complete 再等网络静默，能省下大半等待时间。
 */
export async function openPage({ url, w = 1400, h = 900, waitMs = 2000, fullPage = false, text = true, screenshot = true, outDir, maxText = 4000, js = null }) {
  return withPage(async (ss, boot) => {
    // 等 readyState：轮询比"死等固定毫秒"快，长页面也不用盲猜
    const deadline = Date.now() + Math.max(1000, waitMs)
    let ready = false
    while (Date.now() < deadline) {
      const r = await evalJs(ss, 'document.readyState', 5000)
      if (r.value === 'complete') { ready = true; break }
      await new Promise((res) => setTimeout(res, 120))
    }
    if (ready && waitMs > 0) await new Promise((res) => setTimeout(res, Math.min(waitMs, 2500)))

    const info = await evalJs(ss, 'JSON.stringify({url:location.href,title:document.title,vw:innerWidth,vh:innerHeight,ready:document.readyState})')
    let meta = {}
    try { meta = JSON.parse(info.value || '{}') } catch {}

    let bodyText = null
    let textError = null
    if (text) {
      const t = await evalJs(ss, `(() => { const b = document.body; if (!b) return ''; return b.innerText.replace(/\\n{3,}/g, '\\n\\n').slice(0, ${Math.max(200, Math.min(20000, maxText))}) })()`)
      if (t.error) textError = t.error
      else bodyText = (t.value || '').toString()
    }

    const out = {
      ok: true, url: meta.url || url, title: meta.title || '',
      readyState: meta.ready || '', ready,
      viewport: { w, h }, product: boot.product, text: bodyText,
    }
    if (textError) { out.text = null; out.textError = textError }
    if (screenshot) {
      const params = { format: 'png' }
      if (fullPage) {
        const lm = await send('Page.getLayoutMetrics', {}, ss)
        const cs = lm.result && (lm.result.cssContentSize || lm.result.contentSize)
        if (cs) {
          params.captureBeyondViewport = true
          params.clip = { x: 0, y: 0, width: Math.ceil(cs.width), height: Math.min(Math.ceil(cs.height), 12000), scale: 1 }
        }
      }
      const shot = await send('Page.captureScreenshot', params, ss, 30000)
      if (shot.result && shot.result.data) {
        const dir = outDir || join(process.env.TEMP || '.', 'sa-web-shot')
        try { mkdirSync(dir, { recursive: true }) } catch {}
        const p = join(dir, 'web-' + Date.now() + '.png')
        writeFileSync(p, Buffer.from(shot.result.data, 'base64'))
        out.path = p
        out.bytes = Math.round(shot.result.data.length * 0.75)
      } else {
        out.screenshotError = JSON.stringify(shot.error || {})
      }
    }
    // js 必须在**同一个页面会话**里跑：另起一页等于重新导航一次，返回值与截图不同源
    if (js) {
      const j = await evalJs(ss, String(js), 20000)
      out.js = j.error ? { error: j.error } : j.value
    }
    return out
  }, { w, h, url, timeoutMs: Math.max(15000, waitMs + 12000) })
}

/** 打开一个后台页面执行一段 JS（同源内），用于需要点击/取数据的场景 */
export async function runInPage({ url, expression, w = 1400, h = 900, waitMs = 1500 }) {
  return withPage(async (ss) => {
    await new Promise((res) => setTimeout(res, Math.max(200, waitMs)))
    const r = await evalJs(ss, expression, 20000)
    if (r.error) return { ok: false, error: r.error }
    return { ok: true, value: r.value }
  }, { w, h, url, timeoutMs: Math.max(15000, waitMs + 10000) })
}

/**
 * 注入 cookie 到后台浏览器（隔离 profile，只写我们自己的实例）。
 * 逐条发送：批量里只要有一条不合 CDP 规则（如 __Host- 前缀要求 secure+无 domain），
 * 整批会被 "Invalid cookie fields" 拒掉，单条发送能把坏的和好的一起保住。
 */
export async function setCookies(cookies) {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  let set = 0
  const rejected = []
  for (const c of cookies) {
    const r = await send('Storage.setCookies', { cookies: [c] }, undefined, 10000)
    if (r.error) rejected.push({ name: c.name, domain: c.domain, error: r.error.message })
    else set++
  }
  const g = await send('Storage.getCookies', {}, undefined, 15000)
  if (g.error) return { ok: false, error: '读取 cookie 失败: ' + g.error.message, set, rejected: rejected.slice(0, 8), rejectedCount: rejected.length }
  return { ok: set > 0 || cookies.length === 0, set, rejected: rejected.slice(0, 8), rejectedCount: rejected.length, total: ((g.result && g.result.cookies) || []).length }
}

export async function listCookies(filterHost) {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  const g = await send('Storage.getCookies', {}, undefined, 15000)
  // 浏览器已死时 send 会以 {error} 回来；不判这个字段就会把"故障"报成"0 条 cookie"
  if (g.error) return { ok: false, error: '读取 cookie 失败: ' + g.error.message }
  let list = (g.result && g.result.cookies) || []
  if (filterHost) list = list.filter((c) => String(c.domain || '').includes(filterHost))
  return {
    ok: true, count: list.length,
    cookies: list.map((c) => ({ domain: c.domain, name: c.name, path: c.path, secure: c.secure, httpOnly: c.httpOnly, session: c.session, expires: c.expires, valueLen: String(c.value || '').length })),
  }
}

/** 取后台浏览器里 cookie 的**完整值**（保存 jar 用；listCookies 只给长度，避免误把值打进对话） */
export async function getCookiesFull(filterHost) {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  const g = await send('Storage.getCookies', {}, undefined, 15000)
  if (g.error) return { ok: false, error: '读取 cookie 失败: ' + g.error.message }
  let list = (g.result && g.result.cookies) || []
  if (filterHost) list = list.filter((c) => String(c.domain || '').includes(filterHost))
  const cookies = list.map((c) => {
    const o = {
      name: c.name, value: c.value, domain: c.domain, path: c.path || '/',
      secure: !!c.secure, httpOnly: !!c.httpOnly, sameSite: c.sameSite || undefined,
    }
    if (c.expires && c.expires > 0) o.expires = Math.floor(c.expires)
    if (c.partitionKey && c.partitionKey.topLevelSite) o.partitionKey = c.partitionKey
    return o
  })
  return { ok: true, count: cookies.length, cookies }
}

export async function clearCookies() {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  const r = await send('Storage.clearCookies', {}, undefined, 15000)
  return { ok: !r.error, error: r.error && r.error.message }
}

// ============ 常驻标签页：跨调用保持同一个页面 ============
// 为什么需要：扫码登录这类流程，页面一关链路就断（二维码 key 由页面持有、轮询也由页面发起）。
// openPage 每次都会关标签页，所以另开一套「开着不放」的 API，由 tabKey 引用。

async function prepareSession(ss, boot, w, h) {
  await send('Page.enable', {}, ss)
  await send('Runtime.enable', {}, ss)
  try { await send('Network.enable', {}, ss) } catch {}
  if (boot.ua) { try { await send('Emulation.setUserAgentOverride', { userAgent: boot.ua }, ss) } catch {} }
  try { await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile: false }, ss) } catch {}
}

async function waitReady(ss, waitMs) {
  const deadline = Date.now() + Math.max(600, waitMs)
  let ready = false
  while (Date.now() < deadline) {
    const r = await evalJs(ss, 'document.readyState', 5000)
    if (r.value === 'complete') { ready = true; break }
    await new Promise((res) => setTimeout(res, 120))
  }
  return ready
}

async function snapshot(ss, { text = true, maxText = 4000, screenshot = true, fullPage = false, outDir, w, h } = {}) {
  const info = await evalJs(ss, 'JSON.stringify({url:location.href,title:document.title,ready:document.readyState})')
  let meta = {}
  try { meta = JSON.parse(info.value || '{}') } catch {}
  const out = { url: meta.url || '', title: meta.title || '', readyState: meta.ready || '', viewport: { w, h } }
  if (text) {
    const t = await evalJs(ss, `(() => { const b = document.body; if (!b) return ''; return b.innerText.replace(/\\n{3,}/g, '\\n\\n').slice(0, ${Math.max(200, Math.min(20000, maxText))}) })()`)
    if (t.error) { out.text = null; out.textError = t.error } else out.text = (t.value || '').toString()
  }
  if (screenshot) {
    const params = { format: 'png' }
    if (fullPage) {
      const lm = await send('Page.getLayoutMetrics', {}, ss)
      const cs = lm.result && (lm.result.cssContentSize || lm.result.contentSize)
      if (cs) {
        params.captureBeyondViewport = true
        params.clip = { x: 0, y: 0, width: Math.ceil(cs.width), height: Math.min(Math.ceil(cs.height), 12000), scale: 1 }
      }
    }
    const shot = await send('Page.captureScreenshot', params, ss, 30000)
    if (shot.result && shot.result.data) {
      const dir = outDir || join(process.env.TEMP || '.', 'sa-web-shot')
      try { mkdirSync(dir, { recursive: true }) } catch {}
      const p = join(dir, 'web-' + Date.now() + '.png')
      writeFileSync(p, Buffer.from(shot.result.data, 'base64'))
      out.path = p
      out.bytes = Math.round(shot.result.data.length * 0.75)
    } else {
      out.screenshotError = JSON.stringify(shot.error || {})
    }
  }
  return out
}

/** 开一个**不关闭**的标签页，返回 tabKey；后续用 readTab 反复看同一页 */
export async function openTab({ url, w = 1400, h = 900, waitMs = 2000, fullPage = false, text = true, screenshot = true, outDir, maxText = 4000, js = null }) {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  let targetId = null
  try {
    const t = await send('Target.createTarget', { url: 'about:blank' }, undefined, 10000)
    if (!t.result) return { ok: false, error: '建标签页失败: ' + JSON.stringify(t.error || {}) }
    targetId = t.result.targetId
    const at = await send('Target.attachToTarget', { targetId, flatten: true }, undefined, 10000)
    if (!at.result) { try { await send('Target.closeTarget', { targetId }) } catch {} ; return { ok: false, error: '挂载标签页失败: ' + JSON.stringify(at.error || {}) } }
    const ss = at.result.sessionId
    await prepareSession(ss, boot, w, h)
    if (url && url !== 'about:blank') {
      const nav = await send('Page.navigate', { url }, ss, Math.max(15000, waitMs + 12000))
      if (nav.error) { try { await send('Target.closeTarget', { targetId }) } catch {} ; return { ok: false, error: '导航失败: ' + (nav.error.message || '') } }
      if (nav.result && nav.result.errorText) { try { await send('Target.closeTarget', { targetId }) } catch {} ; return { ok: false, error: '导航失败: ' + nav.result.errorText, navError: true } }
    }
    const ready = await waitReady(ss, waitMs)
    if (ready && waitMs > 0) await new Promise((res) => setTimeout(res, Math.min(waitMs, 2500)))
    const snap = await snapshot(ss, { text, maxText, screenshot, fullPage, outDir, w, h })
    const key = 't' + (S.tabSeq = (S.tabSeq || 0) + 1)
    S.tabs = S.tabs || new Map()
    S.tabs.set(key, { targetId, ss, url: snap.url || url, w, h, outDir, openedAt: Date.now() })
    const out = { ok: true, tabKey: key, kept: true, product: boot.product, ...snap }
    if (js) { const j = await evalJs(ss, String(js), 20000); out.js = j.error ? { error: j.error } : j.value }
    return out
  } catch (e) {
    if (targetId) { try { await send('Target.closeTarget', { targetId }) } catch {} }
    return { ok: false, error: String(e.message || e) }
  }
}

/** 读同一个标签页：可带 waitFor 表达式轮询（如等登录完成），超时也返回当时画面 */
export async function readTab(tabKey, { screenshot = true, fullPage = false, text = true, maxText = 4000, waitFor = null, waitForMs = 60000, js = null } = {}) {
  const t = S.tabs && S.tabs.get(tabKey)
  if (!t) return { ok: false, error: '没有这个标签页（可能已关闭）: ' + tabKey, tabs: [...((S.tabs && S.tabs.keys()) || [])] }
  if (waitFor) {
    const deadline = Date.now() + Math.max(1000, waitForMs)
    let hit = null
    while (Date.now() < deadline) {
      const r = await evalJs(t.ss, String(waitFor), 8000)
      if (r.error) return { ok: false, error: 'waitFor 表达式出错: ' + r.error, tabKey }
      if (r.value) { hit = r.value; break }
      await new Promise((res) => setTimeout(res, 1200))
    }
    if (!hit) {
      const snap = await snapshot(t.ss, { text, maxText, screenshot, fullPage, outDir: t.outDir, w: t.w, h: t.h })
      return { ok: false, error: '等待超时（waitFor 一直是假）', tabKey, ...snap }
    }
  }
  const snap = await snapshot(t.ss, { text, maxText, screenshot, fullPage, outDir: t.outDir, w: t.w, h: t.h })
  const out = { ok: true, tabKey, ...snap }
  if (js) { const j = await evalJs(t.ss, String(js), 20000); out.js = j.error ? { error: j.error } : j.value }
  return out
}

export function listTabs() {
  const tabs = []
  for (const [k, v] of (S.tabs || new Map())) tabs.push({ tabKey: k, url: v.url, w: v.w, h: v.h, ageMs: Date.now() - v.openedAt })
  return { ok: true, count: tabs.length, tabs }
}

export async function closeTab(tabKey) {
  const t = S.tabs && S.tabs.get(tabKey)
  if (!t) return { ok: false, error: '没有这个标签页: ' + tabKey }
  try { await send('Target.closeTarget', { targetId: t.targetId }, undefined, 5000) } catch {}
  S.tabs.delete(tabKey)
  return { ok: true, closed: tabKey, remain: S.tabs.size }
}
