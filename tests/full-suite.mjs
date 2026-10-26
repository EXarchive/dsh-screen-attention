// dsh-screen-attention 完整测试套件：一次性覆盖 helper 全部命令。
// 运行：node tests/full-suite.mjs [helperExe]
import { spawn } from 'node:child_process'
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const exe = process.argv[2] || join(here, '..', 'helper', 'bin', 'Release', 'net9.0-windows10.0.19041.0', 'SaHelper.exe')
const outDir = join(here, '..', 'out')
mkdirSync(outDir, { recursive: true })

const child = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
let buf = ''
let nextId = 1
const pending = new Map()
child.stdout.on('data', (d) => {
  buf += d.toString('utf8')
  let i
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim()
    buf = buf.slice(i + 1)
    if (!line) continue
    let m; try { m = JSON.parse(line) } catch { continue }
    if (m.event) continue
    const p = pending.get(m.id)
    if (p) { pending.delete(m.id); p.resolve(m) }
  }
})
child.stderr.on('data', (d) => process.stderr.write('[helper stderr] ' + d))
child.on('exit', (c) => console.log('[helper exited]', c))

function call(cmd, args = {}, timeoutMs = 60000) {
  const id = nextId++
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => { pending.delete(id); reject(new Error(cmd + ' timeout')) }, timeoutMs)
    pending.set(id, { resolve: (m) => { clearTimeout(t); resolve(m) } })
    child.stdin.write(JSON.stringify({ id, cmd, ...args }) + '\n')
  })
}

const results = []
const T = (name, ok, detail) => results.push({ name, ok, detail })

try {
  // [1] 基础
  const h = await call('handshake'); T('handshake', h.ok && h.data?.version === 1, h.ok ? 'ver=' + h.data.version + ' cores=' + h.data.cores + ' ' + h.data.screen.w + 'x' + h.data.screen.h : h.error)
  const b = await call('screen_bounds'); T('screen_bounds', b.ok && b.data.w > 0, b.ok ? b.data.w + 'x' + b.data.h : b.error)
  const p = await call('ping'); T('ping', p.ok && p.data?.pong === true, p.ok ? 'pong' : p.error)
  const idl = await call('idle'); T('idle', idl.ok, idl.ok ? idl.data.idleMs + 'ms' : idl.error)
  const w = await call('windows'); T('windows', w.ok && w.data?.windows?.length > 0, w.ok ? w.data.windows.length + ' 窗口' : w.error)

  // [2] 采集
  const sh = await call('screenshot', { maxW: 1280 }); T('screenshot', sh.ok && sh.data?.jpegB64, sh.ok ? sh.data.w + 'x' + sh.data.h + ' ' + sh.data.bytes + 'B' : sh.error)
  if (sh.ok) { const f = join(outDir, 'full-suite-screenshot.jpg'); writeFileSync(f, Buffer.from(sh.data.jpegB64, 'base64')); }
  const ocr = await call('ocr', {}); T('ocr fullscreen', ocr.ok && ocr.data?.items?.length > 0, ocr.ok ? ocr.data.chars + '字符/' + ocr.data.lines + '行/' + ocr.data.items.length + '条 ' + ocr.data.ms + 'ms ' + ocr.data.stripes + '条带' : ocr.error)
  const uia = await call('uia', {}); T('uia', uia.ok && uia.data?.items?.length > 0, uia.ok ? uia.data.items.length + ' 节点' : (uia.error || 'uia'))

  // [3] 观察引擎
  const ws = await call('watch_start', { intervalMs: 1000 }); T('watch_start', ws.ok, ws.ok ? JSON.stringify(ws.data) : ws.error)
  await new Promise(r => setTimeout(r, 1200))
  const wst = await call('watch_status', {}); T('watch_status', wst.ok, wst.ok ? 'watching=' + wst.data.watching + ' interval=' + wst.data.intervalMs + ' motion=' + wst.data.motion : wst.error)
  const snap = await call('watch_snapshot', {}); T('watch_snapshot', snap.ok, snap.ok ? 'motion=' + snap.data.motion + ' ratio=' + snap.data.ratio + ' text=' + (snap.data.text || '').slice(0, 40) : snap.error)
  const wf = await call('watch_focus', { x: 300, y: 200, w: 400, h: 300 }); T('watch_focus set', wf.ok, wf.ok ? 'rect=' + JSON.stringify(wf.data) : wf.error)
  const wf2 = await call('watch_focus', { clear: true }); T('watch_focus clear', wf2.ok, wf2.ok ? JSON.stringify(wf2.data) : wf2.error)
  const wstop = await call('watch_stop', {}); T('watch_stop', wstop.ok, wstop.ok ? 'watching=' + wstop.data.watching : wstop.error)

  // [4] 注意力小窗
  const oo = await call('overlay_open', { x: 900, y: 500, w: 480, h: 270 }); T('overlay_open', oo.ok, oo.ok ? JSON.stringify(oo.data) : oo.error)
  await new Promise(r => setTimeout(r, 500))
  const os = await call('overlay_set', { region: { x: 640, y: 360, w: 200, h: 160 }, gaze: { x: 740, y: 440 }, note: 'full-suite 测试' }); T('overlay_set', os.ok, os.ok ? JSON.stringify(os.data) : os.error)
  await new Promise(r => setTimeout(r, 300))
  const oc = await call('overlay_close', {}); T('overlay_close', oc.ok, oc.ok ? 'open=' + oc.data.open : oc.error)

  // [5] 急停 = 注入保护
  const st = await call('stop', {}); T('stop(应急锁)', st.ok && st.data?.locked === true, st.ok ? 'locked=' + st.data.locked : st.error)
  const locked = []
  for (const [cmd, args] of [['click', { x: 5, y: 5 }], ['doubleclick', { x: 5, y: 5 }], ['move', { x: 100, y: 100 }], ['drag', { x1: 10, y1: 10, x2: 20, y2: 20 }], ['scroll', { amount: 0 }], ['type', { text: '' }]]) {
    const r = await call(cmd, args)
    locked.push({ cmd, ok: r.ok, err: r.ok ? 'ALLOWED(异常)' : (r.error || '').slice(0, 30), blocked: !r.ok && /LOCKED/.test(r.error || '') })
    T('锁定下 ' + cmd + ' 被拒', !r.ok && /LOCKED/.test(r.error || ''), r.ok ? '未锁定!? ' + JSON.stringify(r.data) : (r.error || '').slice(0, 50))
  }

  // [6] 解锁后注入放行（无毒参数）
  const rs = await call('resume', {}); T('resume(解锁)', rs.ok && rs.data?.locked === false, rs.ok ? 'locked=' + rs.data.locked : rs.error)
  const sc = await call('scroll', { amount: 0 }); T('解锁后 scroll 放行', sc.ok, sc.ok ? 'ok' : sc.error)
  const mv = await call('move', { x: 500, y: 500 }); T('解锁后 move 放行', mv.ok, mv.ok ? 'ok' : mv.error)

  const sd = await call('shutdown', {}); T('shutdown', sd.ok && sd.data?.bye === true, sd.ok ? 'bye' : sd.error)
} catch (e) {
  T('FATAL', false, e.message)
}

const pass = results.filter(r => r.ok).length
console.log('\n===== 完整测试套件报告 =====')
for (const r of results) console.log((r.ok ? '✅' : '❌') + ' ' + r.name + (r.detail ? '  => ' + r.detail : ''))
console.log('\n通过 ' + pass + '/' + results.length)
process.exit(pass === results.length ? 0 : 1)
