// 证据探针：Edge 的同步到底有哪些数据类型？cookie 在不在里面？
// 用浏览器自己的 edge://sync-internals 页面回答，不靠记忆也不靠第三方文档。
import { openTab, readTab, closeBrowser } from '../permission-plugin/lib/browser.mjs'

const say = (o) => console.log(JSON.stringify(o))

const t = await openTab({ url: 'edge://sync-internals', w: 1200, h: 1000, waitMs: 7000, text: true, maxText: 6000, screenshot: true })
say({ step: 'open', ok: t.ok, title: t.title, shot: t.path, textLen: String(t.text || '').length })

const probe = await readTab(t.tabKey, {
  screenshot: false,
  text: false,
  js: `(() => {
    const s = document.body.innerText || '';
    // 同步数据类型通常以 "Type" 列表出现；把含 cookie/password/bookmark 的关键词都查一遍
    const types = [...s.matchAll(/^\\s*([A-Za-z][A-Za-z _]{2,30})\\s*$/gm)].map((m) => m[1].trim());
    return JSON.stringify({
      len: s.length,
      hasCookieWord: /cookie/i.test(s),
      hasPassword: /password/i.test(s),
      hasBookmark: /bookmark/i.test(s),
      hasAutofill: /autofill/i.test(s),
      hasSessions: /session/i.test(s),
      head: s.replace(/\\s+/g, ' ').slice(0, 400),
    });
  })()`,
})
say({ step: 'probe', value: probe.js })

// 再抓一次纯文本，把可能的数据类型列出前 40 行
const lines = await readTab(t.tabKey, {
  screenshot: false, text: false,
  js: `(() => (document.body.innerText || '').split('\\n').map((x) => x.trim()).filter(Boolean).slice(0, 60).join(' | '))()`,
})
say({ step: 'lines', text: String(lines.js || '').slice(0, 900) })

await readTab(t.tabKey, { screenshot: false, text: false, close: true }).catch(() => {})
closeBrowser()
