// cookie 获取对账：磁盘真实 profile 覆盖 + 每个 profile 的条数账目 + 特殊列（CHIPS 分区）
import { spawn, execFileSync } from 'node:child_process'
import { readdirSync, existsSync, statSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { DatabaseSync } from 'node:sqlite'
const L = 'file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/'
const { chromiumProfiles, scanProfile, readCookies } = await import(L + 'cookies.mjs')

const LA = process.env.LOCALAPPDATA
const ROOTS = {
  edge: join(LA, 'Microsoft', 'Edge', 'User Data'),
  chrome: join(LA, 'Google', 'Chrome', 'User Data'),
}

// 1) 磁盘上到底有哪些 profile 目录
console.log('=== 1. 磁盘上的 profile 目录 vs 被扫描到的 ===')
const detected = chromiumProfiles()
for (const [browser, root] of Object.entries(ROOTS)) {
  if (!existsSync(root)) { console.log(browser, 'root MISSING'); continue }
  const dirs = readdirSync(root, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name)
  const candidates = dirs.filter((d) => d === 'Default' || /^Profile \d+$/i.test(d))
  const others = dirs.filter((d) => d !== 'Default' && !/^Profile \d+$/i.test(d))
  const scanned = detected.filter((p) => p.browser === browser).map((p) => p.profile)
  const hasDb = candidates.filter((d) => existsSync(join(root, d, 'Network', 'Cookies')) || existsSync(join(root, d, 'Cookies')))
  console.log(`${browser}: 候选=${JSON.stringify(candidates)} 有库=${JSON.stringify(hasDb)} 扫描到=${JSON.stringify(scanned)}`)
  if (candidates.length !== hasDb.length) console.log(`  ⚠ 候选目录里没有 cookie 库: ${JSON.stringify(candidates.filter((d) => !hasDb.includes(d)))}`)
  if (JSON.stringify(hasDb) !== JSON.stringify(scanned)) console.log('  ⚠ 有库但未被扫描（漏检）')
  const odd = others.filter((d) => !['User Data', 'Guest Profile', 'System Profile', 'Snapshots', 'Crashpad', 'ShaderCache', 'GrShaderCache', 'GraphiteDawnCache', 'component_crx_cache', 'extensions_crx_cache', 'Safe Browsing', 'BrowserMetrics', 'OptimizationHints', 'CertificateRevocation', 'hyphen-data', 'MEIPreload', 'PKIMetadata', 'WidevineCdm', 'ZxcvbnData', 'OptimizationGuideModelDownloading'].includes(d))
  console.log('  未纳入规则的其他目录:', JSON.stringify(odd))
}

// 2) 逐 profile 对账
console.log('\n=== 2. 每个 profile 的条数账目 ===')
const EXE = 'C:\\Users\\Administrator\\PycharmProjects\\dsh-screen-attention\\helper\\bin\\Release\\net9.0-windows10.0.19041.0\\SaHelper.exe'
const p = spawn(EXE, [], { stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true })
let buf = '', seq = 0
const pend = new Map()
p.stdout.on('data', (d) => {
  buf += d.toString('utf8'); let i
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1); if (!line) continue
    let j; try { j = JSON.parse(line) } catch { continue }
    if (j.event) continue
    const r = pend.get(j.id); if (r) { pend.delete(j.id); r(j) }
  }
})
const call = (cmd, params = {}) => new Promise((res) => { const id = ++seq; pend.set(id, res); p.stdin.write(JSON.stringify({ id, cmd, ...params }) + '\n') })
const dpapi = async (b64) => { const r = await call('crypto_dpapi', { blobs: [b64] }); const f = ((r.data && r.data.results) || [])[0]; return f && f.ok ? f.b64 : null }

for (const prof of detected) {
  const sc = scanProfile(prof)
  const r = await readCookies(prof, { dpapi })
  if (!r.ok) { console.log(prof.browser + '/' + prof.profile, 'readCookies 失败:', String(r.error).slice(0, 80)); continue }
  const s = r.stats
  const accounted = s.decrypted + s.plain + s.blockedV20 + s.failed + s.expired + s.filtered
  console.log(`${prof.browser}/${prof.profile}: 行=${s.total} 解出=${s.decrypted} 明文=${s.plain} v20=${s.blockedV20} 过期=${s.expired} 失败=${s.failed} → cookies=${r.cookies.length}`)
  console.log(`  账目核对: ${accounted} / ${s.total} ${accounted === s.total ? '✓ 无静默丢失' : '⚠ 差 ' + (s.total - accounted)}`)
}

// 3) 特殊列：CHIPS 分区 cookie / 明文值共存
console.log('\n=== 3. 特殊列检查 ===')
for (const prof of detected) {
  try {
    const dir = mkdtempSync(join(tmpdir(), 'sa-audit-'))
    const out = join(dir, 'C')
    // 直接读（浏览器没锁 chrome；edge 锁了就用 vss 版本——这里只统计，允许失败）
    let db
    try { db = new DatabaseSync(prof.dbPath, { readOnly: true }) } catch { rmSync(dir, { recursive: true, force: true }); console.log(prof.browser + '/' + prof.profile, '读不到（锁），跳过特殊列统计'); continue }
    const partitioned = db.prepare("SELECT COUNT(*) n FROM cookies WHERE top_frame_site_key IS NOT NULL AND top_frame_site_key != ''").get().n
    const bothVal = db.prepare("SELECT COUNT(*) n FROM cookies WHERE value != '' AND length(encrypted_value) > 0").get().n
    const noExp = db.prepare('SELECT COUNT(*) n FROM cookies WHERE has_expires = 0').get().n
    const samesite = db.prepare('SELECT samesite, COUNT(*) n FROM cookies GROUP BY samesite').all()
    console.log(`${prof.browser}/${prof.profile}: CHIPS分区=${partitioned} 明文与密文并存=${bothVal} 会话cookie=${noExp} samesite分布=${JSON.stringify(samesite.map(x => x.samesite + ':' + x.n))}`)
    db.close()
    rmSync(dir, { recursive: true, force: true })
  } catch (e) { console.log(prof.browser, '特殊列统计失败', String(e.message).slice(0, 60)) }
}
p.stdin.write(JSON.stringify({ id: 0, cmd: 'shutdown' }) + '\n'); try { p.kill() } catch {}
