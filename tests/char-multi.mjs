// tests/char-multi.mjs — 多角色注册与匹配回归测试（方向1）
// 运行: node tests/char-multi.mjs
// 依赖: 已构建 helper（helper/bin/Release/net9.0-windows10.0.19041.0/SaHelper.exe）+ out/sa-view 下固定截图
// 行为: 确保 3 个角色已注册（缺失则按固定截图/区域补注册）→ 自匹配/跨截图匹配/detect(model=m) →
//       rename/delete（只用临时 ASCII 名，不碰 DeepSeek娘）→ 清理临时角色 → 汇总 PASS/FAIL。
// 退出码: 0 = 全部通过, 1 = 有失败项。
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const EXE = path.join(ROOT, 'helper', 'bin', 'Release', 'net9.0-windows10.0.19041.0', 'SaHelper.exe')
const VIEW = path.join(ROOT, 'out', 'sa-view')
const img = f => path.join(VIEW, f)

// 注册定义：区域取"头部+上半身"的方形（ClipScene.Embed 会对方形中心裁剪，长条框会裁掉脸）
const ROLES = [
  { name: '白毛终端少女', file: 'task-1787947113534-45.jpg', x: 260, y: 72, w: 160, h: 160 },   // 900x506 动漫角色（终端窗口内）
]

const BASELINE = 'DeepSeek娘'                       // 既有注册（已合并 3 份参考），测试不得改动
const MAID_FAMILY = new Set([BASELINE])
const TEMP_SRC = '__char_test_tmp_src'
const TEMP_DST = '__char_test_tmp_dst'
const MIN_SELF = 0.99     // 同截图同区域自匹配≈1.0（实测 1.0）
const MIN_CROSS = 0.80    // 跨截图同角色（实测 0.85-0.87）
const MIN_DETECT = 0.80   // detect(model=m) 的 identityScore（实测 0.87-0.92）

// ---------------- helper client ----------------
function start() {
  const child = spawn(EXE, [], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
  let acc = Buffer.alloc(0)
  let id = 1
  const pend = new Map()
  let exited = null
  child.stdout.on('data', d => {
    acc = Buffer.concat([acc, d])
    let i
    while ((i = acc.indexOf(10)) >= 0) {          // 按换行切 Buffer，避免中文跨包截断
      const line = acc.subarray(0, i).toString('utf8').trim()
      acc = acc.subarray(i + 1)
      if (!line) continue
      let m
      try { m = JSON.parse(line) } catch { continue }
      if (m.event) continue                         // 事件行无 id
      const q = pend.get(m.id)
      if (q) { pend.delete(m.id); q.resolve(m) }
    }
  })
  child.stderr.on('data', () => {})
  child.on('exit', () => {
    exited = true
    for (const [, q] of pend) q.reject(new Error('helper 进程已退出'))
    pend.clear()
  })
  function call(cmd, args = {}, timeoutMs = 60000) {
    if (exited) return Promise.reject(new Error('helper 进程已退出'))
    const n = id++
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pend.delete(n); reject(new Error(`${cmd} 超时 ${timeoutMs}ms`)) }, timeoutMs)
      pend.set(n, {
        resolve: m => { clearTimeout(timer); resolve(m) },
        reject: e => { clearTimeout(timer); reject(e) },
      })
      child.stdin.write(JSON.stringify({ id: n, cmd, ...args }) + '\n')
    })
  }
  function close() {
    try { child.stdin.write(JSON.stringify({ id: 0, cmd: 'shutdown' }) + '\n') } catch {}
    return new Promise(r => {
      const t = setTimeout(() => { try { child.kill() } catch {} ; r() }, 2000)
      child.once('exit', () => { clearTimeout(t); r() })
    })
  }
  return { call, close }
}

// ---------------- 断言汇总 ----------------
const results = []
function expect(label, ok, detail = '') {
  results.push({ label, ok: !!ok, detail })
  console.log(`${ok ? 'PASS' : 'FAIL'} | ${label}${detail ? ' | ' + detail : ''}`)
}
const topOf = resp => (resp && resp.data && resp.data.matches && resp.data.matches[0]) || {}
const namesOf = resp => ((resp.data && resp.data.characters) || []).map(c => c.name)

