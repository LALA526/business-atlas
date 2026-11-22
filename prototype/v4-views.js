/* Business Atlas V4 — Presentation Registry. All components consume project knowledge, never industry page names. */
(function (root, factory) {
  const core = typeof module === 'object' && module.exports ? require('./v4-core.js') : root.AtlasCore;
  const engine = typeof module === 'object' && module.exports ? require('./engine.js') : root.AtlasEngine;
  const api = factory(core, engine);
  if (typeof module === 'object' && module.exports) module.exports = api;
  root.AtlasViews = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (K, E) {
  'use strict';
  const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
  const badge = (text, tone = '') => `<span class="v4-badge ${tone}">${esc(text)}</span>`;
  const empty = (title, copy) => `<div class="v4-empty"><b>${esc(title)}</b><p>${esc(copy)}</p></div>`;
  const typeName = (project, typeId) => project.schemaRegistry?.[typeId]?.name || K.TYPE_LABELS[typeId] || typeId || '知识';
  const entityLink = (project, id, label) => {
    const item = K.getEntity(project, id); if (!item) return esc(label || id);
    return `<button class="v4-inline-link" data-entity="${esc(id)}">${esc(label || item.name)}<span>${esc(id)}</span></button>`;
  };
  const prop = (label, value) => value === undefined || value === null || value === '' || (Array.isArray(value) && !value.length) ? '' : `<div class="v4-property"><dt>${esc(label)}</dt><dd>${esc(typeof value === 'object' ? JSON.stringify(value) : value)}</dd></div>`;

  function KnowledgeSummary(project, item) {
    const schema = K.schemaFor(project, item.typeId), selected = item.selectedVersion;
    const description = selected?.summary || item.description || item.goal || item.purpose || item.conclusion || '此对象已建立身份，详细定义仍待补充。';
    return `<section class="v4-object-summary"><div class="v4-summary-label">${esc(schema?.dimension || '项目知识')} · ${esc(typeName(project, item.typeId))}</div><p>${esc(description)}</p>${item.scope || selected?.scope ? `<div class="v4-scope"><span>适用范围</span>${esc(item.scope === 'project' ? selected?.scope || '当前项目' : item.scope || selected?.scope)}</div>` : ''}</section>`;
  }
  function PropertyTable(project, item, edit = false) {
    const schema = K.schemaFor(project, item.typeId), entries = [];
    const known = new Set(['name', 'description']);
    (schema?.fields || []).forEach(definition => {
      if (known.has(definition.id)) return;
      let value = item.props?.[definition.id];
      if (value === undefined && definition.id === 'states') value = item.states;
      if (value !== undefined && value !== null && value !== '') entries.push([definition.label, value, definition]);
    });
    const fallback = {
      Scenario: [['触发条件', item.trigger], ['期望结果', item.result], ['表达模式', item.expressionMode || (item.caseModel ? 'case' : 'flow')]],
      Action: [['业务目的', item.purpose], ['前置条件', item.pre], ['输入', item.input], ['输出', item.output]],
      BusinessObject: [['别名', item.aliases], ['核心属性', item.props], ['生命周期', item.states]],
      Capability: [['所属领域', item.domainId]],
      Configuration: []
    }[item.typeId] || [];
    fallback.forEach(([label, value]) => { if (value && (!Array.isArray(value) || value.length) && !entries.some(entry => entry[0] === label)) entries.push([label, value, {}]); });
    if (!entries.length) return '';
    return `<section class="v4-section"><div class="v4-section-title"><h2>${edit ? '可维护属性' : '主要属性'}</h2><span>${entries.length} 项有值属性</span></div><dl class="v4-properties">${entries.map(([label, value, definition]) => {
      let display = value;
      if (definition.dataType === 'boolean') display = value ? '是' : '否';
      if (definition.dataType === 'ref') return `<div class="v4-property"><dt>${esc(label)}</dt><dd>${entityLink(project, value)}</dd></div>`;
      if (definition.dataType === 'refs') return `<div class="v4-property"><dt>${esc(label)}</dt><dd>${(value || []).map(id => entityLink(project, id)).join('')}</dd></div>`;
      if (Array.isArray(value)) display = value.map(entry => typeof entry === 'object' ? JSON.stringify(entry) : entry).join(' · ');
      else if (typeof value === 'object') display = JSON.stringify(value, null, 2);
      return `<div class="v4-property"><dt>${esc(label)}</dt><dd class="${typeof value === 'object' && !Array.isArray(value) ? 'v4-pre' : ''}">${esc(display)}</dd></div>`;
    }).join('')}</dl></section>`;
  }
  function DecisionTable(project, item) {
    const version = item.selectedVersion || item.versions?.[item.currentVersion] || item.versions?.draft;
    if (!version) return empty('指定规则版本不可用', '不会用其他版本替代请求版本。');
    if (version.supportsEvaluation === false) return `<section class="v4-section"><div class="v4-section-title"><h2>人工核验约束</h2>${badge('不自动求解', 'amber')}</div><p class="v4-longcopy">${esc(version.constraint || version.summary)}</p></section>`;
    const rows = [...(version.exceptions || []).map(row => ({ ...row, exception: true })), ...(version.rows || [])].sort((a, b) => a.priority - b.priority);
    return `<section class="v4-section"><div class="v4-section-title"><div><h2>结构化判断条件</h2><p>决策行是当前 ${esc(version.version)} 修订的权威定义</p></div><button class="v4-button small" data-action="new-rule-change" data-id="${item.id}">提出修改</button></div><div class="v4-table-wrap"><table class="v4-table"><thead><tr><th>优先级</th>${version.inputs.map(input => `<th>${esc(input.label)}</th>`).join('')}<th>结果与解释</th></tr></thead><tbody>${rows.map(row => {
      const outcome = version.outcomes.find(entry => entry.id === row.outcomeId);
      return `<tr class="${row.exception ? 'exception' : ''}"><td><span class="v4-mono">${esc(row.id)}</span><br>${row.exception ? '例外 · ' : ''}${row.priority}</td>${version.inputs.map(input => `<td>${row.when?.[input.id] && row.when[input.id] !== 'any' ? esc(input.options?.find(option => option.value === row.when[input.id])?.label || row.when[input.id]) : '<span class="muted">任意</span>'}</td>`).join('')}<td>${badge(outcome?.label || row.outcomeId, 'purple')}<small>${esc(outcome?.description || '')}</small></td></tr>`;
    }).join('')}</tbody></table></div><div class="v4-simulator"><h3>条件探索 <span>仅试算，不修改业务事实</span></h3><div class="v4-sim-grid">${version.inputs.map(input => `<label>${esc(input.label)}<select data-rule-sim="${esc(input.id)}" data-rule-id="${item.id}">${input.options.map(option => `<option value="${esc(option.value)}">${esc(option.label)}</option>`).join('')}</select></label>`).join('')}</div><div id="rule-sim-result">${RuleSimulation(project, item, E.defaultValues(version))}</div></div></section>`;
  }
  function RuleSimulation(project, item, values) {
    const version = item.selectedVersion || item.versions?.[item.currentVersion] || item.versions?.draft;
    if (!version || version.supportsEvaluation === false) return '';
    const result = E.evaluateRule(version, values);
    return `<div class="v4-sim-result ${result.outcome ? 'ok' : 'warn'}"><b>${esc(result.outcome?.label || result.code)}</b><p>${esc(result.explanation)}</p><span class="v4-mono">${item.id}@${version.version}</span></div>`;
  }
  function DirectedFlow(project, scenario, context = {}) {
    const stages = (scenario.stageIds || []).map(id => E.byId(project.stages, id)).filter(Boolean), flow = E.flowFor(project, scenario.id);
    if (!stages.length && !flow.nodes.length) return empty('尚未建立流程', '当前对象不虚构参与者或阶段，可切换其他适用视图。');
    const values = { ...E.defaultValues(E.byId(project.rules, flow.nodes.find(node => node.ruleId)?.ruleId)?.versions?.[E.byId(project.rules, flow.nodes.find(node => node.ruleId)?.ruleId)?.currentVersion]), ...(context.exploreValues || {}) };
    const evaluation = E.evaluateFlow(project, scenario.id, values);
    return `<section class="v4-section"><div class="v4-section-title"><div><h2>业务主线与条件路径</h2><p>${stages.length} 个阶段 · ${flow.nodes.filter(node => node.actionId).length} 个动作 · 仅展开局部</p></div><button class="v4-button small" data-action="toggle-flow-explore">${context.explore ? '收起条件探索' : '探索业务结果'}</button></div>${context.explore ? FlowExplorer(project, flow, evaluation, values) : ''}<div class="v4-stage-list">${stages.map((stage, index) => {
      const nodes = flow.nodes.filter(node => node.stageId === stage.id && (!node.id.includes('START') || index === 0));
      return `<article class="v4-stage"><header><span>${String(index + 1).padStart(2, '0')}</span><div><h3>${esc(stage.name)}</h3><p>${esc(stage.goal || stage.result || '')}</p></div>${badge(`${nodes.length} 节点`)}</header><div class="v4-flow-nodes">${nodes.map(node => {
        const action = E.byId(project.actions, node.actionId), active = !context.explore || evaluation.nodeIds.has(node.id);
        return `<button class="v4-flow-node ${node.type} ${active ? '' : 'dim'}" ${action ? `data-entity="${action.id}"` : node.ruleId ? `data-entity="${node.ruleId}"` : ''}><span>${esc({ start: '开始', end: '终点', decision: '判断', parallel: '并行', join: '汇合', return: '返回', exception: '异常', event: '事件' }[node.type] || '动作')}</span><b>${esc(action?.name || node.name)}</b>${node.ruleId ? `<small>◇ ${esc(node.ruleId)}</small>` : action ? `<small>${esc(action.id)}</small>` : ''}</button>`;
      }).join('') || '<p class="muted">此阶段保留职责，流程节点待建立。</p>'}</div>${index < stages.length - 1 ? '<div class="v4-stage-arrow">↓</div>' : ''}</article>`;
    }).join('')}</div></section>`;
  }
  function FlowExplorer(project, flow, evaluation, values) {
    const versions = [...new Set(flow.nodes.filter(node => node.ruleId).map(node => node.ruleId))].map(id => E.byId(project.rules, id)?.versions?.[E.byId(project.rules, id)?.currentVersion]).filter(Boolean);
    const inputs = new Map(); versions.forEach(version => version.inputs.forEach(input => { if (!inputs.has(input.id)) inputs.set(input.id, input); }));
    return `<div class="v4-flow-explorer"><div>${[...inputs.values()].map(input => `<label>${esc(input.label)}<select data-flow-input="${input.id}">${input.options.map(option => `<option value="${option.value}" ${values[input.id] === option.value ? 'selected' : ''}>${esc(option.label)}</option>`).join('')}</select></label>`).join('')}</div><div class="v4-sim-result ok"><b>当前路径 ${evaluation.actions.length} 个动作</b><p>${evaluation.explanations.map(item => item.explanation).join(' ') || '此路径没有独立条件规则。'}</p></div></div>`;
  }
  function ScenarioStoryboard(project, item, context) { return DirectedFlow(project, item, context); }
  function CaseActivityView(project, scenario) {
    const model = scenario.caseModel;
    if (!model) return empty('没有案件活动模型', '此场景使用其他表达模式。');
    return `<section class="v4-section"><div class="v4-section-title"><div><h2>案件状态与可执行活动</h2><p>完成条件约束结果，不把调查过程强制画成固定流水线</p></div>${badge('动态案件', 'blue')}</div><div class="v4-case-grid"><div><h3>必须满足的完成条件</h3><ul class="v4-check-list">${(model.completionConditions || []).map(item => `<li>${esc(typeof item === 'object' ? item.name || item.description : item)}</li>`).join('')}</ul><h3>已具备证据</h3>${(model.evidenceIds || []).map(id => entityLink(project, id)).join('') || '<p class="muted">尚无证据</p>'}</div><div><h3>当前可执行活动</h3>${(model.availableActivityIds || []).map(id => {
      const action = K.getEntity(project, id); return `<button class="v4-activity" data-entity="${id}"><b>${esc(action?.name || id)}</b><span>${esc(action?.description || '根据案件事实决定先后')}</span></button>`;
    }).join('')}<h3>动态待办</h3><ul class="v4-check-list open">${(model.dynamicTasks || []).map(item => `<li>${esc(typeof item === 'object' ? `${item.name || item.id}${item.when ? ` · ${item.when}` : ''}` : item)}</li>`).join('')}</ul></div></div></section>`;
  }
  function StateLifecycle(project, item) {
    if (!item.states?.length) return empty('没有生命周期定义', '状态视图只在对象具有状态信息时出现。');
    const changes = E.relationIndex(project).sources(item.id, 'changes').map(id => E.byId(project.actions, id)).filter(Boolean);
    return `<section class="v4-section"><div class="v4-section-title"><h2>对象生命周期</h2><span>状态定义与导致变化的动作分离</span></div><div class="v4-lifecycle">${item.states.map((state, index) => `<div><span>${index + 1}</span><b>${esc(state)}</b></div>`).join('')}</div><h3>可观察变化</h3><div class="v4-change-list">${changes.map(action => (action.objectChanges || []).filter(change => change.objectId === item.id).map(change => `<button data-entity="${action.id}"><b>${esc(action.name)}</b><span>${esc(change.before)} → ${esc(change.after)}</span><small>${esc(change.constraint || '')}</small></button>`).join('')).join('') || '<p class="muted">尚无动作声明状态变化。</p>'}</div></section>`;
  }
  function ObjectChangeView(project, item) { return StateLifecycle(project, item); }
  function SystemMap(project, item) {
    const children = K.outgoing(project, item.id, 'contains').map(relation => K.getEntity(project, relation.to)).filter(Boolean);
    const dependencies = K.outgoing(project, item.id, 'dependsOn').map(relation => K.getEntity(project, relation.to)).filter(Boolean);
    return `<section class="v4-section"><div class="v4-section-title"><h2>系统组成与职责</h2><span>按需展开，不绘制全项目巨图</span></div><div class="v4-system-tree">${children.map(child => `<details open><summary><button data-entity="${child.id}">${esc(child.name)}</button><span class="v4-mono">${child.id}</span></summary><div><p>${esc(child.description)}</p>${K.outgoing(project, child.id, 'contains').map(relation => entityLink(project, relation.to)).join('')}</div></details>`).join('') || '<p class="muted">此对象没有下级组成关系。</p>'}</div>${dependencies.length ? `<h3>主要依赖</h3>${dependencies.map(child => entityLink(project, child.id)).join('')}` : ''}</section>`;
  }
  function DataMapping(project, item) {
    const fields = item.props?.fields || [];
    const reads = K.incoming(project, item.id, 'reads'), writes = K.incoming(project, item.id, 'writes');
    return `<section class="v4-section"><div class="v4-section-title"><h2>数据含义与读写责任</h2><span class="v4-mono">${esc(item.props?.tableName || item.id)}</span></div>${fields.length ? `<div class="v4-table-wrap"><table class="v4-table"><thead><tr><th>字段</th><th>类型</th><th>业务含义</th></tr></thead><tbody>${fields.map(field => `<tr><td class="v4-mono">${esc(field.name || field.id)}</td><td>${esc(field.type || '未标注')}</td><td>${esc(field.description || field.meaning || '')}</td></tr>`).join('')}</tbody></table></div>` : '<p class="muted">字段尚未结构化，原始数据字典仍可从来源进入。</p>'}<div class="v4-two-col"><div><h3>读取它的业务或接口</h3>${reads.map(relation => entityLink(project, relation.from)).join('') || '<p class="muted">未建立读取关系</p>'}</div><div><h3>写入它的业务或接口</h3>${writes.map(relation => entityLink(project, relation.from)).join('') || '<p class="muted">未建立写入关系</p>'}</div></div></section>`;
  }
  function ConfigurationInspector(project, item) {
    return `${PropertyTable(project, item)}<section class="v4-section"><div class="v4-section-title"><h2>配置影响追踪</h2><span>配置版本独立于软件发布版本</span></div>${RelationExplorer(project, item, { compact: true, types: ['affects', 'uses', 'references'] })}</section>`;
  }
  function ResourceViewer(project, item) {
    const p = item.props || {};
    return `<section class="v4-section v4-resource"><div class="v4-section-title"><h2>原始资料与精确定位</h2>${badge(p.parseStatus === 'structured' ? '已结构化' : p.parseStatus === 'partial' ? '部分提炼' : '未解析', 'amber')}</div><div class="v4-resource-path"><span>资源定位</span><code>${esc(p.uri || '未记录')}</code></div><dl class="v4-properties">${prop('原始版本', p.sourceVersion)}${prop('精确定位', p.locator)}${prop('维护责任', p.owner)}${prop('来源可信状态', p.trust)}</dl><div class="v4-notice amber">摘要只是阅读辅助，不替代原始证据。原型不读取真实文件或仓库。</div><p class="v4-longcopy">${esc(p.summary || '尚未整理摘要。')}</p></section>`;
  }
  function RelationExplorer(project, item, options = {}) {
    const graph = K.relationsFor(project, item.id, { depth: 1 });
    const grouped = {};
    graph.edges.forEach(relation => {
      if (options.types && !options.types.includes(relation.type)) return;
      const outgoing = relation.from === item.id, other = outgoing ? relation.to : relation.from;
      const key = `${outgoing ? 'out' : 'in'}:${relation.type}`;
      (grouped[key] ||= { type: relation.type, outgoing, relations: [] }).relations.push({ relation, other });
    });
    const labels = { contains: '组成', belongsTo: '归属', uses: '使用 / 被使用', supports: '证据支持', implements: '实现', references: '引用', dependsOn: '依赖', affects: '影响', reads: '读取', writes: '写入', changes: '造成变化', triggers: '触发', explains: '术语解释', cites: '依据', constrains: '约束', mapsTo: '映射', derivedFrom: '提炼自', produces: '产生', partOf: '属于' };
    const groups = Object.values(grouped);
    if (!groups.length) return empty('尚无局部关系', '平台不会为了填满页面复制其他对象的关系。');
    return `<div class="v4-relation-groups">${groups.map(group => `<div class="v4-relation-group"><h3>${group.outgoing ? '当前对象 → ' : '关联对象 → '}${esc(labels[group.type] || group.type)}</h3>${group.relations.slice(0, options.compact ? 8 : 30).map(({ relation, other }) => `<div class="v4-relation-row">${entityLink(project, other)}<span>${badge(relation.verified !== false && relation.trust !== 'inferred' ? '已核验' : '待核验', relation.verified !== false && relation.trust !== 'inferred' ? 'green' : 'amber')}${relation.explanation ? `<small>${esc(relation.explanation)}</small>` : ''}${relation.condition ? `<small>条件：${esc(JSON.stringify(relation.condition))}</small>` : ''}</span></div>`).join('')}</div>`).join('')}</div>`;
  }
  function EvidenceInspector(project, item) {
    const factIds = new Set(item.evidence || item.sourceIds || []);
    K.incoming(project, item.id, 'supports').forEach(relation => factIds.add(relation.from));
    K.outgoing(project, item.id, 'cites').forEach(relation => factIds.add(relation.to));
    K.outgoing(project, item.id, 'derivedFrom').forEach(relation => factIds.add(relation.to));
    const facts = [...factIds].map(id => K.getEntity(project, id)).filter(Boolean);
    return `<section class="v4-section"><div class="v4-section-title"><div><h2>来源与可信状态</h2><p>已批准依据、实现观察、历史资料与推断分开呈现</p></div>${badge(`${facts.length} 条来源`)}</div>${facts.length ? facts.map(fact => `<button class="v4-evidence" data-entity="${fact.id}"><div><b>${esc(fact.props?.resourceType || fact.kind || typeName(project, fact.typeId))}</b>${badge(fact.status || fact.props?.trust || '未确认', ['published', '已批准', '已核验', 'confirmed'].includes(fact.status || fact.props?.trust) ? 'green' : 'amber')}</div><p>${esc(fact.props?.uri || fact.source || fact.name)}</p><small>${esc(fact.props?.summary || fact.conclusion || fact.description)}</small></button>`).join('') : empty('尚无来源证据', '此结论不能仅凭摘要被视为已确认事实。')}</section>`;
  }
  function VersionComparison(project, item) {
    if (item.typeId === 'Rule') {
      return `<section class="v4-section"><div class="v4-section-title"><h2>规则修订历史</h2><span>历史定义不会被新草稿覆盖</span></div><div class="v4-version-list">${Object.values(item.versions || {}).map(version => `<button data-rule-version="${esc(version.version)}" data-id="${item.id}"><span class="v4-mono">${item.id}@${esc(version.version)}</span><b>${esc(version.summary || version.constraint || '未生成摘要')}</b><small>${esc(version.status)} · ${esc(version.source || '')}</small></button>`).join('')}</div></section>`;
    }
    const revisions = Object.values(item.revisions || {});
    return `<section class="v4-section"><h2>对象修订</h2><div class="v4-version-list"><button><span class="v4-mono">${item.id}@${esc(item.revision)}</span><b>${esc(item.description)}</b><small>${esc(item.status)}</small></button>${revisions.map(version => `<button data-entity-revision="${esc(version.revision)}" data-id="${item.id}"><span class="v4-mono">${item.id}@${esc(version.revision)}</span><b>${esc(version.description)}</b><small>不可变历史快照</small></button>`).join('')}</div></section>`;
  }
  function DomainMap(project) {
    return `<div class="v4-domain-map">${(project.domains || []).map(domain => `<button data-entity="${domain.id}"><span>${esc(domain.icon || domain.name[0])}</span><div><b>${esc(domain.name)}</b><p>${esc(domain.desc || domain.goal)}</p><small>${(domain.caps || []).length} 项能力 · ${K.incoming(project, domain.id, 'belongsTo').length} 项直接归属</small></div></button>`).join('')}</div>`;
  }
  function CapabilityExplorer(project, domainId) {
    const domain = E.byId(project.domains, domainId);
    return (domain?.caps || []).map(id => entityLink(project, id)).join('') || '<p class="muted">尚未建立能力</p>';
  }
  function WorkflowProgress(definition, instance) {
    const def = instance?.definitionSnapshot || definition;
    if (!def) return empty('尚未选择工作流', '定义与实例分开管理。');
    const current = instance?.currentStageId, completed = new Set((instance?.completed || []).map(item => item.stageId));
    return `<div class="v4-workflow-progress">${def.stages.map((stage, index) => {
      const parallel = stage.parallelGroup && def.stages.some(other => other.id !== stage.id && other.parallelGroup === stage.parallelGroup);
      return `<div class="v4-workflow-stage ${completed.has(stage.id) ? 'done' : current === stage.id ? 'current' : ''}"><span>${completed.has(stage.id) ? '✓' : index + 1}</span><div><b>${esc(stage.name)}</b><small>${esc(stage.role)} · ${stage.required === false ? '可选' : '必需'}${parallel ? ' · 可并行会签' : ''}</small>${stage.returnTo ? `<small>退回至 ${esc(def.stages.find(item => item.id === stage.returnTo)?.name || stage.returnTo)}</small>` : ''}</div></div>`;
    }).join('')}</div>`;
  }
  function ReviewPanel(project, instance) {
    if (!instance) return '';
    const stage = K.currentStage(instance);
    return `<section class="v4-section"><div class="v4-section-title"><h2>执行记录与验证</h2><span>${instance.records.length} 条记录</span></div><div class="v4-records">${instance.records.slice().reverse().map(record => `<div><span class="v4-mono">${esc(record.at)}</span><b>${esc(record.type)} · ${esc(record.stageId || record.fromStageId || '')}</b><p>${esc(record.note || record.by || '')}</p></div>`).join('') || '<p class="muted">尚未产生执行记录。</p>'}</div>${stage ? `<div class="v4-notice blue">当前活动：${esc(stage.name)}。进入与完成条件由 ${esc(instance.definitionId)}@${esc(instance.definitionVersion)} 决定。</div>` : ''}</section>`;
  }

  function availableViews(project, item) {
    if (!item) return [];
    const views = [{ id: 'summary', name: '对象摘要' }];
    if (item.typeId === 'Rule') views.push({ id: 'decision', name: '规则与试算' });
    if (item.typeId === 'Scenario') {
      if (E.flowFor(project, item.id).nodes.length || item.stageIds?.length) views.push({ id: 'flow', name: '业务流程' });
      if (item.caseModel) views.push({ id: 'case', name: '案件活动' });
    }
    if (item.states?.length) views.push({ id: 'state', name: '状态生命周期' });
    if (['System', 'Subsystem', 'Module'].includes(item.typeId)) views.push({ id: 'system', name: '系统组成' });
    if (item.typeId === 'DataEntity') views.push({ id: 'data', name: '字段与数据映射' });
    if (item.typeId === 'Configuration') views.push({ id: 'config', name: '配置检查器' });
    if (item.typeId === 'Resource') views.push({ id: 'resource', name: '原始资料' });
    views.push({ id: 'relations', name: '关联探索' }, { id: 'evidence', name: '来源与状态' }, { id: 'versions', name: '版本历史' });
    return views;
  }
  const Registry = {
    KnowledgeSummary, EntityDetail: KnowledgeSummary, RelationExplorer, DomainMap, CapabilityExplorer, ScenarioStoryboard,
    DirectedFlow, CaseActivityView, StateLifecycle, DecisionTable, ObjectChangeView, SystemMap, DataMapping,
    ConfigurationInspector, ResourceViewer, VersionComparison, WorkflowProgress, ReviewPanel, EvidenceInspector,
    PropertyTable, RuleSimulation, availableViews, typeName, entityLink, badge, empty, esc
  };
  return Registry;
});
