// 读 edge://sync-internals 的「数据类型」表：同步到底覆盖哪些类型，cookie 在不在
import { openTab, readTab, listCookies, closeBrowser } from '../permission-plugin/lib/browser.mjs'
const say = (o) => console.log(JSON.stringify(o))

const t = await openTab({ url: 'edge://sync-internals', w: 1200, h: 1000, waitMs: 7000, text: false })
if (!t.ok) { say({ step: 'open-failed', error: t.error }); process.exit(1) }

const rows = await readTab(t.tabKey, {
  screenshot: false, text: false,
  js: `(() => {
    const out = [];
    for (const tr of document.querySelectorAll('tr')) {
      const cells = [...tr.children].map((c) => (c.innerText || '').trim()).filter(Boolean);
      if (cells.length >= 2) out.push(cells.slice(0, 3).join(' | '));
    }
    return JSON.stringify(out);
  })()`,
})
let parsed = []
try { parsed = JSON.parse(rows.js || '[]') } catch {}
say({ step: 'table-rows', count: parsed.length })

// 只挑「数据类型」那类行（同步类型名开头）
const TYPE_RE = /^(Bookmarks|Passwords|Preferences|Autofill|Extensions|Typed URLs|Apps|Sessions|History|Tabs|Dictionary|Device Info|Managed User|Nigori|Printers|Reading List|Workspace|Collections|Cookie)/i
const typeRows = parsed.filter((r) => TYPE_RE.test(r))
say({ step: 'datatype-rows', count: typeRows.length })
for (const r of typeRows) say({ type: r })
for (const r of parsed.filter((x) => /cookie/i.test(x))) say({ cookieMention: r })

// 关键词核对
const kw = await readTab(t.tabKey, {
  screenshot: false, text: false,
  js: `(() => {
    const s = (document.body.innerText || '').toLowerCase();
    const hit = (w) => s.includes(w);
    return JSON.stringify({ cookies: hit('cookie'), passwords: hit('password'), bookmarks: hit('bookmark'), autofill: hit('autofill'), preferences: hit('preference'), extensions: hit('extension'), sessions: hit('session'), tabs: hit('tab'), history: hit('history') });
  })()`,
})
say({ step: 'keywords', value: kw.js })

// 当前 profile 里到底有多少 cookie（同步若带 cookie，这里会看到别的站）
for (const h of ['live.com', 'bing.com', 'msn.com', 'microsoft', 'bilibili']) {
  const c = await listCookies(h)
  say({ cookies: h, count: c.count, names: (c.cookies || []).map((x) => x.name).slice(0, 8) })
}
await readTab(t.tabKey, { screenshot: false, text: false, close: true }).catch(() => {})
closeBrowser()
