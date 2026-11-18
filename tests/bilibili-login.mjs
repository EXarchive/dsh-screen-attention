// 在插件自己的隔离 profile 里完成 bilibili 扫码登录，成功后**立刻**加密存成 DSH jar。
// 不碰用户真实浏览器、不复制任何文件。
// 用法: node tests/bilibili-login.mjs [--timeout 600] [--jar bilibili]
import { openTab, readTab, listCookies, getCookiesFull, closeBrowser } from '../permission-plugin/lib/browser.mjs'
import { saveJar, jarDir } from '../permission-plugin/lib/cookies.mjs'
import { spawn } from 'node:child_process'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : d }
const TIMEOUT_MS = Number(arg('timeout', '600')) * 1000
const JAR = arg('jar', 'bilibili')
const URL_LOGIN = arg('url', 'https://passport.bilibili.com/login')
const say = (o) => console.log(JSON.stringify({ t: new Date().toISOString().slice(11, 19), ...o }))

// 直接起 helper 调 DPAPI 加密（不依赖 dsh 进程）
const EXE = process.env.SA_HELPER_EXE || join(ROOT, 'helper', 'bin', 'Release', 'net9.0-windows10.0.19041.0', 'SaHelper.exe')
const hp = spawn(EXE, [], { stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true })
let hbuf = '', hseq = 0
const hpend = new Map()
hp.stdout.on('data', (d) => {
  hbuf += d.toString('utf8'); let i
  while ((i = hbuf.indexOf('\n')) >= 0) {
    const line = hbuf.slice(0, i).trim(); hbuf = hbuf.slice(i + 1); if (!line) continue
    let j; try { j = JSON.parse(line) } catch { continue }
    if (j.event) continue
    const r = hpend.get(j.id); if (r) { hpend.delete(j.id); r(j) }
  }
})
const hcall = (cmd, params = {}) => new Promise((res) => { const id = ++hseq; hpend.set(id, res); hp.stdin.write(JSON.stringify({ id, cmd, ...params }) + '\n') })
const protect = async (b64) => { const r = await hcall('crypto_protect', { blobs: [b64] }); const f = ((r.data && r.data.results) || [])[0]; return f && f.ok ? f.b64 : null }

say({ step: 'open', url: URL_LOGIN })
const tab = await openTab({ url: URL_LOGIN, w: 1280, h: 900, waitMs: 4000, fullPage: false, text: true })
if (!tab.ok) { say({ step: 'open-failed', error: tab.error }); process.exit(1) }
say({ step: 'opened', tabKey: tab.tabKey, title: tab.title, qr: tab.path })

const deadline = Date.now() + TIMEOUT_MS
let loggedIn = false
let last = -1
while (Date.now() < deadline) {
  await new Promise((r) => setTimeout(r, 3000))
  let lc
  try { lc = await listCookies('bilibili') } catch (e) { say({ step: 'poll-error', error: String(e.message || e) }); break }
  if (!lc.ok) { say({ step: 'poll-failed', error: lc.error }); break }
  const names = (lc.cookies || []).map((c) => c.name)
  const hasSession = names.includes('SESSDATA') && names.includes('DedeUserID')
  if (names.length !== last) { last = names.length; say({ step: 'cookies', count: names.length, hasSession }) }
  if (hasSession) { loggedIn = true; say({ step: 'logged-in', count: names.length }); break }
}

if (!loggedIn) {
  say({ step: 'timeout', waitedSec: Math.round(TIMEOUT_MS / 1000), qr: tab.path })
  closeBrowser()
  try { hp.kill() } catch {}
  process.exit(2)
}

// 登录成功：先取完整值加密存 jar（不依赖浏览器落盘时机），再验证页面
const full = await getCookiesFull('bilibili')
say({ step: 'snapshot', cookies: full.count })
const saved = await saveJar({ dir: jarDir(join(ROOT, 'permission-plugin')), name: JAR, cookies: full.cookies, dpapi: protect, meta: { source: 'web-iso', via: 'qr-login', host: 'bilibili' } })
say({ step: 'saved', ok: saved.ok, path: saved.path, bytes: saved.bytes, error: saved.error })

const after = await readTab(tab.tabKey, { screenshot: true, text: true, waitMs: 1500, maxText: 400 })
say({
  step: 'verify', url: after.url, title: after.title, shot: after.path,
  loggedInHint: /退出|个人中心|消息|动态|投稿|关注|粉丝/.test(String(after.text || '')) ? 'yes' : 'unknown',
  textHead: String(after.text || '').slice(0, 160),
})
const names = (await listCookies('bilibili')).cookies.map((c) => c.name)
say({ step: 'done', jar: saved.ok ? JAR : null, names: names.slice(0, 14) })

closeBrowser()
try { hp.kill() } catch {}
process.exit(0)
