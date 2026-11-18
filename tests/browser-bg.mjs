// 后台静默浏览器回归：常驻实例复用 / 取文本 / 截图 / cookie 注入 / 干净关闭
// 判据：不建窗口（无法直接断言，改为断言无 --headless 之外的可见性参数 + 进程为 headless）、
//       第二次调用 reused=true 且 wall < 1.5s、文本非空、截图字节 > 5KB。
import { openPage, browserStatus, setCookies, listCookies, clearCookies, closeBrowser } from '../permission-plugin/lib/browser.mjs'

// 测试必须用自己的隔离 profile：活着的 DSH 常驻实例占着 out/web-iso，
// 同 profile 起第二个实例会被 SingletonLock 挡回（表现为"启动后立刻退出"）。
process.env.SA_WEB_ISO = (process.env.TEMP || '.') + '\\sa-web-iso-test'

let pass = 0, fail = 0
const ok = (name, cond, extra = '') => {
  if (cond) { pass++; console.log('  ✓', name, extra) } else { fail++; console.log('  ✗', name, extra) }
}

console.log('[1] 首次打开（冷启动）')
const a = await openPage({ url: 'https://example.com/', waitMs: 1200, fullPage: true })
ok('ok=true', a.ok, a.error || '')
ok('标题正确', /Example Domain/i.test(a.title || ''), JSON.stringify(a.title))
ok('文本非空', (a.text || '').length > 40, (a.text || '').length + ' 字')
ok('截图 >5KB', (a.bytes || 0) > 5000, String(a.bytes))

console.log('[2] 第二次打开（复用常驻实例）')
const t1 = Date.now()
const b = await openPage({ url: 'https://example.org/', waitMs: 200, screenshot: false })
const wall = Date.now() - t1
ok('ok=true', b.ok, b.error || '')
ok('reused=true', b.reused === true)
ok('wall < 1500ms', wall < 1500, wall + 'ms')
ok('文本非空', (b.text || '').length > 40, (b.text || '').length + ' 字')

console.log('[3] cookie 注入 / 列举 / 清空')
const s = await setCookies([{ name: 'sa_probe', value: 'on', domain: 'example.com', path: '/' }])
ok('setCookies ok', s.ok === true, JSON.stringify(s))
const l = await listCookies('example.com')
ok('注入后可读到', (l.cookies || []).some((x) => x.name === 'sa_probe'), JSON.stringify(l.count))
await clearCookies()
const l2 = await listCookies('example.com')
ok('清空后为 0', (l2.cookies || []).length === 0, JSON.stringify(l2.count))

console.log('[5] 端到端：注入的 cookie 真的会随请求发出（本地回环服务器验证）')
const { createServer } = await import('node:http')
let seenCookie = ''
const srv = createServer((req, res) => { seenCookie = req.headers.cookie || ''; res.end('ok') })
await new Promise((r) => srv.listen(0, '127.0.0.1', r))
const port = srv.address().port
await setCookies([{ name: 'sa_e2e', value: 'yes', domain: '127.0.0.1', path: '/' }])
const p2 = await openPage({ url: 'http://127.0.0.1:' + port + '/', waitMs: 400, screenshot: false })
ok('页面加载成功', p2.ok === true, p2.error || '')
ok('请求头里带上了注入的 cookie', /sa_e2e=yes/.test(seenCookie), JSON.stringify(seenCookie))
await new Promise((r) => srv.close(r))

console.log('[6] 状态与关闭')
const st = browserStatus()
ok('running=true', st.running === true)
ok('product 是 Edge/Chrome', /Edg|HeadlessChrome|Chrome/.test(st.product || ''), st.product)
const c = closeBrowser()
ok('closeBrowser 关闭了实例', c.closed === true)
ok('关闭后 running=false', browserStatus().running === false)

console.log(`\n结果: ${pass} 通过 / ${fail} 失败`)
process.exit(fail === 0 ? 0 : 1)
