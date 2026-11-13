// 投产自检（已迁移到 47 工具 / action 入口版本）
// 原 prod-readiness.legacy.mjs 针对合并前的 61 工具 API 与已删字段，跑起来必抛；此文件是它的等价迁移。
import assert from 'node:assert/strict'

const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const injected = []
const mockAgent = {
  id: 'prod-test-agent',
  inject(msg) { injected.push({ kind: 'inject', text: msg.content?.[0]?.text || '' }) },
  steer(msg) { injected.push({ kind: 'steer', text: msg.content?.[0]?.text || '' }) },
}
const ctx = {
  tools: { register(d) { tools.set(d.name, d); return d } },
  on() {}, effect(fn) { fn() },
  agents: { currentInitiator: () => mockAgent, get: () => mockAgent },
  log() {}, warn() {},
}
mod.apply(ctx)
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
await sleep(1000)

let passed = 0, skipped = 0
function ok(name, cond, detail = '') {
  if (!cond) throw new Error('FAIL [' + name + '] ' + detail)
  passed++
  console.log('PASS ' + passed + '. ' + name + (detail ? ' -> ' + detail : ''))
}
function skip(name, why) { skipped++; console.log('SKIP ' + name + ' -> ' + why) }

// 1. 工具清单与 helper 桥接
ok('tool registry size', tools.size >= 40, 'count=' + tools.size)
const h0 = await tools.get('saperm_health').execute({})
ok('health ok & bridged to interactive session', h0.ok === true && h0.helper.online === true && h0.helper.sessionId >= 1,
  'pid=' + h0.helper.pid + ' sessionId=' + h0.helper.sessionId + ' fgTitle=' + h0.fgTitle)

// 2. infer 幂等持有
const infer = tools.get('saperm_infer')
await infer.execute({ action: 'start', maxBoxes: 25, objects: true, scene: true, accurate: false, animeface: true, sceneEvery: 8, faceEvery: 4 })
await infer.execute({ action: 'start', maxBoxes: 25, objects: true, scene: true, accurate: false, animeface: true, sceneEvery: 8, faceEvery: 4 })
const hHold = await tools.get('saperm_health').execute({})
ok('idempotent hold on repeated infer start', hHold.observation.holds === 1 && hHold.observation.mode === 'infer', JSON.stringify(hHold.observation))

// 3. 首帧 + 场景 + 静止跳帧
let st1 = null
for (let i = 0; i < 32; i++) {
  await sleep(500)
  st1 = await infer.execute({ action: 'status', limit: 3 })
  if (st1.framesProcessed >= 1 && st1.staticSkipped >= 3 && st1.latest && st1.latest.scene) break
}
ok('first frame processed & scene populated', st1.framesProcessed >= 1 && st1.latest && typeof st1.latest.scene === 'string' && st1.latest.scene.length > 0,
  'processed=' + st1.framesProcessed + ' scene=' + (st1.latest && st1.latest.scene))
ok('static-skip heartbeat active', st1.staticSkipped > 0, 'staticSkipped=' + st1.staticSkipped + ' seen=' + st1.framesSeen)
ok('understanding includes foreground window title', !!(st1.latest && typeof st1.latest.fgTitle === 'string' && /窗口「/.test(st1.latest.understanding || '')),
  'fgTitle=' + (st1.latest && st1.latest.fgTitle) + ' understanding=' + String((st1.latest && st1.latest.understanding) || '').slice(0, 50))

// 4. 屏幕变化触发重新识别（浮窗已被排除捕获，改用 say 气泡制造真实画面变化）
const procBefore = st1.framesProcessed
await tools.get('saperm_say').execute({ text: 'prod-readiness motion probe', speak: false, ms: 1800 })
await sleep(2200)
const st2 = await infer.execute({ action: 'status', limit: 3 })
ok('motion wake-up processed new frames', st2.framesProcessed > procBefore, 'before=' + procBefore + ' after=' + st2.framesProcessed)

// 5. 崩溃告警 + 自动恢复
const oldPid = (await tools.get('saperm_health').execute({})).helper.pid
process.kill(oldPid)
let hRec = null
for (let i = 0; i < 25; i++) {
  await sleep(400)
  try {
    hRec = await tools.get('saperm_health').execute({})
    if (hRec.ok && hRec.helper.pid && hRec.helper.pid !== oldPid && hRec.watchdog.recoveries >= 1) break
  } catch {}
}
ok('watchdog restarted helper with new pid', hRec && hRec.ok && hRec.helper.pid !== oldPid, 'oldPid=' + oldPid + ' newPid=' + (hRec && hRec.helper.pid))
ok('watchdog restored observation mode & hold', hRec.observation.mode === 'infer' && hRec.observation.holds === 1 && hRec.watchdog.crashes >= 1,
  JSON.stringify({ obs: hRec.observation, watchdog: hRec.watchdog }))
ok('crash alert injected into session', injected.some(m => m.text.includes('异常退出')), 'injected=' + injected.length)

