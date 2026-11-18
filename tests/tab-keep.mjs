// keep-tab 回归：跨调用保持同一页面（扫码登录类流程的地基）
const REPO = 'file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/'
process.env.SA_WEB_ISO = (process.env.TEMP || '.') + '\\sa-web-iso-test-tab'
const { openTab, readTab, listTabs, closeTab, closeBrowser } = await import(REPO + 'browser.mjs')

let pass = 0, fail = 0
const ok = (n, c, e = '') => { if (c) { pass++; console.log('  ✓', n, e) } else { fail++; console.log('  ✗', n, e) } }

console.log('[1] 开一个不关闭的标签页')
const t = await openTab({ url: 'https://example.com/', waitMs: 800, screenshot: true })
ok('openTab ok', t.ok === true, t.error || '')
ok('有 tabKey', /^t\d+$/.test(t.tabKey || ''), String(t.tabKey))
ok('有截图', !!t.path)
ok('kept=true', t.kept === true)

console.log('[2] 同一标签页再读（证明没被关掉、状态延续）')
await new Promise((r) => setTimeout(r, 300))
const r2 = await readTab(t.tabKey, { screenshot: false })
ok('readTab ok', r2.ok === true, r2.error || '')
ok('仍是同一 URL', r2.url === t.url, r2.url)
// 在页面里写状态，再读一次确认同一个 DOM 实例（关掉重开就丢）
await readTab(t.tabKey, { screenshot: false, text: false, js: 'window.__saMark = "kept"' })
const r3 = await readTab(t.tabKey, { screenshot: false, text: false, js: 'window.__saMark || "LOST"' })
ok('页面内状态延续（同一实例）', r3.js === 'kept', JSON.stringify(r3.js))

console.log('[3] waitFor 轮询')
const t2 = await openTab({ url: 'about:blank', waitMs: 300, screenshot: false, text: false })
await readTab(t2.tabKey, { screenshot: false, text: false, js: 'setTimeout(()=>{window.__done=true},1500); "armed"' })
const w = await readTab(t2.tabKey, { screenshot: false, text: false, waitFor: 'window.__done === true', waitForMs: 8000 })
ok('waitFor 等到了条件', w.ok === true, w.error || '')

console.log('[4] 列表与关闭')
const l = listTabs()
ok('listTabs 有 2 个', l.count === 2, JSON.stringify(l.tabs.map((x) => x.tabKey)))
const c1 = await closeTab(t.tabKey)
ok('closeTab ok', c1.ok === true && c1.remain === 1, JSON.stringify(c1))
const c2 = await closeTab(t2.tabKey)
ok('关完剩 0', c2.remain === 0, JSON.stringify(c2))
const gone = await readTab(t.tabKey, { screenshot: false })
ok('已关的 tabKey 报错而不是挂住', gone.ok === false && /没有这个标签页/.test(gone.error || ''), gone.error || '')

closeBrowser()
console.log(`\n结果: ${pass} 通过 / ${fail} 失败`)
process.exit(fail === 0 ? 0 : 1)
