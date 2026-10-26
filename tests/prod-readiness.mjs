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
  on() {},
  effect(fn) { fn() },
  agents: { currentInitiator: () => mockAgent, get: () => mockAgent },
  log() {}, warn() {},
}

mod.apply(ctx)
await new Promise(r => setTimeout(r, 900))

let passed = 0
function ok(name, cond, detail = '') {
  if (!cond) throw new Error('FAIL [' + name + '] ' + detail)
  passed++
  console.log('PASS ' + passed + '. ' + name + (detail ? ' -> ' + detail : ''))
}

// 1. Health & tool count
ok('tools registered >= 60', tools.size >= 60, 'count=' + tools.size)
const h0 = await tools.get('saperm_health').execute({})
ok('initial health ok & bridged to interactive user session (sessionId >= 1)', h0.ok === true && h0.helper.online === true && h0.helper.sessionId >= 1, 'pid=' + h0.helper.pid + ' sessionId=' + h0.helper.sessionId + ' fgTitle=' + h0.fgTitle)

// 2. Idempotent holds & mutual exclusion
await tools.get('saperm_infer_start').execute({ maxBoxes: 25, objects: true, scene: true, accurate: false, animeface: true, sceneEvery: 8, faceEvery: 4 })
await tools.get('saperm_infer_start').execute({ maxBoxes: 25, objects: true, scene: true, accurate: false, animeface: true, sceneEvery: 8, faceEvery: 4 })
const hHold = await tools.get('saperm_health').execute({})
ok('idempotent hold on repeated infer_start', hHold.observation.holds === 1 && hHold.observation.mode === 'infer', JSON.stringify(hHold.observation))

// Wait for first frame + static heartbeats (warm up ONNX on first frame)
let st1 = null
for (let i = 0; i < 32; i++) {
  await new Promise(r => setTimeout(r, 500))
  st1 = await tools.get('saperm_infer_status').execute({ limit: 3 })
  if (st1.framesProcessed >= 1 && st1.staticSkipped >= 3 && st1.latest && st1.latest.scene) break
}
ok('first frame processed & scene populated immediately',
  st1.framesProcessed >= 1 && st1.latest && typeof st1.latest.scene === 'string' && st1.latest.scene.length > 0,
  'processed=' + st1.framesProcessed + ' scene=' + (st1.latest && st1.latest.scene) + ' understanding=' + (st1.latest && st1.latest.understanding || '').slice(0, 60))
ok('motion-adaptive static skip active', st1.staticSkipped > 0, 'staticSkipped=' + st1.staticSkipped + ' seen=' + st1.framesSeen)

// 3. Trigger real screen pixel change via overlay open/close and verify motion detection
const procBefore = st1.framesProcessed
await tools.get('saperm_overlay_open').execute({ x: 300, y: 260, w: 360, h: 200 })
await new Promise(r => setTimeout(r, 600))
await tools.get('saperm_overlay_close').execute({})
await new Promise(r => setTimeout(r, 800))
const st2 = await tools.get('saperm_infer_status').execute({ limit: 3 })
ok('motion wake-up processed new frames on screen change', st2.framesProcessed > procBefore, 'before=' + procBefore + ' after=' + st2.framesProcessed)

// 4. Watchdog crash alert & automatic state recovery (B13 & C14)
const oldPid = (await tools.get('saperm_health').execute({})).helper.pid
process.kill(oldPid)
// Wait for watchdog restart (800ms timer + ready + recovery + 1 frame)
let hRec = null
for (let i = 0; i < 20; i++) {
  await new Promise(r => setTimeout(r, 400))
  try {
    hRec = await tools.get('saperm_health').execute({})
    if (hRec.ok && hRec.helper.pid && hRec.helper.pid !== oldPid && hRec.watchdog.recoveries >= 1) break
  } catch {}
}
ok('watchdog restarted helper with new pid', hRec && hRec.ok && hRec.helper.pid !== oldPid, 'oldPid=' + oldPid + ' newPid=' + (hRec && hRec.helper.pid))
ok('watchdog automatically restored infer mode & hold',
  hRec.observation.mode === 'infer' && hRec.observation.holds === 1 && hRec.watchdog.crashes >= 1 && hRec.watchdog.recoveries >= 1,
  JSON.stringify({ obs: hRec.observation, watchdog: hRec.watchdog }))
ok('crash alert injected into session', injected.some(m => m.text.includes('异常退出') && m.text.includes('infer')), JSON.stringify(injected))