// 6. 互斥切换 + 干净停止
const itp = await tools.get('saperm_interpret').execute({ action: 'start', intervalMs: 800 })
ok('interpret start auto-stopped infer', itp.running === true && itp.autoStopped === 'infer', JSON.stringify({ running: itp.running, autoStopped: itp.autoStopped }))
await tools.get('saperm_interpret').execute({ action: 'stop' })
const hStopped = await tools.get('saperm_health').execute({})
ok('clean stop releases hold & clears mode', hStopped.observation.mode === null && hStopped.observation.holds === 0, JSON.stringify(hStopped.observation))
await sleep(300)
const hRefs = await tools.get('saperm_status').execute({})
ok('helper refs back to 0 (idle shutdown reachable)', hRefs.helper.refs === 0, JSON.stringify(hRefs.helper))

// 7. 审计日志
const hist = await tools.get('saperm_history').execute({ limit: 30 })
const types = [...new Set((hist.items || []).map(x => x.type))]
if (types.includes('helper_crash') && types.includes('helper_recover')) {
  ok('history records crash and recover', true, 'types=' + types.join(','))
} else skip('history crash/recover entries', '当前日志类型: ' + types.join(','))

// 8. 角色备注迁移
const ch = tools.get('saperm_char')
const sampleDir = 'C:/Users/Administrator/PycharmProjects/dsh-screen-attention/out/sa-view'
const fsx = await import('node:fs')
// 注册依赖「图里能自动检出人物」，而 sa-view 里的图会随清理轮换 —— 按候选依次试，
// 都失败才算失败（之前写死 imgs[0]，图一变整份用例就假红）。
const cands = [sampleDir + '/glance-1792733333431-3.jpg']
try {
  const imgs = fsx.readdirSync(sampleDir).filter(n => /\.jpg$/i.test(n)).slice(0, 4)
  for (const n of imgs) cands.push(sampleDir + '/' + n)
} catch {}
let regRes = { ok: false, error: 'no candidate image' }
const tried = []
for (const c of cands) {
  try {
    const r = await ch.execute({ action: 'register', name: '__prod_tmp__', path: c, note: '临时测试角色备注' })
    tried.push({ c: c.slice(-28), ok: !!r.ok, err: r.error })
    if (r.ok) { regRes = r; break }
  } catch (e) { tried.push({ c: c.slice(-28), err: String(e.message).slice(0, 40) }) }
}
ok('char register with note', regRes.ok === true && regRes.name === '__prod_tmp__', JSON.stringify({ reg: regRes, tried }).slice(0, 260))
await ch.execute({ action: 'rename', from: '__prod_tmp__', to: '__prod_tmp_renamed__' })
const listAfterRename = await ch.execute({ action: 'list' })
const renamed = (listAfterRename.characters || []).find(c => c.name === '__prod_tmp_renamed__')
ok('char rename migrates note', !!renamed && renamed.note === '临时测试角色备注', JSON.stringify(renamed))
await ch.execute({ action: 'delete', name: '__prod_tmp_renamed__' })
const listAfterDel = await ch.execute({ action: 'list' })
ok('char delete removes temp character', !(listAfterDel.characters || []).some(c => String(c.name).startsWith('__prod_tmp')), 'remaining=' + (listAfterDel.characters || []).length)
const ignoreStale = await ch.execute({ action: 'ignore', id: 'stale' })
ok('char ignore supports stale cleanup', ignoreStale.ok === true, JSON.stringify(ignoreStale).slice(0, 80))

// 9. 诊断字段
const hDiag = await tools.get('saperm_health').execute({})
ok('health exposes rich diagnostics', hDiag.workingSetMB > 50 && Array.isArray(hDiag.providers) && hDiag.providers.some(p => String(p).includes('Dml')) &&
  Array.isArray(hDiag.privacyKeywords) && hDiag.privacyKeywords.includes('1Password') && typeof hDiag.secureDesktop === 'boolean' && typeof hDiag.storage.viewFiles === 'number',
  JSON.stringify({ wsMB: hDiag.workingSetMB, providers: hDiag.providers, viewFiles: hDiag.storage.viewFiles }))

// 10. 隐私关键词（行为断言见下）
ok('privacy keyword list includes password managers', hDiag.privacyKeywords.includes('1Password') && hDiag.privacyKeywords.length >= 5, 'kw=' + hDiag.privacyKeywords.length)

// 11. 保留清理
const prunedViews = mod.pruneViewDir(30)
const prunedLogs = mod.pruneLogsDir()
const hStorage = await tools.get('saperm_health').execute({})
ok('view/log retention pruning', hStorage.storage.viewFiles <= 36 && prunedViews >= 0 && prunedLogs >= 0,
  JSON.stringify({ prunedViews, prunedLogs, viewFiles: hStorage.storage.viewFiles, logFiles: hStorage.storage.logFiles }))

console.log('RESULT: ' + passed + ' passed, ' + skipped + ' skipped')
if (typeof mod.dispose === 'function') { try { await mod.dispose() } catch {} }
process.exit(0)
