// 后台隔离 Edge 登录微软账户：探测真实控件 → 真敲字 → 真实点击 → 检测认证 cookie → 存加密 jar
// 用法: node tests/ms-login.mjs <account> <password>
// 账号密码只从 argv 进内存，不落任何文件；cookie 只落 DPAPI 加密 jar。
import { openTab, readTab, tabType, tabClick, tabFormInfo, listCookies, getCookiesFull, closeBrowser } from '../permission-plugin/lib/browser.mjs'
import { saveJar, jarDir } from '../permission-plugin/lib/cookies.mjs'
import { spawn } from 'node:child_process'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const ACCOUNT = process.argv[2]
const PASSWORD = process.argv[3]
if (!ACCOUNT || !PASSWORD) { console.error('用法: node tests/ms-login.mjs <account> <password>'); process.exit(1) }
const say = (o) => console.log(JSON.stringify({ t: new Date().toISOString().slice(11, 19), ...o }))
const wait = (ms) => new Promise((r) => setTimeout(r, ms))

const EXE = process.env.SA_HELPER_EXE || join(ROOT, 'helper', 'bin', 'Release', 'net9.0-windows10.0.19041.0', 'SaHelper.exe')
const hp = spawn(EXE, [], { stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true })
let hbuf = '', hseq = 0
const hpend = new Map()
hp.stdout.on('data', (d) => {
  hbuf += d.toString('utf8'); let i
  while ((i = hbuf.indexOf('\n')) >= 0) {
    const line = hbuf.slice(0, i).trim(); hbuf = hbuf.slice(i + 1); if (!line) continue
    let j; try { j = JSON.parse(line) } catch { continue }
    if (j.event) continue
    const r = hpend.get(j.id); if (r) { hpend.delete(j.id); r(j) }
  }
})
const hcall = (cmd, params = {}) => new Promise((res) => { const id = ++hseq; hpend.set(id, res); hp.stdin.write(JSON.stringify({ id, cmd, ...params }) + '\n') })
const protect = async (b64) => { const r = await hcall('crypto_protect', { blobs: [b64] }); const f = ((r.data && r.data.results) || [])[0]; return f && f.ok ? f.b64 : null }

// consumers + Office 公共 client + 已注册的 nativeclient 回跳 + prompt=login（强制交互才会落会话 cookie）
const LOGIN = process.env.MS_LOGIN_URL ||
  'https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=d3590ed6-52b3-4102-aeff-aad2292ab01c&response_type=code&redirect_uri=https%3A%2F%2Flogin.microsoftonline.com%2Fcommon%2Foauth2%2Fnativeclient&scope=openid%20profile%20offline_access&prompt=login&ui_locales=zh-CN'

const SEL = {
  user: 'input[type="email"], input[name="loginfmt"], input[autocomplete="username"], #i0116',
  pass: 'input[type="password"], input[name="passwd"], #i0118',
  next: '#idSIButton9, button[type="submit"], input[type="submit"], button',
}

say({ step: 'open', url: LOGIN.slice(0, 120) + '…' })
const tab = await openTab({ url: LOGIN, w: 1280, h: 900, waitMs: 8000, text: false, screenshot: true })
if (!tab.ok) { say({ step: 'open-failed', error: tab.error }); process.exit(1) }
say({ step: 'opened', tabKey: tab.tabKey, title: tab.title, shot: tab.path })

async function info(label) {
  const f = await tabFormInfo(tab.tabKey)
  say({ step: label, url: String(f.url || '').slice(0, 90), title: f.title, inputs: (f.inputs || []).filter((x) => x.visible).map((x) => `${x.type}:${x.name || x.id || x.placeholder}`), buttons: (f.buttons || []).filter((b) => b.visible && b.text).map((b) => b.text.slice(0, 10)), text: String(f.text || '').slice(0, 140) })
  return f
}

