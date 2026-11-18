// 从 jar 恢复 bilibili 登录态（跨浏览器重启验证）+ 判定页面是否真登录
import { openPage, setCookies, listCookies, closeBrowser } from '../permission-plugin/lib/browser.mjs'
import { loadJar, jarDir, toCdpCookies } from '../permission-plugin/lib/cookies.mjs'
import { spawn } from 'node:child_process'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const say = (o) => console.log(JSON.stringify({ t: new Date().toISOString().slice(11, 19), ...o }))

const EXE = process.env.SA_HELPER_EXE || join(ROOT, 'helper', 'bin', 'Release', 'net9.0-windows10.0.19041.0', 'SaHelper.exe')
const hp = spawn(EXE, [], { stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true })
let b = '', s = 0
const pend = new Map()
hp.stdout.on('data', (d) => { b += d.toString('utf8'); let i; while ((i = b.indexOf('\n')) >= 0) { const l = b.slice(0, i).trim(); b = b.slice(i + 1); if (!l) continue; let j; try { j = JSON.parse(l) } catch { continue } if (j.event) continue; const r = pend.get(j.id); if (r) { pend.delete(j.id); r(j) } } })
const call = (cmd, p = {}) => new Promise((res) => { const id = ++s; pend.set(id, res); hp.stdin.write(JSON.stringify({ id, cmd, ...p }) + '\n') })
const unprotect = async (b64) => { const r = await call('crypto_dpapi', { blobs: [b64] }); const f = ((r.data && r.data.results) || [])[0]; return f && f.ok ? f.b64 : null }

say({ step: 'before', bilibili: (await listCookies('bilibili')).count })
const jar = await loadJar({ dir: jarDir(join(ROOT, 'permission-plugin')), name: 'bilibili', dpapi: unprotect })
say({ step: 'jar', ok: jar.ok, cookies: jar.count, savedAt: jar.savedAt, error: jar.error })
if (!jar.ok) { closeBrowser(); process.exit(1) }
const r = await setCookies(toCdpCookies(jar.cookies))
say({ step: 'restore', ok: r.ok, set: r.set, rejected: r.rejectedCount })

const p = await openPage({ url: 'https://www.bilibili.com/', w: 1280, h: 900, waitMs: 3500, text: true, maxText: 900, screenshot: true, outDir: join(ROOT, 'out', 'sa-view') })
const t = String(p.text || '')
say({
  step: 'verify', ok: p.ok, url: p.url, title: p.title, shot: p.path,
  loggedIn: /消息|创作中心|投稿/.test(t) && !/(^|\n)\s*登录\s*(\n|$)|立即登录/.test(t),
  head: t.replace(/\s+/g, ' ').slice(0, 110),
})

// 落盘确认：正常关闭前等一会儿，让 Chromium 把 cookie flush 到 profile
await new Promise((res) => setTimeout(res, 2500))
closeBrowser()
try { hp.kill() } catch {}
