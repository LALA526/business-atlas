/* V9.4 focused real-Chrome smoke: reading, change approval, delivery, import and persistence. */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawn } = require('node:child_process');
const { pathToFileURL } = require('node:url');
const root = path.join(__dirname, '..'), out = path.join(root, 'artifacts/v9-4-smoke');
fs.mkdirSync(out, { recursive: true });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const chromePath = process.env.CHROME_PATH || ['C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe', path.join(os.homedir(), 'AppData/Local/Google/Chrome/Application/chrome.exe')].find(file => fs.existsSync(file));
if (!chromePath) throw new Error('Set CHROME_PATH to a local Chrome executable');
const profile = path.join(os.tmpdir(), 'opencode', 'atlas-v9-4-smoke-profile'); fs.rmSync(profile, { recursive: true, force: true }); fs.mkdirSync(profile, { recursive: true });
const url = pathToFileURL(path.join(root, 'dist/business-atlas-v9.4.html')).href, chrome = spawn(chromePath, ['--headless=new', '--remote-debugging-port=9227', `--user-data-dir=${profile}`, '--no-first-run', '--disable-extensions', '--disable-background-networking', '--window-size=1280,800', url], { stdio: 'ignore' });
const checks = [];
function check(name, condition, evidence) { checks.push({ name, passed: !!condition, evidence }); if (!condition) throw new Error(`${name}: ${JSON.stringify(evidence)}`); }
async function main() {
  let pages;
  for (let index = 0; index < 100; index++) { try { pages = await (await fetch('http://127.0.0.1:9227/json/list')).json(); if (pages?.some(item => item.url.includes('business-atlas-v9.4.html'))) break; } catch (_) { /* starting */ } await sleep(100); }
  const page = pages?.find(item => item.type === 'page' && item.url.includes('business-atlas-v9.4.html')); if (!page) throw new Error('Chrome did not start');
  const ws = new WebSocket(page.webSocketDebuggerUrl), pending = new Map(), errors = [], requests = []; let sequence = 0, navigation = 0;
  await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
  ws.addEventListener('message', event => { const message = JSON.parse(event.data); if (message.id && pending.has(message.id)) { const item = pending.get(message.id); pending.delete(message.id); message.error ? item.reject(new Error(JSON.stringify(message.error))) : item.resolve(message.result); } if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails); if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') errors.push(message.params); if (message.method === 'Network.requestWillBeSent') requests.push(message.params.request.url); });
  const send = (method, params = {}) => new Promise((resolve, reject) => { const id = ++sequence; pending.set(id, { resolve, reject }); ws.send(JSON.stringify({ id, method, params })); });
  const evaluate = async expression => { const response = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }); if (response.exceptionDetails) throw new Error(JSON.stringify(response.exceptionDetails)); return response.result.value; };
  const waitFor = async expression => { const start = Date.now(); while (Date.now() - start < 15000) { if (await evaluate(`!!(${expression})`)) return; await sleep(50); } throw new Error(`Timed out: ${expression} :: ${await evaluate("location.hash+' :: '+document.body.innerText.slice(0,700)")}`); };
  const go = async (hash, wait) => { const target = new URL(url); target.searchParams.set('smoke', String(++navigation)); target.hash = hash; await send('Page.navigate', { url: target.href }); await waitFor(wait); };
  const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
  const set = (id, value) => evaluate(`(()=>{const el=document.getElementById(${JSON.stringify(id)});el.value=${JSON.stringify(value)};el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));return el.value})()`);
  const capture = async name => { await evaluate("document.querySelectorAll('.toast').forEach(el=>el.remove())"); const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false, fromSurface: true }); fs.writeFileSync(path.join(out, name), Buffer.from(shot.data, 'base64')); };
  await send('Runtime.enable'); await send('Page.enable'); await send('Network.enable'); await send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false });
  await go('PRJ-00003/studio/FEAT-M02', "window.AtlasV93UI && document.querySelector('.decision-card')");

  const reading = await evaluate("({svg:document.querySelectorAll('svg,canvas').length,tables:document.querySelectorAll('.decision-table').length,purpose:document.querySelector('.reading-summary').innerText})");
  check('文字阅读无绘图且目的清晰', reading.svg === 0 && reading.tables === 1 && reading.purpose.includes('质检员、生产班组长'), reading); await capture('01-text-reading.png');
  await click('[data-v93-case="CASE-M-REG-POS"]'); await waitFor("document.querySelector('.evaluation-result.decided')");
  check('规则正例由真实条件评估', (await evaluate("document.querySelector('.evaluation-result').innerText")).includes('允许登记并隔离'), await evaluate("document.querySelector('.evaluation-result').innerText"));

  await go('PRJ-00003/tasks', "document.querySelector('[data-action=\"create-task\"]')"); await click('[data-action="create-task"]'); await set('v93-task-description', '避免旧批次版本覆盖新的异常登记，希望增加预期版本匹配前提'); await set('v93-task-root', 'FEAT-M02'); await set('v93-task-source', 'REQ-SMOKE-94'); await click('[data-action="save-task"]'); await waitFor("document.querySelector('.task-block')");
  check('简单描述只形成待澄清需求', (await evaluate("document.querySelector('.task-main').innerText")).includes('待澄清'), await evaluate("document.querySelector('.task-main').innerText.slice(0,800)"));
  await click('[data-action="propose-v93-decision"]'); await waitFor("document.querySelector('#v93-condition-input')"); await set('v93-condition-input', 'expectedVersionMatch'); await set('v93-condition-label', '预期批次版本是否匹配'); await set('v93-condition-meaning', '请求预期版本必须与当前批次版本一致'); await set('v93-condition-value', 'true'); await set('v93-change-narrative', '生产中、检验依据完整且预期版本匹配时才允许登记；未命中与异常沿用原规范'); await click('[data-action="save-v93-decision"]'); await waitFor("document.querySelector('.decision-record')");
  check('候选保存但未批准', (await evaluate("document.querySelector('.decision-record').innerText")).includes('待批准'), await evaluate("document.querySelector('.decision-record').innerText"));
  await click('[data-action="record-v93-delivery"]'); await set('v93-delivery-code', 'commit-sim-smoke-94'); await set('v93-delivery-test', '模拟正例、反例、边界和回归'); await click('[data-action="save-v93-delivery"]'); await waitFor("document.querySelector('.delivery-summary')");
  await set('app-role', 'owner'); await go('PRJ-00003/tasks', "document.querySelector('[data-action=\"approve-v93-decision\"]')"); await click('[data-action="approve-v93-decision"]'); await set('decision-rationale', '防止并发覆盖，批准形成知识草稿'); await click('[data-action="confirm-v93-decision"]'); await waitFor("document.querySelector('.review-panel')");
  check('Owner 批准生成真实知识草稿', await evaluate("JSON.parse(localStorage.getItem('business-atlas-v9')).projects['PRJ-00003'].draftWorkspace.deliveryLinks.length === 1"), true);
  await set('cs-title', '增加批次版本匹配前提'); await set('cs-reason', '防止旧版本异常登记覆盖新批次状态'); await click('[data-action="submit-changeset"]'); await waitFor("document.querySelector('[data-action=\"publish-changeset\"]')"); await click('[data-action="publish-changeset"]'); await waitFor("document.querySelector('.history-item')");
  const published = await evaluate("({baseline:JSON.parse(localStorage.getItem('business-atlas-v9')).projects['PRJ-00003'].versionLayers.baseline,task:JSON.parse(localStorage.getItem('business-atlas-v9')).projects['PRJ-00003'].tasks[0].deliveryState})");
  check('发布形成新基线并更新 Task 知识状态', published.task === 'knowledge-approved-contract-review-required', published); await capture('02-knowledge-published.png');

  await go('PRJ-00003/contracts', "document.querySelector('[data-action=\"generate-task-contract\"]')"); await click('[data-action="generate-task-contract"]'); await waitFor("document.querySelector('.delivery-summary')");
  await evaluate(`(()=>{const store=JSON.parse(localStorage.getItem('business-atlas-v9')),project=store.projects['PRJ-00003'],task=project.tasks[0],context=AtlasV8Read.context(project,{versionMode:'published',baselineId:project.versionLayers.baseline,permissionContext:{role:'owner',clearance:'restricted'},applicableContext:{environment:'production'}});for(const item of task.impactInvestigation.items){AtlasV93Task.recordImpactDecision(project,context,task.id,item.id,{status:item.status==='required'?'required':'no-change',completion:item.status==='required'?'done':'checked',rationale:item.status==='required'?'模拟实现和测试已覆盖':'已检查现有关系，无需修改'})}AtlasV93Task.recordLegacyPathReview(project,context,task.id,{rationale:'人工检查现有调用与兼容记录，未执行真实仓库调查'});localStorage.setItem('business-atlas-v9',JSON.stringify(store));return true})()`);
  await send('Page.reload', { ignoreCache: true }); await waitFor("document.querySelector('[data-action=\"complete-v93-task\"]')"); await click('[data-action="complete-v93-task"]'); await waitFor("document.querySelector('.task-header').innerText.includes('已完成') || document.querySelector('.task-main').innerText.includes('已完成')");
  check('模拟 Task 完成内部验收', await evaluate("JSON.parse(localStorage.getItem('business-atlas-v9')).projects['PRJ-00003'].tasks[0].deliveryState === 'fully-delivered'"), await evaluate("JSON.parse(localStorage.getItem('business-atlas-v9')).projects['PRJ-00003'].tasks[0].deliveryState")); await capture('03-task-completed.png');

  await click('[data-action="open-import"]'); await send('DOM.enable'); const documentNode = await send('DOM.getDocument', { depth: 0 }); const fileInput = await send('DOM.querySelector', { nodeId: documentNode.root.nodeId, selector: '#akp-file' }); await send('DOM.setFileInputFiles', { nodeId: fileInput.nodeId, files: [path.join(root, 'fixtures/import-smoke.akp.json')] }); await click('[data-action="preview-import"]'); await waitFor("document.querySelector('[data-action=\"commit-import\"]')"); await click('[data-action="commit-import"]'); await waitFor("document.querySelector('.review-panel')");
  const imported = await evaluate("(()=>{const p=JSON.parse(localStorage.getItem('business-atlas-v9')).projects['PRJ-00003'];return {draft:!!p.draftWorkspace,entities:Object.keys(p.draftWorkspace?.entities||{}).length,proposals:p.importProposals.slice(0,3).map(x=>({id:x.proposalId,status:x.status})),toasts:[...document.querySelectorAll('.toast')].map(x=>x.textContent)}})()"); check('AKP 文件导入进入草稿', imported.draft && imported.entities > 0, imported);
  await send('Page.reload', { ignoreCache: true }); await waitFor("document.querySelector('.review-panel')");
  check('重开后草稿与 Task 均保留', await evaluate("JSON.parse(localStorage.getItem('business-atlas-v9')).projects['PRJ-00003'].draftWorkspace.operations.length > 0 && JSON.parse(localStorage.getItem('business-atlas-v9')).projects['PRJ-00003'].tasks[0].deliveryState === 'fully-delivered'"), true);

  const widths = [];
  for (const [width, height] of [[1280, 800], [800, 600]]) { await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false }); const measure = await evaluate("({document:document.documentElement.scrollWidth,body:document.body.scrollWidth,viewport:innerWidth})"); check(`${width} 无整页横向溢出`, measure.document <= width && measure.body <= width, measure); widths.push({ width, height, ...measure }); }
  check('无控制台错误', errors.length === 0, errors); check('无外部网络请求', requests.filter(value => /^https?:/.test(value)).length === 0, requests.filter(value => /^https?:/.test(value)));
  const result = { acceptedAt: new Date().toISOString(), mode: 'file://', checks, widths, consoleErrors: errors, externalRequests: requests.filter(value => /^https?:/.test(value)), screenshots: fs.readdirSync(out).filter(file => file.endsWith('.png')), independentHumanTester: false };
  fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify(result, null, 2)); fs.writeFileSync(path.join(root, 'artifacts/v9-4-smoke-results.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify({ checks: `${checks.filter(item => item.passed).length}/${checks.length}`, screenshots: result.screenshots.length, consoleErrors: errors.length, externalRequests: result.externalRequests.length }, null, 2)); ws.close();
}
main().catch(error => { console.error(error.stack); process.exitCode = 1; }).finally(() => chrome.kill());