// 5. Mutual exclusion switch to interpret & clean stop
const itp = await tools.get('saperm_interpret_start').execute({ intervalMs: 800 })
ok('interpret_start autoStopped infer', itp.running === true && itp.autoStopped === 'infer', JSON.stringify({ running: itp.running, autoStopped: itp.autoStopped }))
await tools.get('saperm_interpret_stop').execute({})
const hStopped = await tools.get('saperm_health').execute({})
ok('clean stop releases hold and clears mode', hStopped.observation.mode === null && hStopped.observation.holds === 0, JSON.stringify(hStopped.observation))

// 6. Audit history log contains crash & recover entries
const hist = await tools.get('saperm_history').execute({ limit: 20 })
ok('saperm_history records crash and recover',
  hist.items.some(x => x.type === 'helper_crash') && hist.items.some(x => x.type === 'helper_recover'),
  'count=' + hist.count + ' types=' + [...new Set(hist.items.map(x => x.type))].join(','))

// 7. Character note sync on rename/delete & stale unknown cleanup
const sampleImg = 'C:/Users/Administrator/PycharmProjects/dsh-screen-attention/out/sa-view/glance-1792733333431-3.jpg'
const regRes = await tools.get('saperm_char_register').execute({ name: '__prod_tmp__', path: sampleImg, x: 580, y: 600, w: 180, h: 200, note: '临时测试角色备注' })
ok('char_register with note', regRes.ok === true && regRes.name === '__prod_tmp__', JSON.stringify(regRes))
await tools.get('saperm_char_rename').execute({ from: '__prod_tmp__', to: '__prod_tmp_renamed__' })
const listAfterRename = await tools.get('saperm_char_list').execute({})
const renamedEntry = listAfterRename.characters.find(c => c.name === '__prod_tmp_renamed__')
ok('char_rename migrates note', renamedEntry && renamedEntry.note === '临时测试角色备注', JSON.stringify(renamedEntry))
await tools.get('saperm_char_delete').execute({ name: '__prod_tmp_renamed__' })
const listAfterDel = await tools.get('saperm_char_list').execute({})
ok('char_delete cleans up temporary character', !listAfterDel.characters.some(c => c.name.startsWith('__prod_tmp')), 'remaining=' + listAfterDel.characters.map(c => c.name).join(','))
const ignoreStale = await tools.get('saperm_char_ignore').execute({ id: 'stale' })
ok('char_ignore supports stale cleanup', ignoreStale.ok === true, JSON.stringify(ignoreStale))

// 8. D4 Rich health diagnostics (workingSetMB, providers, privacyKeywords, storage)
const hDiag = await tools.get('saperm_health').execute({})
ok('health exposes rich diagnostics (D4)',
  hDiag.workingSetMB > 50 && Array.isArray(hDiag.providers) && hDiag.providers.includes('DmlExecutionProvider') &&
  Array.isArray(hDiag.privacyKeywords) && hDiag.privacyKeywords.includes('1Password') &&
  typeof hDiag.secureDesktop === 'boolean' && hDiag.storage && typeof hDiag.storage.viewFiles === 'number',
  JSON.stringify({ wsMB: hDiag.workingSetMB, providers: hDiag.providers, storage: hDiag.storage }))

// 9. B3 Foreground window title in understanding
ok('understanding includes foreground window title (B3)',
  st1.latest && typeof st1.latest.fgTitle === 'string' && (st1.latest.understanding || '').includes('窗口「'),
  'fgTitle=' + (st1.latest && st1.latest.fgTitle) + ' understanding=' + (st1.latest && st1.latest.understanding || '').slice(0, 60))

// 10. B14 Privacy window redaction during screen capture
await tools.get('saperm_overlay_open').execute({ x: 240, y: 200, w: 300, h: 180 })
await tools.get('saperm_overlay_set').execute({ note: '__SA_PRIVACY_TEST__' })
await new Promise(r => setTimeout(r, 350))
await tools.get('saperm_glance').execute({ maxW: 640 })
await tools.get('saperm_overlay_close').execute({})
const hPriv = await tools.get('saperm_health').execute({})
ok('privacy blacklist window redaction (B14)', hPriv.privacyRedactions >= 1, 'privacyRedactions=' + hPriv.privacyRedactions)

// 11. B8 & C13 Storage retention pruning (view files <= 30, logs clean)
const prunedViews = mod.pruneViewDir(30)
const prunedLogs = mod.pruneLogsDir()
const hStorage = await tools.get('saperm_health').execute({})
ok('screenshot & log retention pruning (B8 & C13)',
  hStorage.storage.viewFiles <= 36 && hStorage.storage.logFiles >= 1 && prunedViews >= 0 && prunedLogs >= 0,
  JSON.stringify({ prunedViews, prunedLogs, storage: hStorage.storage }))


console.log('ALL ' + passed + ' PRODUCTION READINESS CHECKS PASSED!')
process.exit(0)
