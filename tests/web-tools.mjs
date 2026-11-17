// 工具级回归：走真实注册表调用 saperm_web / saperm_cookies（mock ctx + 真 helper + 真无头浏览器）
import { existsSync } from 'node:fs'

const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = {
  tools: { register(def) { tools.set(def.name, def); return def } },
  on() {}, effect(fn) { fn() }, injectToSession() {},
  agents: { currentInitiator: () => undefined, get: () => undefined },
  log() {}, warn() {},
}
const dispose = mod.apply(ctx)

let pass = 0, fail = 0
const ok = (n, c, e = '') => { if (c) { pass++; console.log('  ✓', n, e) } else { fail++; console.log('  ✗', n, e) } }

ok('saperm_web 已注册', tools.has('saperm_web'))
ok('saperm_cookies 已注册', tools.has('saperm_cookies'))
console.log('工具总数', tools.size)

console.log('[1] saperm_web 首次打开')
const w1 = await tools.get('saperm_web').execute({ url: 'https://example.com/', waitMs: 1000 })
ok('ok=true', w1.ok === true, w1.error || '')
ok('有截图文件', !!w1.path && existsSync(w1.path), w1.path)
ok('有文本', (w1.text || '').length > 40, (w1.text || '').length + ' 字')
ok('reused=false（冷启动）', w1.reused === false, 'bootMs=' + w1.bootMs)

console.log('[2] saperm_web 复用')
const w2 = await tools.get('saperm_web').execute({ url: 'https://example.org/', waitMs: 200, screenshot: false })
ok('ok=true', w2.ok === true, w2.error || '')
ok('reused=true', w2.reused === true, 'bootMs=' + w2.bootMs)

console.log('[3] saperm_web js 参数')
const w3 = await tools.get('saperm_web').execute({ url: 'https://example.com/', waitMs: 200, screenshot: false, text: false, js: 'document.title' })
ok('js 返回标题', /Example/i.test(String(w3.js || '')), JSON.stringify(w3.js))

console.log('[4] saperm_cookies scan')
const sc = await tools.get('saperm_cookies').execute({ action: 'scan' })
ok('ok=true', sc.ok === true)
ok('有 profile', (sc.profiles || []).length > 0, JSON.stringify((sc.profiles || []).map((p) => p.browser + '/' + p.profile + ' v10=' + (p.versions ? p.versions.v10 : '?') + ' v20=' + (p.versions ? p.versions.v20 : '?'))))

console.log('[5] saperm_cookies import（只取可解的那类）')
const imp = await tools.get('saperm_cookies').execute({ action: 'import', browser: 'chrome', host: 'google' })
ok('ok=true 且注入了 cookie', imp.ok === true && imp.injected > 0, JSON.stringify({ injected: imp.injected, blocked: imp.blockedV20 }))

console.log('[6] saperm_cookies list')
const l = await tools.get('saperm_cookies').execute({ action: 'list', host: 'google' })
ok('能列出 cookie', (l.cookies || []).length > 0, (l.cookies || []).length + ' 条')

console.log('[7] clear / close')
const c = await tools.get('saperm_cookies').execute({ action: 'clear' })
ok('clear ok', c.ok === true, c.error || '')
const l2 = await tools.get('saperm_cookies').execute({ action: 'list', host: 'google' })
ok('清空后为 0', (l2.cookies || []).length === 0, String((l2.cookies || []).length))
const cl = await tools.get('saperm_cookies').execute({ action: 'close' })
ok('close ok', cl.ok === true, JSON.stringify(cl))

if (typeof dispose === 'function') { try { await dispose() } catch {} }
console.log(`\n结果: ${pass} 通过 / ${fail} 失败`)
process.exit(fail === 0 ? 0 : 1)
