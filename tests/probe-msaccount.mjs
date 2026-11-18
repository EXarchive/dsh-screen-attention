// 探针：后台隔离 profile 的 Edge 打开微软账户页，看是否白嫖到 Windows SSO 登录态
import { openTab, readTab, listCookies, getCookiesFull, closeBrowser } from '../permission-plugin/lib/browser.mjs'

const say = (o) => console.log(JSON.stringify({ t: new Date().toISOString().slice(11, 19), ...o }))
const URL = process.argv[2] || 'https://account.microsoft.com/'

say({ step: 'open', url: URL })
const tab = await openTab({ url: URL, w: 1280, h: 900, waitMs: 5000, text: true, maxText: 1500 })
if (!tab.ok) { say({ step: 'open-failed', error: tab.error }); process.exit(1) }

const text = String(tab.text || '')
const signedOut = /登录|登入|Sign in|Sign In/.test(text)
const signedIn = /退出|注销|Sign out|Sign Out|个人资料|Your profile|account\.microsoft\.com\/(?:zh|en)/.test(text)
say({
  step: 'opened', title: tab.title, url: tab.url, qr: tab.path,
  looksSignedOut: signedOut, looksSignedIn: signedIn,
  head: text.replace(/\s+/g, ' ').slice(0, 220),
})

const c = await listCookies('microsoft')
say({ step: 'cookies', microsoftCount: c.count, names: (c.cookies || []).map((x) => x.name).slice(0, 12) })

// 关键域名：登录态一般挂在 login.live.com / .live.com / .microsoft.com
const live = await listCookies('live.com')
const ms = await listCookies('microsoftonline')
say({ step: 'cookies2', liveCom: live.count, msOnline: ms.count, liveNames: (live.cookies || []).map((x) => x.name).slice(0, 10) })

await readTab(tab.tabKey, { screenshot: false, text: false, close: true }).catch(() => {})
const full = await getCookiesFull('microsoft')
say({ step: 'total', allMicrosoftCookies: full.count })
closeBrowser()
