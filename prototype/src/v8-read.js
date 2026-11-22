/* Business Atlas V8 — explicit Knowledge Read Context, Catalog Resolver and contextual coverage. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const F = commonjs ? require('./v7-framework.js') : root.AtlasV7Framework;
  const api = factory(C, G, F);
  if (commonjs) module.exports = api;
  root.AtlasV8Read = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (C, G, F) {
  'use strict';
  const clone = C.clone, cache = new Map();
  const GOVERNANCE_SOURCES = {
    'governance.changes': ['change_set'], 'governance.reviews': ['review_decision'], 'governance.baselines': ['baseline'],
    'governance.tasks': ['task'], 'governance.workflows': ['workflow_definition', 'workflow_instance'],
    'governance.deliveries': ['contract', 'delivery'], 'governance.acceptance': ['acceptance', 'entity'],
    'governance.issues': ['issue', 'entity'], 'governance.health': ['issue', 'impact_review', 'conflict_record'],
    'governance.feedback': ['feedback'], 'governance.agent-runs': ['agent_run'], 'governance.imports': ['import_proposal'],
    'governance.visual-views': ['visual_view'], 'governance.questions': ['question_answer']
  };
  const CATEGORY_POLICIES = {
    'project.introduction': { completeness: ['项目定位', '业务背景', '服务对象'], inspectorProfile: 'project-profile' },
    'project.goals': { completeness: ['目标与价值', '可验证结果'], inspectorProfile: 'goal' },
    'project.scope': { completeness: ['负责范围', '不负责范围', '项目约束'], inspectorProfile: 'project-profile' },
    'project.stakeholders': { completeness: ['参与方', '主要关注'], inspectorProfile: 'actor' },
    'project.roles': { completeness: ['角色职责', '责任边界'], inspectorProfile: 'actor' },
    'project.business-overview': { completeness: ['核心业务领域', '主要业务场景'], inspectorProfile: 'business-overview' },
    'project.system-overview': { completeness: ['主要系统', '系统边界', '外部依赖'], inspectorProfile: 'system-overview' },
    'project.constraints': { completeness: ['关键约定', '决策来源', '当前影响'], inspectorProfile: 'decision' },
    'business.domains': { completeness: ['领域职责', '业务目标'], inspectorProfile: 'domain' },
    'business.capabilities': { completeness: ['能力定义', '业务贡献'], inspectorProfile: 'capability' },
    'business.features': { completeness: ['业务目的', '场景', '动作', '规则', '实现', '验收', '来源'], inspectorProfile: 'feature' },
    'business.scenarios': { completeness: ['业务目标', '触发条件', '显式行为关系'], inspectorProfile: 'scenario' },
    'business.actions': { completeness: ['责任角色', '输入', '输出', '前置条件'], inspectorProfile: 'action' },
    'business.actors': { completeness: ['角色定义', '主要关注'], inspectorProfile: 'actor' },
    'business.objects': { completeness: ['业务含义', '主要属性', '状态（适用时）'], inspectorProfile: 'object' },
    'business.rules': { completeness: ['业务含义', '适用范围', '约束定义', '来源'], inspectorProfile: 'rule' },
    'business.decisions': { completeness: ['输入条件', '判断行', '结果', '优先级', '来源'], inspectorProfile: 'decision-rule' },
    'business.events': { completeness: ['事件类型', '业务载荷', '产生条件'], inspectorProfile: 'event' },
    'business.lifecycles': { completeness: ['合法状态', '转移事件', '守卫', '动作'], inspectorProfile: 'lifecycle' },
    'business.exceptions': { completeness: ['异常条件', '处理方式', '恢复或终止'], inspectorProfile: 'exception' },
    'system.systems': { completeness: ['系统职责', '系统组成'], inspectorProfile: 'system' },
    'system.modules': { completeness: ['模块职责', '业务实现'], inspectorProfile: 'module' },
    'system.surfaces': { completeness: ['页面用途', '操作入口'], inspectorProfile: 'surface' },
    'system.operations': { completeness: ['操作名称', '触发动作', '前置状态'], inspectorProfile: 'operation' },
    'system.apis': { completeness: ['方法与路径', '业务映射', '失败处理'], inspectorProfile: 'api' },
    'system.data': { completeness: ['业务含义', '核心字段', '数据归属'], inspectorProfile: 'data' },
    'system.data-flow': { completeness: ['读取方', '写入方', '一致性约束'], inspectorProfile: 'data-flow' },
    'system.config': { completeness: ['配置含义', '数据类型', '默认值', '生效范围', '适用环境', '影响对象', '来源'], inspectorProfile: 'configuration' },
    'system.flags': { completeness: ['开关含义', '默认状态', '生效范围', '影响功能'], inspectorProfile: 'configuration' },
    'system.environments': { completeness: ['环境', '软件版本', '配置版本', '部署状态'], inspectorProfile: 'environment' },
    'system.integrations': { completeness: ['外部系统', '协议', '维护责任', '失败处理'], inspectorProfile: 'integration' },
    'system.constraints': { completeness: ['技术约束', '事务与依赖', '风险来源'], inspectorProfile: 'constraint' },
    'assets.terms': { completeness: ['标准定义', '适用上下文', '来源'], inspectorProfile: 'term' },
    'assets.names': { completeness: ['标准名称', '别名或历史名称', '易混淆概念'], inspectorProfile: 'term' },
    'assets.requirements': { completeness: ['原始需求定位', '原始版本', '业务意图', '来源状态'], inspectorProfile: 'resource' },
    'assets.specifications': { completeness: ['规范版本', '精确定位', '批准状态'], inspectorProfile: 'resource' },
    'assets.technical': { completeness: ['技术版本', '精确定位', '实现范围'], inspectorProfile: 'resource' },
    'assets.manuals': { completeness: ['操作对象', '操作步骤', '适用版本'], inspectorProfile: 'resource' },
    'assets.regulations': { completeness: ['法规名称', '条款定位', '有效状态'], inspectorProfile: 'resource' },
    'assets.conventions': { completeness: ['约定内容', '适用范围', '确认责任'], inspectorProfile: 'convention' },
    'assets.decisions': { completeness: ['历史意图', '决策时间', '当前有效性'], inspectorProfile: 'decision' },
    'assets.evidence': { completeness: ['来源类型', '精确定位', '来源版本', '可信状态'], inspectorProfile: 'resource' },
    'governance.requirements': { completeness: ['需求定义', '验收条件', '批准状态'], inspectorProfile: 'requirement' },
    'governance.issues': { completeness: ['问题描述', '影响', '确认责任', '处理状态'], inspectorProfile: 'issue' },
    'governance.changes': { completeness: ['修改原因', '变更内容', '验证结果', '审核状态'], inspectorProfile: 'change-set' },
    'governance.reviews': { completeness: ['审核人', '审核决定', '审核时间', '变更关联'], inspectorProfile: 'review' },
    'governance.baselines': { completeness: ['对象修订', '断言修订', '流程修订', '批准记录'], inspectorProfile: 'baseline' },
    'governance.tasks': { completeness: ['任务根对象', '当前契约', '问题状态', '验收状态'], inspectorProfile: 'task' },
    'governance.workflows': { completeness: ['定义版本', '活动与角色', '完成条件', '实例状态'], inspectorProfile: 'workflow' },
    'governance.deliveries': { completeness: ['交付任务', '版本绑定', '必要依赖', '反馈状态'], inspectorProfile: 'contract' },
    'governance.acceptance': { completeness: ['验收对象', '验收结果', '证据', '契约版本'], inspectorProfile: 'acceptance' },
    'governance.health': { completeness: ['冲突或过期原因', '影响范围', '复核状态'], inspectorProfile: 'health' },
    'governance.feedback': { completeness: ['反馈类型', '来源上下文', '关联知识', '处理工作', '处理结果'], inspectorProfile: 'feedback' },
    'governance.agent-runs': { completeness: ['任务要求', '工具调用', '来源', '观察与推断', '知识提案', '人工复核'], inspectorProfile: 'agent-run' },
    'governance.imports': { completeness: ['格式校验', '来源预览', '变更预览', '人工决定', '草稿关联'], inspectorProfile: 'import-proposal' },
    'governance.visual-views': { completeness: ['根知识', '读取上下文', '语义对象与断言', '布局修订', '资料核验'], inspectorProfile: 'visual-view' },
    'governance.questions': { completeness: ['问题意图', '读取上下文', '知识解释', '来源引用', '待确认事项'], inspectorProfile: 'question-answer' },
    'system.architecture': { completeness: ['组件职责', '系统边界', '业务实现', '技术依赖', '来源'], inspectorProfile: 'architecture' },
    'system.technology': { completeness: ['技术版本', '运行环境', '依赖兼容', '使用组件'], inspectorProfile: 'technology' },
    'system.deployment': { completeness: ['部署单元', '承载组件', '环境差异', '运行版本'], inspectorProfile: 'deployment' },
    'system.decisions': { completeness: ['决策内容', '选型依据', '当前状态', '约束组件', '来源'], inspectorProfile: 'architecture-decision' },
    'system.build': { completeness: ['构建入口', '构建产物', '重建验证', '运行时依赖'], inspectorProfile: 'build-pipeline' },
    'system.visual-assets': { completeness: ['资料类型', '资料版本', '来源', '操作热点或说明', '核验状态'], inspectorProfile: 'visual-asset' }
  };
  function context(p, input = {}) {
    const versionMode = input.versionMode || 'published', baselineId = input.baselineId || p.versionLayers.baseline;
    return Object.freeze({ projectId: p.meta.id, baselineId, versionMode, draftWorkspaceId: input.draftWorkspaceId || (versionMode === 'draft' ? p.draftWorkspace?.id || null : null), permissionContext: input.permissionContext || { role: 'reader', clearance: 'restricted' }, applicableContext: Object.freeze(clone(input.applicableContext || {})), applicabilityMode: input.applicabilityMode || 'explore', readPurpose: input.readPurpose || 'understand', timeMode: input.timeMode || (versionMode === 'historical' ? 'as-approved' : 'current-activity'), asOf: input.asOf || input.applicableContext?.asOf || null });
  }
  function optionsFor(ctx, draftAllowed) { return ctx.versionMode === 'draft' && draftAllowed ? { draft: true, permission: ctx.permissionContext, applicableContext: ctx.applicableContext, applicabilityMode: ctx.applicabilityMode } : { baselineId: ctx.baselineId, permission: ctx.permissionContext, applicableContext: ctx.applicableContext, applicabilityMode: ctx.applicabilityMode }; }
  function getKnowledge(p, ctx, id) { return C.getEntity(p, id, optionsFor(ctx, true)); }
  function listKnowledge(p, ctx, filter = {}) { return C.allEntities(p, { ...optionsFor(ctx, true), ...filter }); }
  function getRelations(p, ctx, id) { return C.getAssertions(p, id, optionsFor(ctx, true)); }
  function getFlow(p, ctx, id) { return C.getFlow(p, id, optionsFor(ctx, true)); }
  function getSources(p, ctx, id) {
    const entity = getKnowledge(p, ctx, id); if (!entity) return [];
    const refs = [...(entity.sourceIds || []).map(resourceId => ({ resourceId, locator: '对象级来源' })), ...getRelations(p, ctx, id).flatMap(a => a.sourceRefs || [])];
    return refs.map(ref => ({ ...ref, entity: ref.kind === 'human_confirmation' ? null : getKnowledge(p, ctx, ref.resourceId) })).filter(ref => ref.kind === 'human_confirmation' || ref.entity);
  }
  function getTrust(p, ctx, id) {
    const entity = getKnowledge(p, ctx, id); if (!entity) return 'missing';
    return F.trustState(p, entity, { relations: getRelations(p, ctx, id), impactReviews: (p.impactReviews || []).filter(r => !r.baselineId || r.baselineId === ctx.baselineId) });
  }
  function getCoverage(p, ctx, id) {
    const entity = getKnowledge(p, ctx, id); if (!entity) return null;
    return F.coverage(p, entity, { relations: getRelations(p, ctx, id), getEntity: otherId => getKnowledge(p, ctx, otherId), getRelations: otherId => getRelations(p, ctx, otherId), readContext: ctx });
  }
  function getImpact(p, ctx, id) { return (p.impactReviews || []).filter(r => r.entityId === id); }
  function getContractContext(p, ctx, taskId) {
    const task = (p.tasks || []).find(t => t.id === taskId); if (!task) return null;
    if (!G.permissionAllows(getKnowledge(p, ctx, task.rootId), ctx.permissionContext)) return { ok: false, code: 'FORBIDDEN' };
    const pack = (p.contextPackages || []).find(c => c.packageId === task.activeContractId);
    if (pack && pack.versionBinding.baselineId !== ctx.baselineId && ctx.versionMode !== 'draft') return { ok: false, code: 'VERSION_UNAVAILABLE', task, package: null };
    if (pack) { const authorized = safeGovernanceRaw(p, ctx, 'contract', pack); if (!authorized) return { ok: false, code: 'FORBIDDEN' }; return { ok: true, task, package: authorized, baselineId: ctx.baselineId }; }
    return { ok: true, task, package: null, baselineId: ctx.baselineId };
  }
  function policyFor(category) {
    const sources = GOVERNANCE_SOURCES[category.categoryId] || ['entity'], policy = CATEGORY_POLICIES[category.categoryId] || { completeness: [], inspectorProfile: 'generic' };
    return { categoryId: category.categoryId, recordKind: sources.length === 1 && sources[0] === 'entity' ? 'Knowledge Entity' : 'Catalog Record', dataSource: sources, supportedActions: category.suggestedCreationActions, readPolicy: 'Knowledge Read Context', statusPolicy: 'governance+verification+coverage', completenessPolicy: policy.completeness, inspectorProfile: policy.inspectorProfile };
  }
  function entityRecord(p, ctx, entity) {
    const categoryIds = assignedCategoryIds(p, entity).filter(id => !GOVERNANCE_SOURCES[id]);
    const assignments = (entity.categoryAssignments || []).filter(a => a.assignmentStatus === 'confirmed').map(a => a.categoryId);
    return { stableId: entity.id, recordKind: 'entity', categoryIds: [...new Set([...categoryIds, ...assignments])], projectId: p.meta.id, title: entity.name, summary: entity.description || entity.goal || entity.purpose || '', status: entity.status, versionRef: entity.revision, updatedAt: entity.updatedAt || entity.publishedAt || '', openAction: { route: 'studio', id: entity.id }, supportedActions: ['open', 'edit', 'link'], entity };
  }
  function assignedCategoryIds(p, entity) {
    const base = [...new Set([...F.categoriesForEntity(p, entity), ...F.allCategories(p).filter(cat => cat.applicableTypes.includes(entity.typeId) && !cat.match).map(cat => cat.categoryId)])];
    // V7 string matchers are only compatibility candidates. Explicit assignments are authoritative for special categories.
    const special = new Set(['assets.requirements', 'assets.specifications', 'assets.technical', 'assets.manuals', 'assets.regulations', 'system.flags']);
    const assignments = (entity.categoryAssignments || []).filter(a => a.assignmentStatus === 'confirmed').map(a => a.categoryId);
    return [...new Set([...base.filter(id => !special.has(id)), ...assignments])];
  }
  function safeGovernanceRaw(p, ctx, kind, raw) {
    if (ctx.permissionContext?.role === 'external' || ctx.permissionContext?.clearance === 'public') return null;
    const visible = id => !id || !!getKnowledge(p, ctx, id);
    if (kind === 'task' && !visible(raw.rootId)) return null;
    if (kind === 'issue' && !visible(raw.objectId)) return null;
    if (kind === 'contract') {
      const baselineId = raw.versionBinding?.baselineId, contractCtx = { ...ctx, baselineId, versionMode: 'historical' };
      if (raw.rootId && !getKnowledge(p, contractCtx, raw.rootId)) return null;
      const allowed = item => !item.id || getKnowledge(p, contractCtx, item.id);
      const view = { ...clone(raw), businessContext: (raw.businessContext || []).filter(allowed), approvedRequirements: (raw.approvedRequirements || []).filter(allowed), constraints: (raw.constraints || []).filter(allowed), implementationContext: (raw.implementationContext || []).filter(allowed), evidence: (raw.evidence || []).filter(allowed) };
      delete view.behavior; delete view.includedFacts; delete view.includedAssertions; delete view.inclusionReasons; delete view.excluded;
      return view;
    }
    if (kind === 'baseline') {
      const baselineCtx = { ...ctx, baselineId: raw.id, versionMode: 'historical' }, entries = Object.fromEntries(Object.keys(raw.entries || {}).filter(id => getKnowledge(p, baselineCtx, id)).map(id => [id, raw.entries[id]]));
      return { ...clone(raw), entries, assertionRevisions: {} };
    }
    if (kind === 'feedback') {
      const ids = (raw.relatedKnowledgeIds || []).filter(visible); if (raw.relatedKnowledgeIds?.length && !ids.length) return null;
      return { ...clone(raw), relatedKnowledgeIds: ids };
    }
    if (kind === 'agent_run') {
      const related = [...(raw.sourceRefs || []).map(ref => ref.resourceId), ...(raw.proposedKnowledge || []).flatMap(item => [item.subject, item.object, item.entityId])].filter(Boolean);
      if (related.some(id => !visible(id))) return null;
      return clone(raw);
    }
    if (kind === 'import_proposal') return { proposalId: raw.proposalId, projectId: raw.projectId, packageId: raw.packageId, contentHash: raw.contentHash, status: raw.status, validation: { errors: [], warnings: raw.validation?.warnings?.filter(w => /敏感/.test(w)) || [] }, preview: { summary: clone(raw.preview?.summary || {}) }, draftWorkspaceId: raw.draftWorkspaceId, createdAt: raw.createdAt, updatedAt: raw.updatedAt };
    if (kind === 'visual_view') {
      if (!visible(raw.rootKnowledgeId)) return null;
      return { ...clone(raw), includedKnowledgeIds: (raw.includedKnowledgeIds || []).filter(visible), includedAssertionIds: [] };
    }
    if (kind === 'question_answer') {
      const citations = (raw.citations || []).filter(ref => visible(ref.knowledgeId) && (!ref.sourceId || visible(ref.sourceId)));
      if (raw.focusIds?.some(id => !visible(id))) return null;
      return { ...clone(raw), citations, compared: [] };
    }
    return clone(raw);
  }
  function recordsFor(p, ctx, categoryId) {
    const key = JSON.stringify([ctx.projectId, ctx.baselineId, ctx.versionMode, ctx.draftWorkspaceId, ctx.permissionContext, ctx.applicableContext, ctx.applicabilityMode, ctx.readPurpose, ctx.timeMode, categoryId, p.draftWorkspace?.updatedAt, p._readRevision || 0]);
    if (cache.has(key)) return cache.get(key);
    const records = [];
    if (GOVERNANCE_SOURCES[categoryId]?.includes('entity') || !GOVERNANCE_SOURCES[categoryId]) {
      listKnowledge(p, ctx, { includeRetired: true }).filter(e => assignedCategoryIds(p, e).includes(categoryId)).forEach(e => records.push({ ...entityRecord(p, ctx, e), categoryIds: [categoryId] }));
    }
    const add = (kind, id, title, summary, status, versionRef, openAction, raw) => { const safeRaw = safeGovernanceRaw(p, ctx, kind, raw); if (!safeRaw) return; records.push({ stableId: id, recordKind: kind, categoryIds: [categoryId], projectId: p.meta.id, title, summary, status, versionRef, updatedAt: raw.updatedAt || raw.createdAt || raw.publishedAt || '', openAction, supportedActions: ['open'], raw: safeRaw }); };
    (GOVERNANCE_SOURCES[categoryId] || []).forEach(source => {
      if (source === 'baseline') (p.baselines || []).forEach(b => add('baseline', b.id, b.id, `${Object.keys(b.entries).length} 个对象 · ${Object.keys(b.assertionRevisions).length} 条断言 · ${Object.keys(b.flowRevisions || {}).length} 份流程`, b.approvalStatus, b.id, { route: 'baselines', id: b.id }, b));
      if (source === 'workflow_definition') (p.workflowDefinitions || []).forEach(d => add('workflow_definition', `${d.id}@${d.version}`, d.name, `${d.stages.length} 个活动 · ${d.purpose}`, d.status, d.version, { route: 'workflow-definition', id: d.id, version: d.version }, d));
      if (source === 'workflow_instance') (p.workflowInstances || []).forEach(i => add('workflow_instance', i.id, i.definitionSnapshot.name, `${i.subjectId || '未绑定对象'} · ${i.status}`, i.status, i.definitionVersion, { route: 'workflow-instance', id: i.id }, i));
      if (source === 'task') (p.tasks || []).forEach(t => add('task', t.id, t.title, `${t.rootId} · ${t.status}`, t.status, t.activeContractId || '尚未生成契约', { route: 'tasks', id: t.id }, t));
      if (source === 'issue') (p.issues || []).forEach(i => add('issue', i.id, i.title, `${i.objectId} · ${i.status}`, i.status, i.changeSetId || '尚未关联变更', { route: 'issues', id: i.id }, i));
      if (source === 'change_set') (p.changeSets || []).forEach(cs => add('change_set', cs.id, cs.title, `${cs.operations.length} 个操作 · ${cs.status}`, cs.status, cs.baseBaselineId, { route: 'changes', id: cs.id }, cs));
      if (source === 'review_decision') (p.changeSets || []).flatMap(cs => (cs.reviewDecisions || []).map((d, index) => add('review_decision', `RD-${cs.id}-${index}`, `${d.by} · ${d.decision}`, `${cs.title} · ${d.comment || ''}`, d.decision, d.at, { route: 'changes', id: cs.id }, d)));
      if (source === 'contract') (p.contextPackages || []).forEach(c => add('contract', c.packageId, c.taskIntent, `${c.versionBinding.baselineId} · ${c.valid === false ? '需要复核' : '当前有效'}`, c.valid === false ? 'stale' : 'current', c.versionBinding.baselineId, { route: 'contracts', id: c.packageId, taskId: c.taskId }, c));
      if (source === 'delivery') (p.deliveries || []).forEach(d => add('delivery', d.id, d.id, d.packageVersion || '草稿', d.packageVersion ? 'published' : 'draft', d.packageVersion || d.id, { route: 'deliveries', id: d.id }, d));
      if (source === 'acceptance') (p.tasks || []).flatMap(t => (t.acceptance || []).map((a, index) => add('acceptance', `${t.id}-ACC-${index + 1}`, `${t.title} · ${a.result}`, `${a.by} · ${a.evidence}`, a.result, a.packageId, { route: 'tasks', id: t.id }, a)));
      if (source === 'impact_review') (p.impactReviews || []).forEach(r => add('impact_review', r.id, `${r.entityName || r.entityId} 需要重新核验`, r.reason, r.status, r.changeId, { route: 'changes', id: r.changeId }, r));
      if (source === 'conflict_record') (p.conflictRecords || []).forEach(r => add('conflict_record', r.id, r.title, r.resolution, r.status, r.id, { route: 'health', id: r.id }, r));
      if (source === 'feedback') (p.feedbackRecords || []).forEach(f => add('feedback', f.feedbackId, f.title, `${f.category} · ${f.status}`, f.status, f.resolvedBaselineId || f.originContext?.baselineId || '未绑定', { route: 'feedback', id: f.feedbackId }, f));
      if (source === 'agent_run') (p.agentRuns || []).forEach(run => add('agent_run', run.runId, run.userInstructions || run.taskId, `${run.executionEvents.length} 个事件 · ${run.toolCalls.length} 次工具调用 · ${run.reviewStatus}`, run.reviewStatus, run.endTime || run.startTime, { route: 'agents', id: run.runId }, run));
      if (source === 'import_proposal') (p.importProposals || []).forEach(proposal => add('import_proposal', proposal.proposalId, proposal.packageId, `${proposal.preview?.summary?.create || 0} 新增 · ${proposal.preview?.summary?.unknowns || 0} 未知 · ${proposal.status}`, proposal.status, proposal.contentHash, { route: 'imports', id: proposal.proposalId }, proposal));
      if (source === 'visual_view') (p.visualViews || []).forEach(view => add('visual_view', view.viewId, view.title || view.viewType, `${view.rootKnowledgeId} · ${view.verificationStatus}`, view.verificationStatus, view.revision, { route: 'visuals', id: view.viewId }, view));
      if (source === 'question_answer') (p.questionAnswers || []).forEach(answer => add('question_answer', answer.answerId, answer.query, `${answer.intent} · ${answer.conclusions.length} 条解释 · ${answer.citations.length} 项依据`, answer.status, answer.readContext?.baselineId, { route: 'ask', id: answer.answerId }, answer));
    });
    cache.set(key, records); return records;
  }
  function resolveCatalog(p, ctx, input = {}) {
    const category = F.categoryById(p, input.categoryId); if (!category) return null;
    const query = String(input.query || '').toLowerCase(), status = input.status || 'all';
    const all = recordsFor(p, ctx, category.categoryId), filtered = all.filter(r => (status === 'all' || r.status === status) && `${r.stableId}${r.title}${r.summary}`.toLowerCase().includes(query));
    const sort = input.sort || 'title';
    filtered.sort((a, b) => sort === 'id' ? a.stableId.localeCompare(b.stableId) : sort === 'status' ? a.status.localeCompare(b.status) : a.title.localeCompare(b.title, 'zh-CN') || a.stableId.localeCompare(b.stableId));
    const pageSize = Math.min(100, input.pageSize || 20), pages = Math.max(1, Math.ceil(filtered.length / pageSize)), page = Math.min(pages, Math.max(1, input.page || 1));
    return { category, policy: policyFor(category), items: filtered.slice((page - 1) * pageSize, page * pageSize).map(clone), total: filtered.length, page, pages, pageSize, counts: { total: all.length, draft: all.filter(r => r.status === 'draft').length, unverified: all.filter(r => ['unverified', 'unknown'].includes(r.status)).length, retired: all.filter(r => r.status === 'retired').length } };
  }
  function check(id, label, status, reason, action) { return { id, label, status, reason, action: action || null }; }
  function recordCoverage(p, ctx, record) {
    if (!record) return null;
    if (record.recordKind === 'entity') return getCoverage(p, ctx, record.stableId);
    const raw = record.raw, checks = [];
    if (record.recordKind === 'baseline') {
      checks.push(check('entries', '对象修订', Object.keys(raw.entries).length ? 'met' : 'unknown', `${Object.keys(raw.entries).length} 个对象修订`), check('assertions', '断言修订', Object.keys(raw.assertionRevisions).length ? 'met' : 'unknown', `${Object.keys(raw.assertionRevisions).length} 条断言修订`), check('flows', '流程修订', Object.keys(raw.flowRevisions || {}).length ? 'met' : 'unknown', `${Object.keys(raw.flowRevisions || {}).length} 份流程修订`), check('approval', '批准记录', raw.confirmations?.length ? 'met' : 'missing', raw.confirmations?.length ? `${raw.confirmations.length} 条确认记录` : '缺少确认记录'));
    } else if (record.recordKind === 'workflow_definition') {
      checks.push(check('definition', '定义与活动', raw.stages?.length ? 'met' : 'missing', raw.stages?.length ? `${raw.stages.length} 个活动` : '没有活动'), check('roles', '责任角色', raw.stages?.every(s => s.role) ? 'met' : 'missing', raw.stages?.every(s => s.role) ? '活动均有责任角色' : '存在未分配角色'), check('completion', '完成条件', raw.stages?.every(s => s.completionWhen || s.artifacts || s.capabilities?.includes('complete')) ? 'met' : 'unknown', '按活动定义检查完成条件'));
    } else if (record.recordKind === 'workflow_instance') {
      checks.push(check('definition', '固定定义版本', raw.definitionSnapshot ? 'met' : 'missing', raw.definitionSnapshot ? `${raw.definitionId}@${raw.definitionVersion}` : '缺少定义快照'), check('records', '执行记录', raw.records?.length ? 'met' : 'unknown', raw.records?.length ? `${raw.records.length} 条执行记录` : '尚无执行记录'), check('state', '实例状态', raw.status ? 'met' : 'unknown', raw.status || '未知'));
    } else if (record.recordKind === 'task') {
      checks.push(check('root', '任务根对象', getKnowledge(p, ctx, raw.rootId) ? 'met' : 'missing', getKnowledge(p, ctx, raw.rootId) ? raw.rootId : '当前版本不可读'), check('contract', '当前契约', raw.activeContractId ? 'met' : 'missing', raw.activeContractId || '尚未生成正式契约'), check('issues', '问题状态', raw.issueIds?.some(id => (p.issues || []).find(i => i.id === id && !['resolved', 'cancelled'].includes(i.status))) ? 'unverified' : 'met', raw.issueIds?.length ? `${raw.issueIds.length} 个关联问题` : '无未决问题'), check('acceptance', '验收状态', raw.acceptance?.length ? 'met' : 'unknown', raw.acceptance?.length ? `${raw.acceptance.length} 条验收记录` : '尚未验收'));
    } else if (record.recordKind === 'issue') {
      checks.push(check('object', '关联知识', getKnowledge(p, ctx, raw.objectId) ? 'met' : 'missing', raw.objectId), check('impact', '影响与责任', raw.impact && raw.assignee ? 'met' : 'missing', raw.impact && raw.assignee ? `${raw.impact} · ${raw.assignee}` : '缺少影响或确认责任'), check('decision', '处理决定', raw.decision ? 'met' : 'unknown', raw.decision || '尚未形成处理决定'));
    } else if (record.recordKind === 'change_set') {
      checks.push(check('reason', '修改原因', raw.reason ? 'met' : 'missing', raw.reason || '未填写原因'), check('operations', '变更内容', raw.operations?.length ? 'met' : 'missing', raw.operations?.length ? `${raw.operations.length} 个操作` : '没有操作'), check('validation', '验证结果', raw.validation?.ok ? 'met' : raw.validation ? 'missing' : 'unknown', raw.validation ? (raw.validation.ok ? '检查通过' : raw.validation.errors.join('；')) : '尚未执行检查'), check('review', '审核状态', raw.reviewDecisions?.length ? 'met' : 'unknown', raw.reviewDecisions?.length ? `${raw.reviewDecisions.length} 条决定` : raw.status));
    } else if (record.recordKind === 'contract') {
      checks.push(check('binding', '版本绑定', raw.versionBinding?.baselineId ? 'met' : 'missing', raw.versionBinding?.baselineId || '缺少基线'), check('completeness', '必要依赖', raw.completeness?.requiredClosure ? 'met' : 'unknown', raw.completeness?.requiredClosure ? '必要依赖闭包已检查' : '未记录完整性检查'), check('review', '当前有效性', raw.valid === false ? 'unverified' : 'met', raw.valid === false ? raw.staleReason || '需要复核' : '当前有效'));
    } else if (record.recordKind === 'acceptance') {
      checks.push(check('result', '验收结果', raw.result ? 'met' : 'unknown', raw.result || '未记录'), check('evidence', '验收证据', raw.evidence ? 'met' : 'missing', raw.evidence || '缺少证据'), check('binding', '契约版本', raw.packageId ? 'met' : 'missing', raw.packageId || '未绑定契约'));
    } else {
      const criteria = CATEGORY_POLICIES[record.categoryIds[0]]?.completeness || [];
      checks.push(...(criteria.length ? criteria.map(label => check(label.toLowerCase().replace(/\s/g, '-'), label, 'unknown', '此治理记录需要结合原始内容人工复核')) : [check('unconfigured', '评估标准', 'unknown', '尚未配置评估标准')]));
    }
    const structural = { met: checks.filter(c => c.status === 'met').length, missing: checks.filter(c => c.status === 'missing').length, unknown: checks.filter(c => c.status === 'unknown').length, unverified: checks.filter(c => c.status === 'unverified').length, notApplicable: checks.filter(c => c.status === 'not_applicable').length };
    return { entityId: record.stableId, state: record.status, trust: record.status === 'published' ? 'unknown' : record.status, structural, checks, note: '分类记录覆盖按真实数据来源计算；发布状态不等于事实核验。' };
  }
  function categoryCoverage(p, ctx, categoryId) {
    const category = F.categoryById(p, categoryId), records = recordsFor(p, ctx, categoryId), criteria = CATEGORY_POLICIES[categoryId]?.completeness || [];
    const coverageRows = records.map(record => ({ record, coverage: recordCoverage(p, ctx, record) }));
    const criteriaResults = criteria.map(label => {
      const checks = coverageRows.flatMap(row => (row.coverage?.checks || []).filter(check => check.label === label || check.id === label || label.includes(check.label) || check.label.includes(label)));
      const counts = { met: checks.filter(check => check.status === 'met').length, missing: checks.filter(check => check.status === 'missing').length, unknown: checks.filter(check => check.status === 'unknown').length, unverified: checks.filter(check => check.status === 'unverified').length, notApplicable: checks.filter(check => check.status === 'not_applicable').length };
      const status = checks.some(check => check.status === 'missing') ? 'missing' : checks.some(check => check.status === 'unverified') ? 'unverified' : checks.some(check => check.status === 'unknown') || !checks.length ? 'unknown' : 'met';
      return { label, status, counts, uncoveredRecordIds: coverageRows.filter(row => !(row.coverage?.checks || []).some(check => (check.label === label || check.id === label || label.includes(check.label) || check.label.includes(label)) && check.status === 'met')).map(row => row.record.stableId).slice(0, 20), action: checks.find(check => check.action)?.action || category.suggestedCreationActions[0] || null };
    });
    const environments = [...new Set(records.flatMap(record => record.recordKind === 'entity' && record.entity?.typeId === 'Configuration' ? [record.entity.applicableContext?.environment, record.entity.props?.effectiveWhen, ...(record.entity.props?.environments || [])].filter(Boolean) : []))];
    const unresolved = criteriaResults.filter(item => item.status !== 'met');
    return { categoryId, criteria, criteriaResults, recordCount: records.length, investigatedScopes: { environments, note: environments.length ? '只表示已记录的环境范围，不代表未调查环境适用。' : '环境范围尚未调查或未声明。' }, unresolved, status: !records.length ? criteria.length ? 'unknown' : 'unconfigured' : unresolved.length ? 'partial' : 'reviewed', message: !records.length ? '当前分类没有记录；空状态不自动等于项目不完整。' : '按标准目录职责、对象关系、来源和已调查范围检查；不按对象数量平均，也不计算项目完成率。', criteriaConfigured: criteria.length > 0 };
  }
  function clearCache() { cache.clear(); }
  return { context, getKnowledge, listKnowledge, getRelations, getFlow, getSources, getTrust, getCoverage, getImpact, getContractContext, policyFor, resolveCatalog, recordCoverage, categoryCoverage, assignedCategoryIds, recordsFor, clearCache, CATEGORY_POLICIES };
});
