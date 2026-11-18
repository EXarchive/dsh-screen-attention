// 探针：后台隔离 Edge 打开微软登录页，判断是「已 SSO 登录」还是「要账号密码」
import { openTab, readTab, listCookies, closeBrowser } from '../permission-plugin/lib/browser.mjs'

const say = (o) => console.log(JSON.stringify({ t: new Date().toISOString().slice(11, 19), ...o }))
const URL = process.argv[2] || 'https://login.live.com/'

const tab = await openTab({ url: URL, w: 1280, h: 900, waitMs: 6000, text: false, screenshot: true })
if (!tab.ok) { say({ step: 'open-failed', error: tab.error }); process.exit(1) }
say({ step: 'opened', title: tab.title, url: tab.url, shot: tab.path })

const r = await readTab(tab.tabKey, {
  screenshot: true, text: true, maxText: 1200,
  waitFor: 'document.body && document.body.innerText.trim().length > 30',
  waitForMs: 30000,
})
const text = String(r.text || '').replace(/\s+/g, ' ')
say({
  step: 'settled', ok: r.ok, error: r.error || undefined,
  title: r.title, url: r.url, shot: r.path,
  needsCredential: /密码|Password|电子邮件|Email|电话|Phone|输入密码|验证/.test(text),
  head: text.slice(0, 260),
})

const live = await listCookies('live.com')
say({ step: 'cookies', liveCount: live.count, names: (live.cookies || []).map((c) => c.name).slice(0, 12) })

await readTab(tab.tabKey, { screenshot: false, text: false, close: true }).catch(() => {})
closeBrowser()
