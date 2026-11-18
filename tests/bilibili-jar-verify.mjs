// bilibili 登录态落袋 + 三重验证：
//   1) 存 DPAPI 加密 jar（profile 清了也能 restore）
//   2) 无头实例打开 bilibili 首页，确认页面是登录态（不是"登录"按钮）
//   3) clear → 再开（应为登出）→ restore → 再开（应回到登录态）：这就是"复制 cookie 会不会失效"的实测
import { openPage, listCookies, getCookiesFull, setCookies, clearCookies, closeBrowser } from '../permission-plugin/lib/browser.mjs'
import { saveJar, loadJar, jarDir, toCdpCookies } from '../permission-plugin/lib/cookies.mjs'
import { spawn } from 'node:child_process'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const say = (o) => console.log(JSON.stringify({ t: new Date().toISOString().slice(11, 19), ...o }))

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
const unprotect = async (b64) => { const r = await hcall('crypto_dpapi', { blobs: [b64] }); const f = ((r.data && r.data.results) || [])[0]; return f && f.ok ? f.b64 : null }

// 登录态判据：顶部出现「消息/动态/投稿/创作中心」，且没有「登录」按钮
const judge = (text) => {
  const t = String(text || '')
  const hasLoginBtn = /(^|\n)\s*登录\s*(\n|$)|立即登录/.test(t)
  const hasUserNav = /消息|动态|投稿|创作中心|个人中心/.test(t)
  return { loggedIn: hasUserNav && !hasLoginBtn, hasLoginBtn, hasUserNav }
}

async function openBili(tag) {
  const r = await openPage({ url: 'https://www.bilibili.com/', w: 1280, h: 900, waitMs: 3500, text: true, maxText: 1200, screenshot: true, outDir: join(ROOT, 'out', 'sa-view') })
  const j = r.ok ? judge(r.text) : { loggedIn: false, error: r.error }
  say({ step: tag, ok: r.ok, url: r.url, title: r.title, ...j, shot: r.path, head: String(r.text || '').replace(/\s+/g, ' ').slice(0, 120) })
  return { ...r, ...j }
}

// 1) 现状 + 存 jar
const cur = await listCookies('bilibili')
const names = (cur.cookies || []).map((c) => c.name)
say({ step: 'current', count: cur.count, hasSession: names.includes('SESSDATA') && names.includes('DedeUserID') })
if (!names.includes('SESSDATA')) { say({ step: 'abort', reason: 'profile 里没有 SESSDATA' }); closeBrowser(); process.exit(1) }

const full = await getCookiesFull('bilibili')
const saved = await saveJar({ dir: jarDir(join(ROOT, 'permission-plugin')), name: 'bilibili', cookies: full.cookies, dpapi: protect, meta: { via: 'manual-login-visible-edge', host: 'bilibili' } })
say({ step: 'saved', ok: saved.ok, path: saved.path, bytes: saved.bytes, cookies: full.count, error: saved.error })

// 2) 后台无头验证登录态
const a = await openBili('verify-1-logged-in')

// 3) clear → 应为登出
await clearCookies()
const b = await openBili('verify-2-after-clear')

// 4) restore → 应回到登录态（这一步就是"复制 cookie 是否失效"的答案）
const jar = await loadJar({ dir: jarDir(join(ROOT, 'permission-plugin')), name: 'bilibili', dpapi: unprotect })
say({ step: 'jar-load', ok: jar.ok, cookies: jar.count, error: jar.error })
if (jar.ok) {
  const s = await setCookies(toCdpCookies(jar.cookies))
  say({ step: 'restore', ok: s.ok, set: s.set, rejected: s.rejectedCount })
}
const c = await openBili('verify-3-after-restore')

say({ step: 'summary', savedJar: saved.ok, loggedInBefore: a.loggedIn, loggedInAfterClear: b.loggedIn, loggedInAfterRestore: c.loggedIn })
closeBrowser()
try { hp.kill() } catch {}
process.exit(c.loggedIn ? 0 : 2)
