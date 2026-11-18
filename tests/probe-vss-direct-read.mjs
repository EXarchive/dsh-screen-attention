// 探针：不复制文件，直接在卷影快照路径上只读打开 Edge 的 Cookies，读 bilibili cookie 的元数据
import { execFileSync } from 'node:child_process'
import { DatabaseSync } from 'node:sqlite'

const REL = 'Users\\Administrator\\AppData\\Local\\Microsoft\\Edge\\User Data\\Default\\Network\\Cookies'

const psCreate = `
$ErrorActionPreference='Stop'
$r = ([wmiclass]'Win32_ShadowCopy').Create('C:\\','ClientAccessible')
if ($r.ReturnValue -ne 0) { throw "rc=$($r.ReturnValue)" }
$sc = Get-CimInstance Win32_ShadowCopy | Where-Object { $_.ID -eq $r.ShadowID }
Write-Output ($r.ShadowID + '|' + $sc.DeviceObject)
`
const info = execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', psCreate], { encoding: 'utf8', timeout: 60000 }).trim()
const [id, device] = info.split('|')
console.log('snapshot', id, device)

const shadowPath = device + '\\' + REL
let opened = 'no'
try {
  const db = new DatabaseSync(shadowPath, { readOnly: true })
  opened = 'yes'
  const n = db.prepare('SELECT COUNT(*) n FROM cookies').get().n
  console.log('直接在快照上打开成功，rows=', n)
  const rows = db.prepare("SELECT host_key, name, CAST(expires_utc AS TEXT) exp, is_secure, is_httponly, hex(substr(encrypted_value,1,3)) ver FROM cookies WHERE host_key LIKE '%bilibili%'").all()
  console.log('bilibili cookie 条数', rows.length)
  for (const r of rows) console.log(' ', r.name.padEnd(22), r.host_key.padEnd(18), Buffer.from(r.ver, 'hex').toString(), 'secure=' + r.is_secure, 'httpOnly=' + r.is_httponly)
  db.close()
} catch (e) {
  console.log('打开失败:', String(e.message).slice(0, 160))
} finally {
  try { execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', `$null = & vssadmin delete shadows /shadow="${id}" /quiet 2>&1`], { timeout: 60000 }) } catch {}
  console.log('已删快照；当前快照数', String(execFileSync('powershell', ['-NoProfile', '-Command', '(Get-CimInstance Win32_ShadowCopy | Measure-Object).Count'], { encoding: 'utf8' })).trim())
}
