/* Text-first business reading and task interactions. No SVG, canvas or visual-layout runtime. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const U = commonjs ? require('./ui-utils.js') : root.AtlasUI;
  const Access = commonjs ? require('./v9-access.js') : root.AtlasV9Access;
  const D = commonjs ? require('./decision.js') : root.AtlasV93Decision;
  const Reading = commonjs ? require('./knowledge-reading.js') : root.AtlasV93Reading;
  const Task = commonjs ? require('./task.js') : root.AtlasV93Task;
  const api = factory(C, U, Access, D, Reading, Task);
  if (commonjs) module.exports = api;
  root.AtlasV93UI = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (C, U, Access, D, Reading, Task) {
  'use strict';
  const esc = U.esc, clone = C.clone;
  const badge = (text, tone = 'blue') => U.badge(text, tone);
  const empty = (title, copy = '') => U.empty(title, copy || '这里只展示当前有依据的内容，不会补写未知事实。');
  const entityName = (api, id) => id ? Access.readEntity(api.M(), api.readContext(), id).data?.name || null : null;
  const link = (api, id, detail = '') => { const entity = id ? Access.readEntity(api.M(), api.readContext(), id).data : null; return entity ? `<button class=" knowledge-link" data-open-studio="${esc(id)}"><b>${esc(entity.name)}</b>${detail ? `<small>${esc(detail)}</small>` : ''}</button>` : ''; };
  const short = (value, max = 180) => { const text = String(value ?? ''); return text.length > max ? `${text.slice(0, max - 1)}…` : text; };
  const pretty = value => value == null || value === '' ? '待确认' : Array.isArray(value) ? value.join('、') : typeof value === 'object' ? JSON.stringify(value) : String(value);
  const statusText = { consistent: '已确认一致', inconsistent: '已确认不一致', missing_target: '缺少目标规范', missing_observation: '缺少实现观察', requires_reverification: '需要重新核验', partial: '部分适用', unverified: '待核验', 'fully-delivered': '已完成', 'implementation-complete-knowledge-pending': '知识待审批', 'not-ready': '尚未闭合', 'clarification-required': '待澄清需求', 'candidate-target-required': '候选目标待形成', 'business-approval-pending': '业务决定待批准', 'business-decision-pending': '业务决定待确认', 'knowledge-draft-pending': '知识草稿待审核', 'knowledge-approved-contract-review-required': '知识已发布，待复核契约', 'business-rejected': '候选已拒绝' };
  const statusBadge = status => badge(statusText[status] || status, ['consistent', 'fully-delivered'].includes(status) ? 'green' : ['inconsistent', 'business-rejected'].includes(status) ? 'red' : ['unverified', 'missing_target', 'missing_observation', 'requires_reverification', 'not-ready', 'clarification-required'].includes(status) ? 'amber' : 'blue');
  const inputsInCondition = (condition, output = { inputs: new Set(), operators: new Set() }) => {
    if (!condition) return output;
    if (condition.inputId) output.inputs.add(condition.inputId);
    if (condition.op) output.operators.add(condition.op);
    (condition.conditions || []).forEach(child => inputsInCondition(child, output));
    return output;
  };
  function evaluationBasis(rule, condition) {
    const semantics = inputsInCondition(condition), inputs = [...semantics.inputs].map(id => rule.version?.inputs?.find(input => input.id === id)).filter(Boolean);
    const operators = [...semantics.operators].filter(op => !['all', 'any'].includes(op)).map(op => D.OPERATOR_LABELS[op] || op);
    const names = [...semantics.inputs].map(id => inputs.find(input => input.id === id)?.label || id);
    const stringNote = operators.some(label => /包含|前缀/.test(label)) ? '字符串大小写与空值按已声明规则处理；未声明则停止模拟。' : '必要输入缺失时不作确定判断。';
    return `检查${names.join('、') || '相关输入'}；采用${operators.join('、') || '待确认的比较方式'}。${stringNote}`;
  }
  function outcomeText(rule, row) {
    const outcome = row.outcome?.label || row.outcomeId || '结果待确认', fallback = rule.defaultOutcome ? `未命中时执行“${typeof rule.defaultOutcome === 'string' ? (rule.version.outcomes || []).find(item => item.id === rule.defaultOutcome)?.label || rule.defaultOutcome : rule.defaultOutcome.label || '已定义默认结果'}”` : '未命中且没有默认结果时返回未知';
    return `成立时：${outcome}。${fallback}。`;
  }
  function purpose(api, view) {
    const root = view.root, actors = view.actors.map(actor => actor.name).join('、') || '主要使用者待明确';
    return `<section class="reading-summary"><p>${esc(root.description || root.goal || '业务目的待确认')}</p><dl><div><dt>主要使用者</dt><dd>${esc(actors)}</dd></div><div><dt>业务结果</dt><dd>${esc(root.props?.finalBusinessResult || root.result || root.goal || '待明确')}</dd></div></dl></section>`;
  }
  function decisionCard(api, rule) {
    const version = rule.version;
    return `<article class="decision-card"><header><div><h3>${esc(rule.entity.name)}</h3><p>${esc(version?.businessPurpose || version?.summary || rule.entity.description || '')}</p></div>${statusBadge(rule.approved ? 'consistent' : 'proposed')}</header><dl class="compact-facts"><div><dt>适用范围</dt><dd>${esc(version?.scope || '待确认')}</dd></div><div><dt>规则顺序</dt><dd>${esc(rule.priority.confirmed ? '按已批准的判断行顺序处理' : '优先级待确认，冲突时不自动选择')}</dd></div></dl>${version?.supportsEvaluation ? `<div class="table-scroll"><table class="decision-table"><thead><tr><th>业务情况</th><th>检查什么、怎样比较</th><th>处理结果</th></tr></thead><tbody>${rule.rows.map(row => `<tr><td>${esc(row.exception ? `例外：${row.explanation}` : row.explanation)}</td><td>${esc(evaluationBasis(rule, row.condition))}</td><td>${esc(outcomeText(rule, row))}</td></tr>`).join('')}</tbody></table></div><div class="default-note"><b>默认与异常：</b>${esc(rule.defaultOutcome ? outcomeText(rule, { outcomeId: typeof rule.defaultOutcome === 'string' ? rule.defaultOutcome : rule.defaultOutcome.outcomeId, outcome: null }) : '没有已批准默认结果；未命中时返回未知。')} ${esc(rule.exceptionHandling)}</div>` : `<p>${esc(version?.constraint || version?.summary || '人工约束，不能自动求值。')}</p>`}<details><summary>输入含义、完整性与依据</summary>${(version?.inputs || []).map(input => `<p><b>${esc(input.label || input.id)}</b>：${esc(input.businessMeaning || input.description || '业务含义待确认')}<small>技术字段 ${esc(input.id)}</small></p>`).join('') || '<p>尚未定义结构化输入。</p>'}<p>默认与异常：${esc(rule.exceptionHandling)}</p>${rule.sources.map(source => link(api, source.resourceId, `${source.sourceVersion || '来源'} · ${short(source.locator, 100)}`)).join('') || '<p>当前没有可读取来源。</p>'}<p><small>规则版本 ${esc(version?.version || '待确认')} · 实体修订 ${esc(rule.entity.revision || '待确认')}</small></p></details></article>`;
  }
  function evaluationResult(rule, result) {
    if (!result) return '<p class="muted">输入后点击评估。空白表示未知，不会自动采用默认值。</p>';
    return `<section class="evaluation-result ${esc(result.status)}"><b>${esc(result.outcome?.label || ({ MISSING_INPUT: '缺少必要事实，无法判断', UNSUPPORTED_CONDITION: '条件暂不支持模拟', NO_MATCH_NO_DEFAULT: '无命中且无默认结果', UNAPPROVED_TARGET: '候选规则不能正式评估', APPLICABILITY_UNKNOWN: '适用条件未知' }[result.code] || result.code))}</b><p>${esc(result.explanation || '')}</p>${result.missingFacts?.length ? `<p><strong>缺失：</strong>${esc(result.missingFacts.map(id => rule.version?.inputs?.find(input => input.id === id)?.label || id).join('、'))}</p>` : ''}${result.conflicts?.length ? `<p><strong>冲突：</strong>${esc(result.conflicts.map(item => item.reason).join('；'))}</p>` : ''}<details><summary>查看逐条判断轨迹</summary>${(result.evaluatedRows || []).map(row => `<p>${esc(row.description)}：<b>${esc(row.status)}</b> · ${esc(row.reason)}</p>`).join('') || '<p>没有可执行判断行。</p>'}</details></section>`;
  }
  function caseSimulator(api, view) {
    const executable = view.targetRules.filter(rule => rule.version?.supportsEvaluation);
    if (!executable.length) return `<section><h2>具体案例</h2>${empty(view.observations.length ? '只有当前观察，缺少可验证的目标规则' : '没有可自动验证的业务规则')}</section>`;
    const rule = executable.find(item => item.entity.id === api.Ctx().v93EvaluationRuleId) || executable[0], ruleId = rule.entity.id, values = api.Ctx().v93EvaluationInputs?.[ruleId] || {}, result = api.Ctx().v93EvaluationResults?.[ruleId], cases = view.cases.filter(item => item.props?.decisionRef?.ruleId === ruleId);
    return `<section class="case-section"><h2>具体案例</h2>${cases.length ? `<div class="case-list">${cases.map(item => { const kind = { positive: '正例', negative: '反例', boundary: '边界', unknown: '未知', regression: '回归' }[item.props.caseKind] || '案例'; return `<button data-v93-case="${esc(item.id)}"><span>${kind}</span><b>${esc(item.name)}</b><small>预期：${esc(item.props.expected?.outcomeId === 'UNKNOWN' ? '无法判断' : item.props.expected?.outcomeId)}</small></button>`; }).join('')}</div>` : '<p class="muted">尚未提供结构化案例，可以手工输入验证。</p>'}<div class="simulator"><h3>试一条业务数据</h3>${executable.length > 1 ? `<select id="v93-eval-rule" aria-label="选择规则">${executable.map(item => `<option value="${item.entity.id}" ${item.entity.id === ruleId ? 'selected' : ''}>${esc(item.entity.name)}</option>`).join('')}</select>` : ''}<div class="simulator-inputs">${(rule.version.inputs || []).map(input => { const value = values[input.id] ?? '', options = input.options || []; return `<label>${esc(input.label || input.id)}${options.length ? `<select data-v93-eval-input="${esc(input.id)}" data-rule="${esc(ruleId)}"><option value="">未知 / 未提供</option>${options.map(option => `<option value="${esc(option.value)}" ${String(value) === String(option.value) ? 'selected' : ''}>${esc(option.label)}</option>`).join('')}</select>` : `<input data-v93-eval-input="${esc(input.id)}" data-rule="${esc(ruleId)}" value="${esc(value)}" placeholder="未提供">`}<small>${esc(input.businessMeaning || input.description || `技术字段 ${input.id}`)}</small></label>`; }).join('')}</div><button class="button primary" data-action="evaluate-v93-rule" data-rule="${esc(ruleId)}">评估</button><button class="button" data-action="clear-v93-evaluation" data-rule="${esc(ruleId)}">清空</button>${evaluationResult(rule, result)}</div></section>`;
  }
  function flowExplanation(api, view) {
    if (view.root.typeId !== 'Scenario' || !view.flows.length) return '';
    const flow = view.flows[0], selected = api.Ctx().flowStageId || flow.stages[0]?.id, stage = flow.stages.find(item => item.id === selected) || flow.stages[0];
    return `<section class="flow-reading"><h2>业务怎样一步步处理</h2><p class="muted">以下顺序和分支均来自真实 Flow，不根据阶段排列补充关系。</p><ol class="stage-list">${flow.stages.map(item => `<li><button data-flow-stage="${esc(item.id)}" class="${item.id === stage?.id ? 'active' : ''}"><b>${esc(item.name)}</b>${item.goal ? `<p>${esc(item.goal)}</p>` : ''}<small>${item.nodes.length} 个业务环节</small></button></li>`).join('')}</ol>${stage ? `<section class="stage-detail"><h3>${esc(stage.name)}：详细处理</h3>${stage.nodes.map(node => `<p><b>${esc(node.name)}</b> · ${esc(node.type === 'decision' ? '业务判断' : node.type === 'event' ? '业务事件' : '业务动作')}</p>`).join('')}<div class="branch-list">${stage.edges.map(edge => `<p><b>${esc(edge.fromName)} → ${esc(edge.toName)}</b>：${esc(edge.semanticLabel)}${edge.ruleName ? `（${esc(edge.ruleName)}${edge.outcomeLabel ? ` · ${esc(edge.outcomeLabel)}` : ''}）` : ''}${edge.type === 'return' ? ' · 回流' : edge.type === 'error' ? ' · 异常' : ''}</p>`).join('') || '<p>本阶段没有跨环节分支。</p>'}</div><details><summary>核对全部流程条件与回流</summary>${flow.edges.map(edge => `<p>${esc(edge.fromName)} → ${esc(edge.toName)}：${esc(edge.semanticLabel)}${edge.ruleName ? `（${esc(edge.ruleName)}）` : ''}</p>`).join('')}</details></section>` : ''}</section>`;
  }
  function observationSummary(observation) {
    const output = observation.props?.actualOutput;
    if (!output || typeof output !== 'object') return pretty(output);
    return Object.entries(output).map(([key, value]) => {
      if (value && typeof value === 'object' && !Array.isArray(value)) {
        const parts = Object.entries(value).map(([name, item]) => `${name}=${Array.isArray(item) ? item.join('、') : item}`);
        return `${key}：${parts.join('；')}`;
      }
      return `${key}：${Array.isArray(value) ? value.join('、') : value}`;
    }).join('；');
  }
  function observedAndMechanism(api, view) {
    return `<section class="implementation-reading"><h2>当前系统怎样实现</h2>${statusBadge(view.conformance.status)}<p class="muted">${esc(view.conformance.explanation)}</p>${view.observations.length ? view.observations.map(observation => `<article class="observation"><h3>${esc(observation.name)}</h3><p>${esc(observation.description)}</p><dl class="compact-facts"><div><dt>实际结果</dt><dd>${esc(observationSummary(observation))}</dd></div><div><dt>观察版本 / 环境</dt><dd>${esc(observation.props?.observedVersion || '待确认')} · ${esc(observation.props?.environment || '待确认')}</dd></div><div><dt>观察方式</dt><dd>${esc(observation.props?.method || '待确认')}</dd></div></dl><details><summary>观察输入与依据</summary><pre>${esc(JSON.stringify(observation.props?.input, null, 2))}</pre><p>${esc(observation.props?.conformance?.rationale || '尚未形成符合性结论')}</p></details></article>`).join('') : empty('尚无当前实现行为观察，符合性未验证')}<details class="mechanism-details"><summary>实现机制与技术依据</summary>${view.mechanisms.map(entity => `<article><h3>${link(api, entity.id, entity.typeId)}</h3><p>${esc(entity.description || entity.props?.responsibility || '')}</p><p>机制：${esc(entity.props?.mechanism || entity.props?.technicalDetail || entity.props?.transaction || '详见技术映射')} · 版本：${esc(entity.props?.currentCodeVersion || entity.props?.codeVersion || entity.props?.implementationVersion || '待确认')}</p>${entity.props?.businessMeaningApproval === 'not-approved' ? '<p><b>技术机制尚未获得业务语义批准，不能当成目标规则。</b></p>' : ''}</article>`).join('') || '<p>尚未记录技术机制。</p>'}</details></section>`;
  }
  function technicalReading(api, view) {
    const root = view.root, props = root.props || {};
    return `${purpose(api, view)}<section><h2>职责与处理</h2><dl class="detail-list">${[['业务作用', root.description || root.goal], ['调用前提 / 输入', props.input || root.input], ['主要处理', props.responsibility || props.mechanism || root.purpose], ['输出', props.output || root.output], ['异常', props.failure || root.props?.exceptionHandling], ['实现组件 / 版本', props.currentCodeVersion || props.codeVersion || props.implementationVersion || props.contractVersion]].filter(([, value]) => value).map(([label, value]) => `<div><dt>${label}</dt><dd>${esc(pretty(value))}</dd></div>`).join('')}</dl></section>${root.typeId === 'BusinessObject' ? `<section><h2>状态与转移</h2><p>合法状态：${esc((root.states || root.props?.states || []).join('、') || '待确认')}</p>${(root.transitions || []).map(transition => `<p>${esc(transition.fromState)} → ${esc(transition.toState)}：${esc(transition.guard)} · ${transition.verificationStatus === 'confirmed' ? '已确认' : '待核验'}</p>`).join('') || '<p>尚未定义状态转移，不按状态排列推断。</p>'}</section>` : ''}${view.targetRules.length ? `<section><h2>相关业务规则</h2>${view.targetRules.map(rule => decisionCard(api, rule)).join('')}</section>` : ''}${observedAndMechanism(api, view)}${evidence(api, view)}`;
  }
  function evidence(api, view) {
    return `<details class="evidence"><summary>详细依据与版本</summary>${view.unknowns.map(item => `<p><b>${esc(item.title)}</b>：${esc(item.impact)}</p>`).join('')}${view.conflicts.map(item => `<p><b>${esc(item.title)}</b>：${esc(item.resolution || '冲突待解决')}</p>`).join('')}${view.sources.map(source => link(api, source.id, source.sourceVersion || '来源')).join('') || '<p>当前没有可读取来源。</p>'}<p><small>批准基线 ${esc(api.readContext().baselineId)} · 阅读模式 ${esc(api.readContext().versionMode)}</small></p></details>`;
  }
  function readingWorkbench(api, projection) {
    const view = Reading.decisionProjection(api.M(), api.readContext(), projection.root.id);
    if (!view.ok) return empty('当前知识不可读', view.code);
    if (!['Feature', 'Scenario', 'Rule'].includes(view.root.typeId)) return `<main class="reading-page">${technicalReading(api, view)}</main>`;
    return `<main class="reading-page">${purpose(api, view)}${flowExplanation(api, view)}<section><h2>它怎样判断和处理</h2>${view.targetRules.length ? view.targetRules.map(rule => decisionCard(api, rule)).join('') : empty('缺少已批准目标业务规范，无法确定“业务应该怎样”')}</section>${caseSimulator(api, view)}${observedAndMechanism(api, view)}${evidence(api, view)}</main>`;
  }

  function taskIntent(api, task) {
    const sc = task.semanticContract;
    return `<section class="task-block"><h2>本次想改变什么</h2><p>${esc(sc.businessIntent.problem || sc.businessIntent.goal || '待澄清需求')}</p>${sc.targetBehavior.narrative ? `<p><b>期望结果：</b>${esc(sc.targetBehavior.narrative)}</p>` : empty('目标行为和验收方式待澄清')}${sc.targetBehavior.decisionChanges?.map(change => `<article class="proposal"><p>${esc(change.naturalLanguage)}</p><small>${esc(entityName(api, change.ruleId) || '新目标规则')} · ${esc(change.baseVersion || '新提案')} → ${esc(change.proposedRevision)} · ${change.approvalStatus === 'approved' ? '已批准' : change.approvalStatus === 'rejected' ? '已拒绝' : '候选'}</small></article>`).join('') || ''}</section>`;
  }
  function confirmedPanel(api, task) {
    const sc = task.semanticContract, impacts = (task.impactInvestigation?.items || []).filter(item => ['required', 'potential'].includes(item.status)).slice(0, 4);
    return `<section class="task-block"><h2>目前已确认什么</h2>${sc.existingBehavior.specificationRefs?.length ? sc.existingBehavior.specificationRefs.map(ref => link(api, ref.ruleId, `${ref.version} · 已批准规范`)).join('') : '<p>尚无关联的已批准规则。</p>'}${sc.existingBehavior.observedBehaviorRefs?.map(id => link(api, id, '当前观察')).join('') || '<p>尚无当前实现观察。</p>'}${impacts.length ? `<h3>有依据的影响候选</h3><p class="muted">仅基于现有知识，未执行真实仓库调查。</p>${impacts.map(item => `<p>• ${esc(entityName(api, item.entityId) || item.category)}：${esc(item.rationale)}</p>`).join('')}${(task.impactInvestigation.items || []).length > impacts.length ? `<details><summary>查看全部 ${task.impactInvestigation.items.length} 条候选与处理记录</summary>${task.impactInvestigation.items.map(item => impactEditor(api, task, item)).join('')}</details>` : impactEditor(api, task, impacts[0])}` : '<p>目前没有足够关系证据生成影响候选。</p>'}</section>`;
  }
  function impactEditor(api, task, item) {
    if (!item) return '';
    return `<article class="impact-item" data-impact="${esc(item.id)}"><b>${esc(entityName(api, item.entityId) || item.category)}</b><p>${esc(item.rationale)}</p><div class="impact-controls"><select data-impact-status aria-label="影响判断">${Object.entries(Task.STATUS_LABELS).map(([value, label]) => `<option value="${value}" ${item.status === value ? 'selected' : ''}>${esc(label)}</option>`).join('')}</select><select data-impact-completion aria-label="处理进度">${[['unknown', '待处理'], ['pending', '进行中'], ['done', '已完成'], ['verified', '已核验'], ['checked', '已检查'], ['pending-knowledge-approval', '知识待审批']].map(([value, label]) => `<option value="${value}" ${item.completion === value ? 'selected' : ''}>${label}</option>`).join('')}</select><input data-impact-rationale value="${esc(item.rationale || '')}" aria-label="调查依据"><button class="button small" data-action="save-v93-impact" data-task="${esc(task.id)}" data-impact="${esc(item.id)}">保存结论</button></div></article>`;
  }
  function decisionPanel(api, task) {
    const records = (api.M().decisionRecords || []).filter(record => record.taskId === task.id), role = api.Ctx().role;
    const content = records.length ? records.map(record => {
      const recommendation = record.recommendation ? `<p>建议：${esc(record.options?.find(option => option.id === record.recommendation.optionId)?.label || record.recommendation.optionId)}。${esc(record.recommendation.rationale)}</p>` : '';
      const decision = record.approval ? `<p>${record.approval.decision === 'approved' ? '已批准' : '已拒绝'} · ${esc(record.approval.by)} · ${esc(record.approval.rationale)}</p>` : `<p>${statusBadge('business-approval-pending')}</p>`;
      const actions = !record.approval && ['owner', 'admin'].includes(role) ? `<div class="actions"><button class="button primary" data-action="approve-v93-decision" data-task="${esc(task.id)}" data-proposal="${esc(record.decisionId)}">批准并形成知识草稿</button><button class="button" data-action="reject-v93-decision" data-task="${esc(task.id)}" data-proposal="${esc(record.decisionId)}">拒绝候选</button></div>` : !record.approval ? '<p class="muted">需要业务 Owner 批准或拒绝，当前角色只能查看。</p>' : '';
      return `<article class="decision-record"><p><b>${esc(record.question)}</b></p>${recommendation}${decision}${actions}</article>`;
    }).join('') : '<p>当前没有单独的业务决定。</p>';
    const unknown = (task.semanticContract.businessDifference.unknown || []).map(item => `<p>• ${esc(item)}</p>`).join('');
    const legacy = task.impactInvestigation.legacyPathCheck?.status !== 'checked' ? `<details><summary>旧路径与兼容需要人工复核</summary><p>${esc(task.impactInvestigation.legacyPathCheck?.conclusion || '尚未检查')}</p><button class="button" data-action="review-v93-legacy" data-task="${esc(task.id)}">记录人工检查结果</button></details>` : '<p>旧路径与兼容已人工复核。</p>';
    return `<section class="task-block"><h2>还需要解决什么</h2>${content}${unknown}${legacy}</section>`;
  }
  function deliveryPanel(api, task, closure) {
    const delivery = task.delivery;
    return `<section class="task-block"><h2>下一步与交付状态</h2>${statusBadge(closure.state)}<div class="actions"><button class="button" data-action="propose-v93-decision" data-task="${esc(task.id)}">提出业务变化</button><button class="button" data-action="record-v93-delivery" data-task="${esc(task.id)}">记录模拟开发与测试</button><button class="button" data-action="generate-task-contract" data-task="${esc(task.id)}">生成 / 复核知识契约</button><button class="button" data-action="export-v93-task" data-task="${esc(task.id)}">导出任务记录</button>${task.status !== 'completed' ? `<button class="button primary" data-action="complete-v93-task" data-task="${esc(task.id)}">检查并验收</button>` : ''}</div>${delivery ? `<div class="delivery-summary"><p>实现：${esc(delivery.implementation?.codeRevision || '待补充')} · 测试：${esc(delivery.tests?.map(test => test.status).join('、') || '待补充')} · 知识：${esc(delivery.knowledgeChanges?.map(change => change.status).join('、') || '待补充')}</p><p>交付清单：${esc(delivery.deliveryManifest?.id || '待生成')} · 批准基线：${esc(delivery.deliveryManifest?.approvedBaselineId || '尚未形成')}</p>${delivery.simulation ? '<p class="muted">以上是明确标注的模拟记录，只证明原型内部规则闭合，不代表真实开发或部署。</p>' : ''}</div>` : '<p>尚未记录实现与测试。</p>'}${closure.blockers.length ? `<details><summary>当前还不能完成的原因（${closure.blockers.length}）</summary>${[...new Map(closure.blockers.map(item => [item.code + item.reason, item])).values()].map(item => `<p>• ${esc(item.reason)}</p>`).join('')}</details>` : ''}${closure.warnings.length ? `<p class="muted">${esc(closure.warnings.map(item => item.reason).join('；'))}</p>` : ''}<details><summary>版本与知识交付详情</summary><dl class="detail-list">${Object.entries(closure.versions || {}).map(([key, value]) => `<div><dt>${esc({ requirement: '需求版本', approvedBusinessBaseline: '业务基线', code: '实现版本', tested: '测试版本', deployment: '部署版本', knowledgeProposal: '知识提案', manifestApprovedBaseline: '清单批准基线' }[key] || key)}</dt><dd>${esc(pretty(value))}</dd></div>`).join('')}</dl></details></section>`;
  }
  function taskDetail(api, selectedTask, tasks, issues) {
    const task = Task.readTask(api.M(), api.readContext(), selectedTask.id).data;
    if (!task) return empty('任务不可读');
    const closure = Task.evaluateDelivery(api.M(), api.readContext(), task.id);
    return `<div class="task-layout"><aside class="task-list">${tasks.map(item => `<button data-task="${item.id}" class="${item.id === task.id ? 'active' : ''}"><b>${esc(item.title)}</b><small>${esc(entityName(api, item.rootId) || '待关联功能')}</small></button>`).join('')}</aside><main class="task-main"><header class="task-header"><div><h2>${esc(task.title)}</h2><p>${link(api, task.rootId, '查看关联业务') || '待关联功能'}</p></div>${statusBadge(task.deliveryState || task.status)}</header>${taskIntent(api, task)}${confirmedPanel(api, task)}${decisionPanel(api, task)}${deliveryPanel(api, task, closure)}${issues.length ? `<details><summary>关联问题（${issues.length}）</summary>${issues.map(issue => `<p>${esc(issue.title)} · ${esc(issue.status)}</p>`).join('')}</details>` : ''}</main></div>`;
  }
  function newTaskForm(api, rootId) {
    const candidates = api.currentEntities().filter(entity => ['Feature', 'Requirement', 'Scenario'].includes(entity.typeId));
    return `<p class="muted">先记录真实需求。信息不足时只创建待澄清需求，不会生成看似完整的业务合同。</p><label>需求描述 *<textarea id="v93-task-description" rows="5" placeholder="说明遇到的具体问题、希望改变的结果；不知道技术影响可以留到后续调查"></textarea></label><label>关联功能（可选）<select id="v93-task-root"><option value="">暂不关联，后续澄清</option>${candidates.map(entity => `<option value="${esc(entity.id)}" ${entity.id === rootId ? 'selected' : ''}>${esc(entity.name)}</option>`).join('')}</select></label><label>附件或来源（可选）<input id="v93-task-source" placeholder="需求链接、会议记录或附件定位"></label><input id="v93-task-type" type="hidden" value="clarification">`;
  }
  function decisionChangeForm(api, task) {
    const decision = Reading.decisionProjection(api.M(), api.readContext(), task.rootId), rules = decision.approvedRules || [];
    if (!rules.length) return empty('当前缺少已批准规则，请先澄清目标规范，不能从技术观察直接生成批准规则');
    const rule = rules[0];
    return `<p class="muted">候选只引用“${esc(rule.entity.name)}@${esc(rule.version?.version)} + 判断行差异”，不会复制或自动发布规范。</p><label>已批准规则<select id="v93-change-rule">${rules.map(item => `<option value="${item.entity.id}">${esc(item.entity.name)} · ${esc(item.version?.version)}</option>`).join('')}</select></label><label>要调整的判断行<select id="v93-change-row">${(rule.version?.rows || []).map(row => `<option value="${row.id}">${esc(row.id)} · ${esc(D.describeCondition(D.conditionFromRow(row), rule.version))}</option>`).join('')}</select></label><label>新增业务输入（可选）<input id="v93-condition-input" placeholder="例如 expectedVersionMatch"></label><label>输入的业务名称<input id="v93-condition-label" placeholder="例如：预期批次版本是否匹配"></label><label>输入的业务含义<textarea id="v93-condition-meaning" placeholder="说明这个事实从哪里来、为什么影响业务判断"></textarea></label><div class="form-grid"><label>比较方式<select id="v93-condition-op">${D.SUPPORTED_OPERATORS.filter(op => !['all', 'any'].includes(op)).map(op => `<option value="${op}">${esc(D.OPERATOR_LABELS[op])}</option>`).join('')}</select></label><label>比较值<input id="v93-condition-value" placeholder="true / false 或具体值"></label></div><label>目标业务结果<select id="v93-change-outcome">${(rule.version?.outcomes || []).map(outcome => `<option value="${outcome.id}">${esc(outcome.label)}</option>`).join('')}</select></label><label>候选目标说明 *<textarea id="v93-change-narrative" placeholder="明确原条件与新条件怎样组合，以及未命中和异常如何处理"></textarea></label><label>推荐依据<textarea id="v93-change-reason" placeholder="为什么建议这样改；推荐不等于批准"></textarea></label>`;
  }
  function deliveryForm() {
    return `<p class="muted">这里只记录模拟实现和测试。不会连接真实仓库、测试执行器或部署平台。</p><label>模拟实现版本 *<input id="v93-delivery-code" placeholder="例如 commit-sim-001"></label><label>模拟测试说明 *<textarea id="v93-delivery-test" placeholder="记录模拟覆盖了哪些正例、反例、边界和回归；不是实际执行日志"></textarea></label><label>模拟部署版本（可选）<input id="v93-delivery-deployment" placeholder="deployment-sim-001"></label>`;
  }
  return { readingWorkbench, taskDetail, newTaskForm, decisionChangeForm, deliveryForm, decisionCard, caseSimulator, evaluationResult, flowExplanation, deliveryPanel };
});
