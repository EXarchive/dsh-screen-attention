// 本地 cookie 只读复用回归：
//   v10 profile（Chrome）→ 必须解得出且能注入后台浏览器
//   v20 profile（Edge）→ 必须被明确标为 blocked（这是已知硬限制，不是 bug）
// 判据：Chrome 解密数 >0 且 0 失败；Edge blockedV20 >0；注入后浏览器里能读到同名 cookie。
import { spawn, execFileSync } from 'node:child_process'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { existsSync } from 'node:fs'
import { chromiumProfiles, scanProfile, readCookies, toCdpCookies } from '../permission-plugin/lib/cookies.mjs'
import { setCookies, listCookies, clearCookies, closeBrowser } from '../permission-plugin/lib/browser.mjs'

// 独立隔离 profile：活着的 DSH 常驻实例占着 out/web-iso
process.env.SA_WEB_ISO = (process.env.TEMP || '.') + '\\sa-web-iso-test'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const EXE = process.env.SA_HELPER_EXE || join(ROOT, 'helper', 'bin', 'Release', 'net9.0-windows10.0.19041.0', 'SaHelper.exe')

let pass = 0, fail = 0
const ok = (n, c, e = '') => { if (c) { pass++; console.log('  ✓', n, e) } else { fail++; console.log('  ✗', n, e) } }

function helperClient() {
  if (!existsSync(EXE)) throw new Error('helper 未编译: ' + EXE)
  const p = spawn(EXE, [], { stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true })
  let buf = '', seq = 0
  const pend = new Map()
  p.stdout.on('data', (d) => {
    buf += d.toString('utf8')
    let i
    while ((i = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1)
      if (!line) continue
      let m; try { m = JSON.parse(line) } catch { continue }
      if (m.event) continue
      const r = pend.get(m.id); if (r) { pend.delete(m.id); r(m) }
    }
  })
  const call = (cmd, params = {}, timeout = 10000) => new Promise((res, rej) => {
    const id = ++seq; pend.set(id, res)
    p.stdin.write(JSON.stringify({ id, cmd, ...params }) + '\n')
    setTimeout(() => { if (pend.has(id)) { pend.delete(id); rej(new Error('helper 超时: ' + cmd)) } }, timeout)
  })
  return { call, kill: () => { try { p.stdin.write(JSON.stringify({ id: 0, cmd: 'shutdown' }) + '\n') } catch {} ; try { p.kill() } catch {} } }
}

const H = helperClient()
const dpapi = async (b64) => {
  // helper 的应答统一包在 {id, ok, data} 里
  const r = await H.call('crypto_dpapi', { blobs: [b64] })
  const first = ((r.data && r.data.results) || [])[0]
  return first && first.ok ? first.b64 : null
}

console.log('[1] DPAPI 通道')
const probe = await dpapi(Buffer.from('not-a-blob').toString('base64'))
ok('非法 blob 返回 null（不抛）', probe === null)

console.log('[2] profile 扫描')
const profiles = chromiumProfiles()
ok('至少找到一个 profile', profiles.length > 0, profiles.length + ' 个')
const shadowBefore = Number(String(execFileSync('powershell', ['-NoProfile', '-Command', "(Get-CimInstance Win32_ShadowCopy | Measure-Object).Count"])).trim())
const scanRows = []
for (const p of profiles) {
  const t0 = Date.now()
  const s = scanProfile(p)
  scanRows.push(s)
  console.log('     ', p.browser, p.profile, s.error ? 'ERR ' + String(s.error).slice(0, 70) : `rows=${s.rows} v10=${s.versions.v10} v20=${s.versions.v20} mode=${s.readMode} ${Date.now() - t0}ms`)
}
// 浏览器在跑时 Edge 会独占锁 DB：只读打开与直接复制都会失败，必须靠卷影快照兜住。
// 这条判据就是为了拦住"扫描静默失败、还报『本机没有 v20』"那种假阳性。
ok('每个 profile 都能读到（含被浏览器锁住的）', scanRows.every((s) => !s.error), scanRows.filter((s) => s.error).map((s) => s.browser + ':' + String(s.error).slice(0, 50)).join(' | '))
const shadowAfter = Number(String(execFileSync('powershell', ['-NoProfile', '-Command', "(Get-CimInstance Win32_ShadowCopy | Measure-Object).Count"])).trim())
ok('扫描不留影子卷（VSS 用完即删）', shadowAfter <= shadowBefore, `before=${shadowBefore} after=${shadowAfter}`)

console.log('[3] v10 profile 解密（Chrome）')
const v10Profile = profiles
  .map((p) => ({ p, s: scanProfile(p) }))
  .find((x) => x.s.versions && x.s.versions.v10 > 0)
if (!v10Profile) {
  console.log('  跳过：本机没有 v10 cookie 的 profile')
} else {
  const r = await readCookies(v10Profile.p, { dpapi })
  ok('解密成功', r.ok === true, r.error || '')
  if (r.ok) {
    ok('解出条数 >0', r.cookies.length > 0, `${r.cookies.length} 条`)
    ok('0 条失败', r.stats.failed === 0, JSON.stringify(r.stats))
    ok('coockie 形状合法', r.cookies.every((c) => c.name && c.domain && typeof c.value === 'string'))
  }

  console.log('[4] 注入后台浏览器')
  if (r.ok) {
    const cdp = toCdpCookies(r.cookies)
    const s = await setCookies(cdp)
    ok('setCookies ok', s.ok === true, JSON.stringify(s))
    const host = r.cookies[0].domain.replace(/^\./, '')
    const l = await listCookies(host)
    ok('浏览器里能读到该域的 cookie', (l.cookies || []).length > 0, `${(l.cookies || []).length} 条 / ${host}`)
    await clearCookies()
    closeBrowser()
  } else {
    console.log('  跳过注入：解密失败')
  }
}

console.log('[5] v20 profile 必须被标为 blocked（已知硬限制）')
const v20Profile = profiles
  .map((p) => ({ p, s: scanProfile(p) }))
  .find((x) => x.s.versions && x.s.versions.v20 > 0)
if (!v20Profile) {
  console.log('  跳过：本机没有 v20 cookie 的 profile')
} else {
  const r = await readCookies(v20Profile.p, { dpapi })
  ok('读取流程不报错', r.ok === true, r.error || '')
  if (r.ok) {
    ok('v20 全部计入 blockedV20', r.stats.blockedV20 > 0, `${r.stats.blockedV20} 条被拦`)
    ok('没有把 v20 当明文返回', r.cookies.length === 0, `${r.cookies.length} 条解出`)
  }
}
H.kill()
console.log(`\n结果: ${pass} 通过 / ${fail} 失败`)
process.exit(fail === 0 ? 0 : 1)