const cookieNames = async (host) => ((await listCookies(host)).cookies || []).map((c) => c.name)
const authedNow = async () => {
  const all = [...(await cookieNames('live.com')), ...(await cookieNames('microsoftonline')), ...(await cookieNames('office.com'))]
  return { authed: all.some((n) => /^(ESTSAUTH|ESTSAUTHPERSISTENT|RPSSecAuth)$/.test(n)), names: all.slice(0, 16) }
}

let f = await info('step1')

// 0) 页面若没表单但有「登录/Sign in」按钮：先点它（让 MS 自己生成带正确参数的登录 URL）
if (!(f.inputs || []).some((x) => x.visible)) {
  const btn = await readTab(tab.tabKey, { screenshot: false, text: false, js: `(() => {
    const cands = [...document.querySelectorAll('a, button')].filter((e) => /^(登录|登入|Sign in|Sign In)$/i.test((e.innerText || '').trim()));
    const el = cands.find((e) => e.offsetWidth || e.offsetHeight) || cands[0];
    if (!el) return 'no-btn';
    el.click();
    return (el.innerText || '').trim();
  })()` })
  say({ step: 'click-signin-entry', result: f2js(btn) })
  await wait(7000)
  f = await info('step1b')
}

function f2js(r) { return r && r.js !== undefined ? r.js : null }

// 第一屏：账号
if ((f.inputs || []).some((x) => x.visible && x.type === 'email') || String(f.text || '').includes('电子邮件')) {
  const t = await tabType(tab.tabKey, SEL.user, ACCOUNT)
  say({ step: 'type-account', ...t })
  const c = await tabClick(tab.tabKey, SEL.next)
  say({ step: 'click-next-account', ...c })
  await wait(6000)
  f = await info('step2')
}

// 第二屏：密码
if ((f.inputs || []).some((x) => x.visible && x.type === 'password')) {
  const t = await tabType(tab.tabKey, SEL.pass, PASSWORD)
  say({ step: 'type-password', ok: t.ok, valueLen: t.valueLen, error: t.error })
  const c = await tabClick(tab.tabKey, SEL.next)
  say({ step: 'click-next-password', ...c })
  await wait(9000)
  f = await info('step3')
}

// 「保持登录?」/ MFA
if (/保持登录|Stay signed in|KMSI/i.test(String(f.text || ''))) {
  const c = await tabClick(tab.tabKey, SEL.next)
  say({ step: 'kmsi', ...c })
  await wait(7000)
  f = await info('step4')
}

const st = await authedNow()
const needMfa = /验证码|Verify|Authenticator|保护你的帐户|Help us protect|输入代码|Enter code/i.test(String(f.text || ''))
const errText = /(密码不正确|错误|不正确|invalid|AADSTS\d+|incorrect)/i.test(String(f.text || ''))
say({ step: 'status', authed: st.authed, needMfa, errText, url: String(f.url || '').slice(0, 100), cookies: st.names })

if (!st.authed) {
  say({ step: 'not-authed', needMfa, errText, hint: needMfa ? '要 MFA/验证码，把收到的码发我' : '看截图与 text：可能密码不对或页面结构又变了' })
  try { hp.kill() } catch {}
  process.exit(2)
}

const full = await getCookiesFull('live.com')
say({ step: 'snapshot', cookies: full.count })
const saved = await saveJar({ dir: jarDir(join(ROOT, 'permission-plugin')), name: 'msaccount', cookies: full.cookies, dpapi: protect, meta: { via: 'password-login', account: ACCOUNT, host: 'live.com' } })
say({ step: 'saved', ok: saved.ok, path: saved.path, bytes: saved.bytes, error: saved.error })
const after = await readTab(tab.tabKey, { screenshot: true, text: true, waitMs: 1200, maxText: 200 })
say({ step: 'verify', url: String(after.url || '').slice(0, 100), title: after.title, shot: after.path })

closeBrowser()
try { hp.kill() } catch {}
process.exit(0)
