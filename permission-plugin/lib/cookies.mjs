// 本地 Chromium cookie 只读取证/导入。
//
// 机制（实测，2026-11-18）：
//   v10 = DPAPI(当前用户) 包住 32 字节 AES 密钥 + AES-256-GCM(IV|密文|tag)  → 用户态可解（Chrome 73/73 全解）
//   v20 = App-Bound：密钥先被 SYSTEM DPAPI 包一层，再交给 elevation_service.exe（内置密钥）解，
//         且 COM IElevator::DecryptData 会校验调用方路径（非浏览器路径 → 事件 257、不返回数据）。
//         → 用户态无解；把 profile 复制到别处让浏览器自己解也不行（实测启动即把 1073 条全删）。
//
// 因此：v10 走"只读解密 + 注入后台浏览器"；v20 只能在该隔离 profile 里由用户登录一次。
import { DatabaseSync } from 'node:sqlite'
import { existsSync, readFileSync, readdirSync, copyFileSync, mkdtempSync, statSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { createDecipheriv, createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'

const EPOCH_DELTA_SEC = 11644473600n  // 1601-01-01 → 1970-01-01

export function chromiumProfiles() {
  const la = process.env.LOCALAPPDATA || ''
  const roots = [
    ['edge', join(la, 'Microsoft', 'Edge', 'User Data')],
    ['chrome', join(la, 'Google', 'Chrome', 'User Data')],
    ['chrome-beta', join(la, 'Google', 'Chrome Beta', 'User Data')],
    ['brave', join(la, 'BraveSoftware', 'Brave-Browser', 'User Data')],
  ]
  const out = []
  for (const [browser, root] of roots) {
    if (!root || !existsSync(root)) continue
    const localStatePath = join(root, 'Local State')
    let entries = []
    try { entries = readdirSync(root, { withFileTypes: true }) } catch { continue }
    for (const e of entries) {
      if (!e.isDirectory()) continue
      if (e.name !== 'Default' && !/^Profile \d+$/i.test(e.name)) continue
      const dbPath = join(root, e.name, 'Network', 'Cookies')
      const legacy = join(root, e.name, 'Cookies')
      const use = existsSync(dbPath) ? dbPath : (existsSync(legacy) ? legacy : null)
      if (!use) continue
      let size = 0
      try { size = statSync(use).size } catch {}
      out.push({ browser, profile: e.name, dbPath: use, localStatePath, size })
    }
  }
  return out
}

/**
 * 在卷影快照上**直接只读打开**（不复制文件）。Edge 运行时独占锁住 Cookies 库：
 * readOnly 打开报 SQLITE_CANTOPEN、copyFileSync 报 EBUSY（实测 2026-11-18），
 * 只有 VSS 快照能绕过 —— 快照读的是块层，跟进程锁无关。
 * 快照用完必须删，否则每扫一次就留一个影子卷。
 */
function vssOpen(dbPath) {
  const m = /^([A-Za-z]:)\\(.*)$/.exec(dbPath)
  if (!m) throw new Error('不支持的路径: ' + dbPath)
  const drive = m[1]
  const rel = m[2]
  const ps = [
    "$ErrorActionPreference='Stop'",
    `$r = ([wmiclass]'Win32_ShadowCopy').Create('${drive}\\','ClientAccessible')`,
    `if ($r.ReturnValue -ne 0) { throw "shadow create rc=$($r.ReturnValue)" }`,
    `$sc = Get-CimInstance Win32_ShadowCopy | Where-Object { $_.ID -eq $r.ShadowID }`,
    `Write-Output ($r.ShadowID + '|' + $sc.DeviceObject)`,
  ].join('\n')
  const text = String(execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', ps], { encoding: 'utf8', timeout: 90000, windowsHide: true })).trim()
  const [id, device] = text.split('|')
  if (!id || !device) throw new Error('卷影创建失败: ' + text.slice(0, 120))
  const shadowPath = device + '\\' + rel

  const deleteShadow = () => {
    // 只能删自己这一个（实测 2026-11-18：不带引号插值时 /shadow=$id 会退化，
    // 变成"删掉全部影子卷"，把系统还原点一起干掉）。三条护栏：格式校验 + 引号 + 只删本 id。
    if (!/^\{[0-9A-Fa-f-]{36}\}$/.test(id)) return
    try { execFileSync('pwsh', ['-NoProfile', '-NonInteractive', '-Command', `$null = & vssadmin delete shadows /shadow="${id}" /quiet 2>&1`], { timeout: 60000, windowsHide: true }) } catch {}
  }

  try {
    const db = new DatabaseSync(shadowPath, { readOnly: true })
    return { db, mode: 'vss', cleanup: () => { try { db.close() } catch {}; deleteShadow() }, shadowPath }
  } catch (e) {
    deleteShadow()
    throw e
  }
}

// 正在运行的浏览器会独占锁 DB；三级回落：只读打开 → 直接复制 → 卷影快照直读。
// copy 分支用完要清临时目录 —— 那是一份完整的加密 cookie 库，别留在 %TEMP%。
function openCookies(dbPath) {
  try {
    return { db: new DatabaseSync(dbPath, { readOnly: true }), mode: 'readonly', cleanup: () => {} }
  } catch (e1) {
    // 优先走卷影直读（零文件复制）；快照起不来再退回"复制副本"。
    try {
      return vssOpen(dbPath)
    } catch (e2) {
      let dir = null
      try {
        dir = mkdtempSync(join(tmpdir(), 'sa-cookie-'))
        const dst = join(dir, 'Cookies')
        copyFileSync(dbPath, dst)
        const db = new DatabaseSync(dst, { readOnly: true })
        return {
          db, mode: 'copy',
          cleanup: () => { try { db.close() } catch {}; try { rmSync(dir, { recursive: true, force: true }) } catch {} },
        }
      } catch (e3) {
        try { if (dir) rmSync(dir, { recursive: true, force: true }) } catch {}
        return {
          error: '打不开 cookie 库（浏览器在跑，卷影快照与副本都失败）: ' + (e1.message || e1) + ' / ' + (e2.message || e2) + ' / ' + (e3.message || e3),
          hint: '关掉该浏览器再 scan，或以管理员身份运行 dsh',
        }
      }
    }
  }
}

function rowVersion(encBuf) {
  if (!encBuf || encBuf.length < 3) return 'empty'
  const p = encBuf.subarray(0, 3).toString('latin1')
  if (p === 'v10' || p === 'v11') return 'v10'
  if (p === 'v20') return 'v20'
  return 'raw'
}

/** 只读统计：行数 / 加密版本分布 / 主要域（不解密、不外泄值） */
export function scanProfile(profile) {
  const o = openCookies(profile.dbPath)
  if (o.error) return { browser: profile.browser, profile: profile.profile, dbPath: profile.dbPath, bytes: profile.size, error: o.error, hint: o.hint }
  try {
    const rows = o.db.prepare('SELECT host_key, encrypted_value FROM cookies').all()
    const versions = { v10: 0, v20: 0, raw: 0, empty: 0 }
    const hosts = new Map()
    for (const r of rows) {
      versions[rowVersion(Buffer.from(r.encrypted_value || []))]++
      hosts.set(r.host_key, (hosts.get(r.host_key) || 0) + 1)
    }
    return {
      browser: profile.browser, profile: profile.profile, dbPath: profile.dbPath,
      bytes: profile.size, rows: rows.length, versions, readMode: o.mode,
      topHosts: [...hosts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 10).map(([h, n]) => ({ host: h, n })),
      readable: versions.v10 + versions.raw > 0, blocked: versions.v20,
    }
  } catch (e) {
    return { browser: profile.browser, profile: profile.profile, dbPath: profile.dbPath, bytes: profile.size, error: e.message || String(e) }
  } finally { try { o.cleanup() } catch {} }
}

async function dpapiKeyFromLocalState(localStatePath, dpapi) {
  const ls = JSON.parse(readFileSync(localStatePath, 'utf8'))
  const wrapped = ls && ls.os_crypt && ls.os_crypt.encrypted_key
  if (!wrapped) return { error: 'Local State 里没有 os_crypt.encrypted_key' }
  const buf = Buffer.from(wrapped, 'base64')
  const body = buf.subarray(0, 5).toString('latin1') === 'DPAPI' ? buf.subarray(5) : buf
  const plain = await dpapi(body.toString('base64'))
  if (!plain) return { error: 'DPAPI 解不开主密钥（可能不是当前用户创建的 profile）' }
  const key = Buffer.from(plain, 'base64')
  if (key.length !== 32) return { error: '主密钥长度异常: ' + key.length }
  return { key }
}

function aesGcmDecryptRaw(key, buf) {
  const iv = buf.subarray(3, 15)
  const tag = buf.subarray(buf.length - 16)
  const ct = buf.subarray(15, buf.length - 16)
  const d = createDecipheriv('aes-256-gcm', key, iv)
  d.setAuthTag(tag)
  return Buffer.concat([d.update(ct), d.final()])
}

/**
 * Chrome 130+ 的 cookie 域绑定：明文 = SHA256(host_key)[32] || 真值。
 * 不解这层就会出现"解密成功但值是乱码"（实测 73 条全中）。
 */
function stripHostDigest(plain, hostKey) {
  try {
    const digest = createHash('sha256').update(String(hostKey)).digest()
    if (plain.length >= 32 && plain.subarray(0, 32).equals(digest)) return plain.subarray(32)
  } catch {}
  return plain
}

function expiresToUnixSec(usText, hasExpires) {
  if (!hasExpires) return null
  try {
    const us = BigInt(usText || '0')
    if (us <= 0n) return null
    const sec = us / 1000000n - EPOCH_DELTA_SEC
    if (sec <= 0n) return null
    return Number(sec)
  } catch { return null }
}

const SAMESITE = { 0: 'None', 1: 'Lax', 2: 'Strict' }

/**
 * 只读解密一个 profile 的 cookie。
 * dpapi(b64) => b64|null  由调用方注入（helper 的 crypto_dpapi 命令）。
 */
export async function readCookies(profile, { dpapi, hostFilter = null, limit = 0 } = {}) {
  if (typeof dpapi !== 'function') return { ok: false, error: '缺少 dpapi 注入' }
  let k
  try { k = await dpapiKeyFromLocalState(profile.localStatePath, dpapi) } catch (e) { return { ok: false, error: 'Local State 读取失败: ' + (e.message || e) } }
  if (k.error) return { ok: false, error: k.error }

  const o = openCookies(profile.dbPath)
  if (o.error) return { ok: false, error: o.error }
  try {
    const rows = o.db.prepare(`SELECT host_key, name, value, encrypted_value, path,
      CAST(expires_utc AS TEXT) AS exp, is_secure, is_httponly, samesite, has_expires
      FROM cookies`).all()
    const cookies = []
    const stats = { total: rows.length, decrypted: 0, blockedV20: 0, failed: 0, plain: 0, filtered: 0, expired: 0 }
    const nowSec = Math.floor(Date.now() / 1000)
    for (const r of rows) {
      if (hostFilter && !String(r.host_key).includes(hostFilter)) { stats.filtered++; continue }
      const enc = Buffer.from(r.encrypted_value || [])
      const ver = rowVersion(enc)
      let val = null
      if (ver === 'v20') { stats.blockedV20++; continue }
      if (r.value) { val = String(r.value); stats.plain++ }
      else if (ver === 'v10') {
        try {
          const plain = stripHostDigest(aesGcmDecryptRaw(k.key, enc), r.host_key)
          val = plain.toString('utf8')
          stats.decrypted++
        } catch { stats.failed++; continue }
      } else if (ver === 'raw') {
        let p = null
        try { p = await dpapi(enc.toString('base64')) } catch (e) {
          // 吞掉就会被误读成"坏数据"；helper 掉线要能在诊断里看出来
          stats.failed++
          stats.lastDpapiError = String((e && e.message) || e)
          continue
        }
        if (!p) { stats.failed++; continue }
        val = stripHostDigest(Buffer.from(p, 'base64'), r.host_key).toString('utf8')
        stats.decrypted++
      } else { stats.failed++; continue }

      const expires = expiresToUnixSec(r.exp, Number(r.has_expires) === 1)
      // 过期 cookie 不注入：同域同名会把隔离 profile 里还有效的会话 cookie 覆盖掉
      if (expires && expires < nowSec) { stats.expired++; continue }
      const cookie = {
        name: String(r.name), value: val, domain: String(r.host_key), path: String(r.path || '/'),
        secure: Number(r.is_secure) === 1, httpOnly: Number(r.is_httponly) === 1,
        sameSite: SAMESITE[Number(r.samesite)] || undefined,
      }
      if (expires) cookie.expires = expires
      cookies.push(cookie)
      if (limit && cookies.length >= limit) break
    }
    return { ok: true, cookies, stats, keyLen: k.key.length }
  } catch (e) {
    return { ok: false, error: '读表失败: ' + (e.message || e) }
  } finally { try { o.cleanup() } catch {} }
}

/** 转成 CDP Storage.setCookies 需要的形状（去掉 undefined 字段，URL 由域推导） */
export function toCdpCookies(cookies) {
  return cookies.map((c) => {
    const o = { name: c.name, value: c.value, domain: c.domain, path: c.path || '/', secure: !!c.secure, httpOnly: !!c.httpOnly }
    if (c.expires) o.expires = c.expires
    if (c.sameSite) o.sameSite = c.sameSite
    return o
  })
}