async function main() {
  const { call, close } = start()
  try {
    // 0) 基线：char_list 至少含 DeepSeek娘
    let r = await call('char_list', {}, 20000)
    expect('char_list 返回 ok', r && r.ok === true && r.data && Array.isArray(r.data.characters))
    const names0 = namesOf(r)
    console.log('  现有注册:', JSON.stringify(names0))
    expect(`char_list 含基线「${BASELINE}」`, names0.includes(BASELINE))

    // 1) 确保 3 个角色已注册（幂等：缺失才补）
    for (const role of ROLES) {
      if (names0.includes(role.name)) { console.log(`SKIP | 已注册 ${role.name}（${role.file}）`); continue }
      const reg = await call('char_register', { name: role.name, path: img(role.file), x: role.x, y: role.y, w: role.w, h: role.h }, 120000)
      const d = (reg && reg.data) || {}
      expect(`补注册 ${role.name}`, reg.ok === true && d.ok === true,
        `rect=${JSON.stringify(d.rect || null)} total=${d.total}`)
    }
    r = await call('char_list', {}, 20000)
    const names = namesOf(r)
    for (const role of ROLES) expect(`char_list 含「${role.name}」`, names.includes(role.name))
    expect('char_list 角色数 >= 2', Number((r.data || {}).count) >= 2, `count=${(r.data || {}).count}`)

    // 2) 自匹配：同截图同区域 → 名称必须是自己，相似度≈1.0
    const selfScores = {}
    for (const role of ROLES) {
      const m = await call('char_match', { path: img(role.file), x: role.x, y: role.y, w: role.w, h: role.h }, 60000)
      const top = topOf(m)
      selfScores[role.name] = top.score
      expect(`自匹配 ${role.name} 名称`, top.name === role.name, `top1=${top.name} score=${top.score}`)
      expect(`自匹配 ${role.name} 相似度 >= ${MIN_SELF}`, Number(top.score) >= MIN_SELF, `score=${top.score}`)
    }

    // 3) 跨截图同角色：未参与注册的 DSH 截图 → top-1 落在女仆族，分数 >= 0.80
    const crossCases = [
      { label: 'DSH 全屏截图 glance-...-3', file: 'glance-1792733333431-3.jpg', x: 555, y: 575, w: 250, h: 250 },
      { label: 'DSH 窗口截图 task-...-1', file: 'task-1788460279425-1.jpg', x: 284, y: 367, w: 193, h: 193 },
    ]
    for (const c of crossCases) {
      const m = await call('char_match', { path: img(c.file), x: c.x, y: c.y, w: c.w, h: c.h }, 60000)
      const top = topOf(m)
      expect(`跨截图 ${c.label} top-1 属女仆族`, MAID_FAMILY.has(top.name), `top1=${top.name} score=${top.score}`)
      expect(`跨截图 ${c.label} 相似度 >= ${MIN_CROSS}`, Number(top.score) >= MIN_CROSS, `score=${top.score}`)
    }

    // 4) detect(model=m)：人物框自动附 identity，取 identityScore 最高者作 top-1
    const dm = await call('detect', { path: img('mid.jpg'), model: 'm' }, 120000)
    const dmd = (dm && dm.data) || {}
    const persons = (dmd.items || []).filter(o => o.label === 'person')
    expect('detect(m) mid.jpg 检出人物', persons.length >= 1, `persons=${persons.length} backend=${dmd.backend}`)
    const best = persons.filter(p => p.identity).sort((a, b) => b.identityScore - a.identityScore)[0]
    expect('detect(m) mid.jpg 人物 top-1 身份属女仆族', !!best && MAID_FAMILY.has(best.identity), best ? `top1=${best.identity} score=${best.identityScore}` : '无 identity')
    expect(`detect(m) mid.jpg 身份相似度 >= ${MIN_DETECT}`, !!best && Number(best.identityScore) >= MIN_DETECT, best ? `score=${best.identityScore}` : '')

    const dw = await call('detect', { path: img('task-1787947113534-45.jpg'), model: 'm' }, 120000)
    const dwd = (dw && dw.data) || {}
    const wpersons = (dwd.items || []).filter(o => o.label === 'person')
    const wbest = wpersons.filter(p => p.identity).sort((a, b) => b.identityScore - a.identityScore)[0]
    expect('detect(m) 动漫截图人物 top-1 = 白毛终端少女', !!wbest && wbest.identity === '白毛终端少女', wbest ? `top1=${wbest.identity} score=${wbest.identityScore}` : '无 identity')

    // 5) rename：临时角色（唯一嵌入，避免与既有角色并列 1.0 打平）
    await call('char_delete', { name: TEMP_SRC }, 20000)
    await call('char_delete', { name: TEMP_DST }, 20000)
    const tmpp = { file: 'task-1787946257617-6.jpg', x: 140, y: 200, w: 160, h: 160 }
    let reg = await call('char_register', { name: TEMP_SRC, path: img(tmpp.file), x: tmpp.x, y: tmpp.y, w: tmpp.w, h: tmpp.h }, 120000)
    expect('临时角色注册', reg.ok === true && reg.data.ok === true)
    const rn = await call('char_rename', { from: TEMP_SRC, to: TEMP_DST }, 20000)
    expect('char_rename 临时角色 ok', rn.ok === true && rn.data.ok === true)
    const afterRn = await call('char_list', {}, 20000)
    const rnNames = namesOf(afterRn)
    expect('rename 后含新名', rnNames.includes(TEMP_DST))
    expect('rename 后无旧名', !rnNames.includes(TEMP_SRC))
    const tm = await call('char_match', { path: img(tmpp.file), x: tmpp.x, y: tmpp.y, w: tmpp.w, h: tmpp.h }, 60000)
    const ttop = topOf(tm)
    expect('rename 后匹配返回新名', ttop.name === TEMP_DST, `top1=${ttop.name} score=${ttop.score}`)
    expect('rename 后匹配相似度 >= ' + MIN_SELF, Number(ttop.score) >= MIN_SELF, `score=${ttop.score}`)
    const rnMiss = await call('char_rename', { from: '__char_test_no_such', to: '__char_test_x' }, 20000)
    expect('rename 不存在的角色返回 false', rnMiss.ok === true && rnMiss.data.ok === false)

    // 6) delete：删临时角色，二次删除返回 false
    const del = await call('char_delete', { name: TEMP_DST }, 20000)
    expect('char_delete 临时角色 ok', del.ok === true && del.data.ok === true)
    const afterDel = await call('char_list', {}, 20000)
    expect('delete 后列表无临时角色', !namesOf(afterDel).includes(TEMP_DST))
    const del2 = await call('char_delete', { name: TEMP_DST }, 20000)
    expect('重复 delete 返回 false', del2.ok === true && del2.data.ok === false)

    // 7) 收尾：基线仍在，临时角色清干净
    const final = await call('char_list', {}, 20000)
    const finalNames = namesOf(final)
    expect(`基线「${BASELINE}」仍在`, finalNames.includes(BASELINE))
    expect('临时角色已清理', !finalNames.includes(TEMP_SRC) && !finalNames.includes(TEMP_DST))
    console.log('\n最终注册表:', JSON.stringify(finalNames))
    console.log('自匹配相似度:', JSON.stringify(selfScores))
  } finally {
    // 失败路径也清理临时角色，绝不碰 DeepSeek娘
    for (const n of [TEMP_SRC, TEMP_DST]) {
      try { await call('char_delete', { name: n }, 10000) } catch {}
    }
    await close()
  }

  const passed = results.filter(x => x.ok).length
  const failed = results.length - passed
  console.log(`\n==== 多角色回归: ${passed}/${results.length} 通过${failed ? `, ${failed} 失败` : ''} ====`)
  process.exitCode = failed ? 1 : 0
}

main().catch(e => { console.error('FATAL:', e && e.stack || e); process.exitCode = 1 })
