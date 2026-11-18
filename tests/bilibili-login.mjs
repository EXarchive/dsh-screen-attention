// 在插件自己的隔离 profile 里完成 bilibili 扫码登录（不碰用户真实浏览器，不复制任何文件）
// 用法: node tests/bilibili-login.mjs [--url https://passport.bilibili.com/login] [--timeout 240]
// 输出: 每 PT 一行 JSON 进度；二维码截图路径写在 qr 字段
import { openTab, readTab, listCookies, closeBrowser } from '../permission-plugin/lib/browser.mjs'
import { existsSync } from 'node:fs'

const arg = (k, d) => {
  const i = process.argv.indexOf('--' + k)
  return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : d
}
const URL_LOGIN = arg('url', 'https://passport.bilibili.com/login')
const TIMEOUT_MS = Number(arg('timeout', '240')) * 1000

const say = (o) => console.log(JSON.stringify({ t: new Date().toISOString(), ...o }))

say({ step: 'open', url: URL_LOGIN })
const tab = await openTab({ url: URL_LOGIN, w: 1280, h: 900, waitMs: 4000, fullPage: false, text: true })
if (!tab.ok) { say({ step: 'open-failed', error: tab.error }); process.exit(1) }
say({ step: 'opened', tabKey: tab.tabKey, title: tab.title, qr: tab.path, textHead: String(tab.text || '').slice(0, 120) })

const deadline = Date.now() + TIMEOUT_MS
let lastCount = -1
let loggedIn = false
while (Date.now() < deadline) {
  await new Promise((r) => setTimeout(r, 3000))
  let lc
  try { lc = await listCookies('bilibili') } catch (e) { say({ step: 'poll-error', error: String(e.message || e) }); break }
  if (!lc.ok) { say({ step: 'poll-failed', error: lc.error }); break }
  const names = (lc.cookies || []).map((c) => c.name)
  const hasSession = names.includes('SESSDATA') && names.includes('DedeUserID')
  if (names.length !== lastCount) { lastCount = names.length; say({ step: 'cookies', count: names.length, hasSession }) }
  if (hasSession) { loggedIn = true; say({ step: 'logged-in', count: names.length }); break }
  // 顺带汇报页面是否已经跳转（用户扫码后页面会变）
  try {
    const r = await readTab(tab.tabKey, { screenshot: false, text: false, js: 'location.href' })
    if (r.ok && r.js && !String(r.js).includes('passport')) say({ step: 'navigated', url: r.js })
  } catch {}
}

if (loggedIn) {
  const after = await readTab(tab.tabKey, { screenshot: true, text: true, waitMs: 2000, maxText: 600 })
  say({
    step: 'verify',
    url: after.url, title: after.title, shot: after.path,
    loggedInHint: /退出|个人中心|消息|动态|投稿/.test(String(after.text || '')) ? 'yes' : 'unknown',
    textHead: String(after.text || '').slice(0, 200),
  })
  // 登录态已经写进隔离 profile；关掉标签页不影响
  await readTab(tab.tabKey, { screenshot: false, text: false, js: 'document.title' }).catch(() => {})
  const cs = await listCookies('bilibili')
  say({ step: 'done', cookieCount: (cs.cookies || []).length, names: (cs.cookies || []).map((c) => c.name).slice(0, 12) })
  closeBrowser()
  process.exit(0)
}

say({ step: 'timeout', waitedSec: Math.round(TIMEOUT_MS / 1000), qr: existsSync(String(tab.path || '')) ? tab.path : null })
closeBrowser()
process.exit(2)
