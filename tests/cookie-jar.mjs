// cookie jar 回归：save → clear → restore → 条数一致；jar 必须是 DPAPI 加密（盘上不能见明文）
import { spawn } from 'node:child_process'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { existsSync, readFileSync, rmSync } from 'node:fs'
const REPO = 'file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/'
process.env.SA_WEB_ISO = (process.env.TEMP || '.') + '\\sa-web-iso-test-jar'
const { setCookies, listCookies, getCookiesFull, clearCookies, closeBrowser } = await import(REPO + 'browser.mjs')
const { saveJar, loadJar, listJars, removeJar } = await import(REPO + 'cookies.mjs')

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const EXE = join(ROOT, 'helper', 'bin', 'Release', 'net9.0-windows10.0.19041.0', 'SaHelper.exe')
const DIR = join(process.env.TEMP || '.', 'sa-jar-test')
try { rmSync(DIR, { recursive: true, force: true }) } catch {}

let pass = 0, fail = 0
const ok = (n, c, e = '') => { if (c) { pass++; console.log('  ✓', n, e) } else { fail++; console.log('  ✗', n, e) } }

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
const protect = async (b64) => { const r = await call('crypto_protect', { blobs: [b64] }); const f = ((r.data && r.data.results) || [])[0]; return f && f.ok ? f.b64 : null }

console.log('[1] 造一份 cookie 并保存')
const seed = [
  { name: 'SESSDATA', value: 'fake-sess-abc123', domain: '.example.com', path: '/', secure: true, httpOnly: true },
  { name: 'bili_jct', value: 'fake-jct-xyz', domain: '.example.com', path: '/', secure: false, httpOnly: false },
]
ok('注入种子 cookie', (await setCookies(seed)).ok === true)
const full = await getCookiesFull('example.com')
ok('拿到完整值（不是只有长度）', full.ok && full.cookies.every((c) => typeof c.value === 'string' && c.value.length > 0), `${full.cookies.length} 条`)
const saved = await saveJar({ dir: DIR, name: 'unit', cookies: full.cookies, dpapi: protect })
ok('saveJar ok', saved.ok === true, saved.error || `${saved.bytes} 字节`)
ok('jar 文件存在', saved.path && existsSync(saved.path))

console.log('[2] 盘上是密文（不能出现明文值）')
const raw = readFileSync(saved.path, 'utf8')
ok('jar 里看不到 fake-sess-abc123', !raw.includes('fake-sess-abc123'))
ok('jar 里看不到 cookie 名 SESSDATA', !raw.includes('SESSDATA'))

console.log('[3] loadJar 还原内容')
const jar = await loadJar({ dir: DIR, name: 'unit', dpapi })
ok('loadJar ok', jar.ok === true, jar.error || '')
ok('条数一致', jar.count === full.cookies.length, `${jar.count} vs ${full.count}`)
ok('值也一致', jar.cookies.find((c) => c.name === 'SESSDATA')?.value === 'fake-sess-abc123')

console.log('[4] clear → restore 走通')
await clearCookies()
ok('清空后为 0', (await listCookies('example.com')).cookies.length === 0)
const restored = await setCookies(jar.cookies)
ok('restore 成功', restored.ok === true, JSON.stringify({ set: restored.set, rejected: restored.rejectedCount }))
const after = await listCookies('example.com')
ok('恢复后能读到 SESSDATA', (after.cookies || []).some((c) => c.name === 'SESSDATA'), `${after.cookies.length} 条`)

console.log('[5] 列表 / 删除 / 错名字')
ok('listJars 有 1 个', listJars(DIR).count === 1, JSON.stringify(listJars(DIR).jars.map((j) => j.name)))
ok('错名字 load 报错不抛', (await loadJar({ dir: DIR, name: 'nope', dpapi })).ok === false)
ok('forget 删除', removeJar({ dir: DIR, name: 'unit' }).ok === true)
ok('删后列表为空', listJars(DIR).count === 0)

await clearCookies()
closeBrowser()
p.stdin.write(JSON.stringify({ id: 0, cmd: 'shutdown' }) + '\n'); try { p.kill() } catch {}
rmSync(DIR, { recursive: true, force: true })
console.log(`\n结果: ${pass} 通过 / ${fail} 失败`)
process.exit(fail === 0 ? 0 : 1)
