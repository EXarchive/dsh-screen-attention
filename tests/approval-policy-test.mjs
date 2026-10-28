// 验证：全局审批策略为 never 时插件不得 ask（否则鼠标注入永久被拒）
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')

let policy = 'ask'
const approvalMock = {
  config: { policy: 'ask' },
  effectivePolicy: () => policy,
}
const tools = new Map()
const hooks = {}
const ctx = {
  tools: { register(d) { tools.set(d.name, d); return d } },
  on(ev, fn) { hooks[ev] = fn },
  effect(fn) { fn() },
  get(name) { return name === 'approval' ? approvalMock : undefined },
  agents: { currentInitiator: () => ({ id: 'a1', session: {} }), get: () => ({ id: 'a1', session: {} }) },
  log() {}, warn() {},
}
mod.apply(ctx)
await new Promise(r => setTimeout(r, 800))

const pre = hooks['tools/pre-execute']
const next = () => ({ kind: 'next' })
const agent = { id: 'a1', session: {} }

function gate(toolName) {
  return pre({ name: toolName, agent }, next)
}

let pass = 0
function ok(name, cond, detail) {
  if (!cond) { console.log('FAIL ' + name + ' -> ' + JSON.stringify(detail)); process.exitCode = 1; return }
  pass++
  console.log('PASS ' + name + (detail !== undefined ? ' -> ' + JSON.stringify(detail) : ''))
}

// --- 场景 A：全局 never（当前故障态）---
policy = 'never'
ok('never: saperm_click 直接放行(不再 ask)', gate('saperm_click').kind === 'next', gate('saperm_click'))
ok('never: saperm_scroll 直接放行', gate('saperm_scroll').kind === 'next', gate('saperm_scroll'))
ok('never: saperm_doubleclick(L3) 直接放行', gate('saperm_doubleclick').kind === 'next', gate('saperm_doubleclick'))
ok('never: saperm_drag(L3) 直接放行', gate('saperm_drag').kind === 'next', gate('saperm_drag'))
ok('never: saperm_resume 直接放行(急停可恢复)', gate('saperm_resume').kind === 'next', gate('saperm_resume'))
ok('never: 观察类不受影响', gate('saperm_health').kind === 'next', gate('saperm_health'))

// --- 场景 B：ask 策略（常规审批态）应恢复 ask ---
policy = 'ask'
// 新会话（新 agent id）验证 ask 路径不受 never 阶段影响
const agent2 = { id: 'a2', session: {} }
const gate2 = (n) => pre({ name: n, agent: agent2 }, next)
const st = await tools.get('saperm_status').execute({})
ok('status 暴露 approvalPolicy(最近一次门控观测值)', ['ask', 'never'].includes(st.approvalPolicy), st.approvalPolicy)
ok('ask: 新会话首次 click 需要审批', gate2('saperm_click').kind === 'ask', gate2('saperm_click'))
ok('ask: resume 仍需确认', gate2('saperm_resume').kind === 'ask', gate2('saperm_resume'))
ok('ask: L3 仍需审批', gate2('saperm_doubleclick').kind === 'ask', gate2('saperm_doubleclick'))

// --- 场景 D：曾被拒绝后不得永久卡死（可重新审批）---
policy = 'ask'
const agent3 = { id: 'a3', session: {} }
const gate3 = (n) => pre({ name: n, agent: agent3 }, next)
// 第一次：发起审批
ok('ask: 首次触发审批并进入待决', gate3('saperm_click').kind === 'ask', gate3('saperm_click'))
// 模拟应答者返回拒绝
hooks['tools/result']({ name: 'saperm_click', agent: agent3 }, { isError: true, error: 'the user rejected tool "saperm_click"' })
// 第二次：必须仍然可以重新发起审批，而不是永久 deny
const retry = gate3('saperm_click')
ok('ask: 被拒后仍可重新发起审批(不永久卡死)', retry.kind === 'ask', retry)

// --- 场景 C：急停优先级最高（never 下也拦截）---
const stopRes = await tools.get('saperm_stop').execute({})
policy = 'never'
const pausedGate = gate('saperm_click')
ok('never + 急停: 注入仍被拦截', pausedGate.kind === 'deny', pausedGate)

// 收尾：解除测试 helper 的注入锁，避免遗留锁定
try { await tools.get('saperm_resume').execute({}) } catch {}
console.log('ALL ' + pass + ' APPROVAL POLICY CHECKS PASSED')
process.exit(process.exitCode || 0)
