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
import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
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
  bootMs: 0, product: '', ua: '', isoDir: '', exe: '', lastError: '',
})

function pickIsoDir(isoDir) {
  if (isoDir) return isoDir
  if (process.env.SA_WEB_ISO) return process.env.SA_WEB_ISO
  if (S.isoDir) return S.isoDir
  return join(process.env.TEMP || '.', 'sa-web-iso')
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

/** 起（或复用）常驻后台浏览器。返回 { ok, product, bootMs, reused, isoDir } */
export async function ensureBrowser({ isoDir, exe } = {}) {
  if (S.child && !S.child.killed && S.fromChild) {
    return { ok: true, product: S.product, ua: S.ua, bootMs: S.bootMs, reused: true, isoDir: S.isoDir }
  }
  const bin = exe || findBrowserExe()
  if (!bin) return { ok: false, error: '没找到 Edge/Chrome（可用 SA_BROWSER_EXE 指定路径）' }
  const iso = pickIsoDir(isoDir)
  try { mkdirSync(iso, { recursive: true }) } catch {}
  S.isoDir = iso
  S.exe = bin
  S.buf = ''
  S.pending.clear()

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
  S.toChild = child.stdio[3]
  attachReader(child.stdio[4])
  child.on('exit', () => {
    S.child = null; S.toChild = null; S.fromChild = null; S.product = ''
    for (const [, p] of S.pending) { clearTimeout(p.timer); p.res({ error: { message: '浏览器已退出' } }) }
    S.pending.clear()
  })
  child.on('error', (e) => { S.lastError = String(e.message || e) })

  // 等握手：不停重试 Browser.getVersion，直到管道就绪
  let lastErr = null
  for (let i = 0; i < 40; i++) {
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
  try { child.kill() } catch {}
  S.child = null
  return { ok: false, error: '后台浏览器起不来：' + (lastErr || '超时') }
}

export function browserStatus() {
  const running = !!(S.child && !S.child.killed)
  return { running, product: S.product, bootMs: S.bootMs, isoDir: S.isoDir, exe: S.exe, lastError: S.lastError }
}

export function closeBrowser() {
  const c = S.child
  S.child = null; S.toChild = null; S.fromChild = null; S.product = ''; S.pending.clear()
  try { if (c && !c.killed) c.kill() } catch {}
  return { closed: !!c }
}

async function withPage(fn, { w = 1400, h = 900, url = 'about:blank', timeoutMs = 30000 } = {}) {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  let targetId = null
  try {
    const t = await send('Target.createTarget', { url: 'about:blank' }, undefined, 10000)
    if (!t.result) return { ok: false, error: '建标签页失败: ' + JSON.stringify(t.error || {}) }
    targetId = t.result.targetId
    const at = await send('Target.attachToTarget', { targetId, flatten: true }, undefined, 10000)
    if (!at.result) return { ok: false, error: '挂载标签页失败: ' + JSON.stringify(at.error || {}) }
    const ss = at.result.sessionId
    await send('Page.enable', {}, ss)
    await send('Runtime.enable', {}, ss)
    try { await send('Network.enable', {}, ss) } catch {}
    if (boot.ua) { try { await send('Emulation.setUserAgentOverride', { userAgent: boot.ua }, ss) } catch {} }
    try { await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 1, mobile: false }, ss) } catch {}
    if (url && url !== 'about:blank') {
      const nav = await send('Page.navigate', { url }, ss, timeoutMs)
      if (nav.error) return { ok: false, error: '导航失败: ' + (nav.error.message || '') }
    }
    const out = await fn(ss, boot)
    return { ...out, bootMs: boot.bootMs, reused: boot.reused }
  } catch (e) {
    return { ok: false, error: String(e.message || e) }
  } finally {
    if (targetId) { try { await send('Target.closeTarget', { targetId }, undefined, 5000) } catch {} }
  }
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
export async function openPage({ url, w = 1400, h = 900, waitMs = 2000, fullPage = false, text = true, screenshot = true, outDir, maxText = 4000 }) {
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
    if (text) {
      const t = await evalJs(ss, `(() => { const b = document.body; if (!b) return ''; return b.innerText.replace(/\\n{3,}/g, '\\n\\n').slice(0, ${Math.max(200, Math.min(20000, maxText))}) })()`)
      bodyText = (t.value || '').toString()
    }

    const out = {
      ok: true, url: meta.url || url, title: meta.title || '',
      readyState: meta.ready || '', ready,
      viewport: { w, h }, product: boot.product, text: bodyText,
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
  return { ok: set > 0 || cookies.length === 0, set, rejected: rejected.slice(0, 8), rejectedCount: rejected.length, total: ((g.result && g.result.cookies) || []).length }
}

export async function listCookies(filterHost) {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  const g = await send('Storage.getCookies', {}, undefined, 15000)
  let list = (g.result && g.result.cookies) || []
  if (filterHost) list = list.filter((c) => String(c.domain || '').includes(filterHost))
  return {
    ok: true, count: list.length,
    cookies: list.map((c) => ({ domain: c.domain, name: c.name, path: c.path, secure: c.secure, httpOnly: c.httpOnly, session: c.session, expires: c.expires, valueLen: String(c.value || '').length })),
  }
}

export async function clearCookies() {
  const boot = await ensureBrowser({})
  if (!boot.ok) return { ok: false, error: boot.error }
  const r = await send('Storage.clearCookies', {}, undefined, 15000)
  return { ok: !r.error, error: r.error && r.error.message }
}
