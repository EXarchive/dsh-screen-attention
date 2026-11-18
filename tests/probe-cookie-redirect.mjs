// 实验：把真实 Edge 的 cookie **行数据**（含 v20 密文）+ os_crypt 密钥封装，搬进隔离 profile，
// 看浏览器自己能不能解开。全程只读真实库（VSS 快照直读），只写我们自己的 out/web-iso。
// 结论三种：解出来（可用）/ 行被删（ABE 绑定了 user-data-dir）/ 留着但无值。
import { DatabaseSync } from 'node:sqlite'
import { readFileSync, writeFileSync, copyFileSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import { execFileSync } from 'node:child_process'

const REAL_DB = process.env.LOCALAPPDATA + '\\Microsoft\\Edge\\User Data\\Default\\Network\\Cookies'
const REAL_LS = process.env.LOCALAPPDATA + '\\Microsoft\\Edge\\User Data\\Local State'
const ISO = 'C:\\Users\\Administrator\\PycharmProjects\\dsh-screen-attention\\out\\web-iso'
const ISO_DB = join(ISO, 'Default', 'Network', 'Cookies')
const ISO_LS = join(ISO, 'Local State')
const BAK = 'C:\\Users\\Administrator\\PycharmProjects\\dsh-screen-attention\\out\\backup-before-redirect'
const say = (o) => console.log(JSON.stringify(o))

// 1) 备份隔离 profile 的库与 Local State（可回滚）
execFileSync('pwsh', ['-NoProfile', '-Command', `New-Item -ItemType Directory -Force -Path '${BAK}' | Out-Null; Copy-Item -LiteralPath '${ISO_DB}' -Destination '${BAK}\\Cookies' -Force; Copy-Item -LiteralPath '${ISO_LS}' -Destination '${BAK}\\Local State' -Force`], { windowsHide: true })
say({ step: 'backup', dir: BAK, cookies: existsSync(BAK + '\\Cookies'), localState: existsSync(BAK + '\\Local State') })

// 2) 真实库：VSS 直读（不复制）
const ps = [
  "$ErrorActionPreference='Stop'",
  `$r = ([wmiclass]'Win32_ShadowCopy').Create('C:\\','ClientAccessible')`,
  `$sc = Get-CimInstance Win32_ShadowCopy | Where-Object { $_.ID -eq $r.ShadowID }`,
  `Write-Output ($r.ShadowID + '|' + $sc.DeviceObject)`,
].join('\n')
const [sid, device] = String(execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', ps], { encoding: 'utf8', timeout: 90000, windowsHide: true })).trim().split('|')
const shadowDb = device + '\\' + REAL_DB.slice(3)
let rows, cols, bigCols
try {
  const db = new DatabaseSync(shadowDb, { readOnly: true })
  const info = db.prepare('PRAGMA table_info(cookies)').all()
  cols = info.map((c) => c.name)
  // creation_utc/expires_utc 超过 JS 安全整数（实测 RangeError），按文本读、按 INTEGER 写回
  bigCols = info.filter((c) => /integer/i.test(String(c.type || ''))).map((c) => c.name)
  const sel = cols.map((c) => (bigCols.includes(c) ? `CAST(${c} AS TEXT) AS ${c}` : c)).join(',')
  rows = db.prepare(`SELECT ${sel} FROM cookies`).all()
  db.close()
} finally {
  if (/^\{[0-9A-Fa-f-]{36}\}$/.test(sid)) {
    try { execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', `$null = & vssadmin delete shadows /shadow="${sid}" /quiet 2>&1`], { timeout: 60000, windowsHide: true }) } catch {}
  }
}
say({ step: 'read-real', rows: rows.length, cols: cols.length })

// 3) 把真实 os_crypt 写进隔离 profile 的 Local State（其它字段保留）
const realLs = JSON.parse(readFileSync(REAL_LS, 'utf8'))
const isoLs = JSON.parse(readFileSync(ISO_LS, 'utf8'))
isoLs.os_crypt = realLs.os_crypt
writeFileSync(ISO_LS, JSON.stringify(isoLs))
say({ step: 'ls-patched', hasEnc: !!(isoLs.os_crypt && isoLs.os_crypt.encrypted_key), hasAbe: !!(isoLs.os_crypt && isoLs.os_crypt.app_bound_encrypted_key) })

// 4) 把行数据写进隔离 profile 的库（先清空）
const isoDb = new DatabaseSync(ISO_DB)
const isoCols = isoDb.prepare('PRAGMA table_info(cookies)').all().map((c) => c.name)
const shared = cols.filter((c) => isoCols.includes(c))
isoDb.exec('DELETE FROM cookies')
const ins = isoDb.prepare(`INSERT INTO cookies (${shared.join(',')}) VALUES (${shared.map((c) => (bigCols.includes(c) ? 'CAST(? AS INTEGER)' : '?')).join(',')})`)
let inserted = 0
isoDb.exec('BEGIN')
for (const r of rows) {
  try { ins.run(...shared.map((c) => (r[c] === undefined ? null : r[c]))); inserted++ } catch {}
}
isoDb.exec('COMMIT')
const cnt = isoDb.prepare('SELECT COUNT(*) n FROM cookies').get().n
const ver = isoDb.prepare("SELECT hex(substr(encrypted_value,1,3)) v, COUNT(*) n FROM cookies GROUP BY v").all()
isoDb.close()
say({ step: 'rows-written', inserted, inDb: cnt, versions: ver.map((x) => ({ v: Buffer.from(x.v, 'hex').toString(), n: x.n })) })

// 5) 起隔离 profile 的浏览器，看它能不能解
const B = 'file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/'
const { ensureBrowser, listCookies, closeBrowser } = await import(B + 'browser.mjs')
const boot = await ensureBrowser({})
say({ step: 'browser', ok: boot.ok, product: boot.product, error: boot.error })
if (boot.ok) {
  const all = await listCookies()
  const bili = await listCookies('bilibili')
  say({ step: 'after-boot', total: all.count, bilibili: bili.count, names: (bili.cookies || []).map((c) => c.name).slice(0, 10) })
  closeBrowser()
}
