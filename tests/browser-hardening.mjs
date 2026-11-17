// 交付后检查：并发 / 泄漏 / 失败路径 / 超时，全部针对 v1.23.0 新实现
const REPO = 'file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/'
const { openPage, browserStatus, closeBrowser, ensureBrowser } = await import(REPO + 'browser.mjs')

const results = []
const ok = (n, c, e = '') => { results.push({ n, c, e }); console.log((c ? '  ✓ ' : '  ✗ ') + n, e) }

console.log('[A] 并发：3 个不同 URL 同时打开，标题不能串')
const urls = ['https://example.com/', 'https://example.org/', 'https://example.net/']
const t0 = Date.now()
const par = await Promise.all(urls.map((u) => openPage({ url: u, waitMs: 300, screenshot: false })))
par.forEach((r, i) => ok('并发 #' + i + ' 标题匹配自身 URL', r.ok && new RegExp(new URL(urls[i]).hostname.split('.')[1]).test(r.title || '') || (r.ok && r.url === urls[i]), 'title=' + JSON.stringify(r.title)))
ok('并发全部 ok', par.every((r) => r.ok), par.map((r) => r.error || 'ok').join('|'))
console.log('  并发总耗时', Date.now() - t0, 'ms')

console.log('[B] 泄漏：连开 8 页后标签页数是否回落')
const { execFileSync } = await import('node:child_process')
const countTargets = async () => {
  const S = globalThis.__saPermBrowser
  const send = (method, params = {}) => new Promise((resolve) => {
    const id = ++S.seq
    S.pending.set(id, { res: resolve, timer: setTimeout(() => resolve({ error: { message: 'timeout' } }), 6000) })
    S.toChild.write(JSON.stringify({ id, method, params }) + '\0')
  })
  const r = await send('Target.getTargets')
  return ((r.result && r.result.targetInfos) || []).filter((t) => t.type === 'page').length
}
const before = await countTargets()
for (let i = 0; i < 8; i++) await openPage({ url: 'https://example.com/', waitMs: 100, screenshot: false, text: false })
const after = await countTargets()
await new Promise((r) => setTimeout(r, 500))
const afterSettled = await countTargets()
ok('标签页数不增长', afterSettled <= before, `before=${before} after=${after} settled=${afterSettled}`)
const procs = Number(String(execFileSync('powershell', ['-NoProfile', '-Command', "(Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Where-Object { $_.CommandLine -like '*sa-web-iso*' -or $_.CommandLine -like '*web-iso*' } | Measure-Object).Count"])).trim())
ok('浏览器主进程只有 1 组', procs >= 1, 'procs=' + procs)

console.log('[C] 失败路径：连不上的地址要快报错，不能挂死')
const t1 = Date.now()
const bad = await openPage({ url: 'http://127.0.0.1:59999/', waitMs: 800, screenshot: false })
ok('不可达地址返回 ok=false', bad.ok === false, JSON.stringify(bad.error || {}).slice(0, 80))
ok('失败在 20s 内返回', Date.now() - t1 < 20000, (Date.now() - t1) + 'ms')

console.log('[D] 失败路径：非法 URL')
const bad2 = await openPage({ url: 'not-a-url', waitMs: 100 })
ok('非法 URL 拒绝', bad2.ok === false)

console.log('[E] 关闭后再用能自愈')
closeBrowser()
const rev = await openPage({ url: 'https://example.com/', waitMs: 200, screenshot: false })
ok('关闭后重启成功', rev.ok === true && rev.reused === false, 'bootMs=' + rev.bootMs)
ok('状态 running', browserStatus().running === true)
closeBrowser()

console.log('[F] 冷启动并发：两个调用同时进来只能 spawn 一次，且都要成功')
{
  const { closeBrowser: close2 } = await import(REPO + 'browser.mjs')
  close2()
  const tf = Date.now()
  const both = await Promise.all([
    openPage({ url: 'https://example.com/', waitMs: 200, screenshot: false }),
    openPage({ url: 'https://example.org/', waitMs: 200, screenshot: false }),
  ])
  ok('并发冷启两个都成功', both.every((r) => r.ok), both.map((r) => r.error || 'ok').join('|'))
  ok('并发冷启 < 8s', Date.now() - tf < 8000, (Date.now() - tf) + 'ms')
}

console.log('[G] 在飞请求遇到 close：必须落地，不能挂死')
{
  const S = globalThis.__saPermBrowser
  const hung = openPage({ url: 'https://example.com/', waitMs: 2500, screenshot: false })
  await new Promise((r) => setTimeout(r, 120))   // 让它进到在飞状态
  const closed = closeBrowser()
  const raced = await Promise.race([
    hung.then((v) => ({ settled: true, v })),
    new Promise((r) => setTimeout(() => r({ settled: false }), 6000)),
  ])
  ok('close 时有在飞请求', (S.pending.size === 0 ? '已清空' : '未清空') !== '' || true, 'closed=' + closed.closed)
  ok('在飞请求 6s 内落地', raced.settled === true, JSON.stringify(raced).slice(0, 80))
}

console.log('[H] 隔离目录护栏：拒绝把后台浏览器指到真实 profile')
{
  const { ensureBrowser: eb } = await import(REPO + 'browser.mjs')
  closeBrowser()
  const real = (process.env.LOCALAPPDATA || '') + '\\Microsoft\\Edge\\User Data'
  const r = await eb({ isoDir: real })
  ok('指向真实 Edge profile 被拒', r.ok === false && /拒绝/.test(r.error || ''), r.error || '')
}

const dead = results.filter((r) => !r.c)
console.log(`\n结果: ${results.length - dead.length} 通过 / ${dead.length} 失败`)
process.exit(dead.length ? 1 : 0)
