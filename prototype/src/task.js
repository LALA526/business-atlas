/* Business Atlas V9.3 — Task Semantic Contract, impact investigation and V1.2 delivery closure. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const P = commonjs ? require('./v6-projection.js') : root.AtlasV6Projection;
  const Access = commonjs ? require('./v9-access.js') : root.AtlasV9Access;
  const Reading = commonjs ? require('./knowledge-reading.js') : root.AtlasV93Reading;
  const D = commonjs ? require('./decision.js') : root.AtlasV93Decision;
  const W = commonjs ? require('./write-access.js') : root.AtlasWriteAccess;
  const api = factory(C, P, Access, Reading, D, W);
  if (commonjs) module.exports = api;
  root.AtlasV93Task = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (C, P, Access, Reading, D, W) {
  'use strict';
  const clone = C.clone;
  const COMPLETE_TASK_TYPES = new Set(['new-feature', 'business-rule-change', 'api-behavior-change', 'data-semantic-change', 'cross-module-technical-change']);
  const IMPACT_CATEGORIES = ['target-feature', 'business-rule', 'caller', 'frontend-input-state', 'api-contract', 'backend-implementation', 'database-object', 'parameter-passing', 'event', 'configuration', 'test', 'legacy-path', 'compatibility', 'deployment-migration', 'knowledge'];
  const STATUS_LABELS = { required: '需要修改或交付', potential: '潜在影响，待复核', 'no-change': '已检查，无需修改', deferred: '有影响，转其他 Task', unknown: '尚未确认' };

  function readTask(project, ctx, taskId) {
    const read = Access.readRecord(project, ctx, 'task', taskId);
    if (!read.ok) return read;
    const task = read.data;
    if (!task.semanticContract) {
      const decision = Reading.decisionProjection(project, ctx, task.rootId);
      task.semanticContract = {
        contractVersion: 'TSC/1.0', taskType: 'legacy-unassessed', strictness: 'complete', baseBaselineId: ctx.baselineId,
        businessIntent: { goal: task.title, problem: '旧任务尚未记录本次真实业务问题。', reason: 'V9.3 兼容投影，未回写原任务。', requestedVersion: null },
        existingBehavior: { specificationRefs: (decision.approvedRules || []).map(item => ({ ruleId: item.entity.id, version: item.version?.version, baselineId: ctx.baselineId })), observedBehaviorRefs: (decision.observations || []).map(item => item.id), conformance: decision.conformance?.status || 'unverified' },
        targetBehavior: { narrative: '尚未明确本次要改变的目标业务行为。', decisionChanges: [], defaultAndException: '待确认', applicableScope: '待确认' },
        businessDifference: { added: [], modified: [], removed: [], unchanged: [], unknown: ['目标变化与保留约束尚未分析'] }, retainedConstraints: [],
        acceptanceCaseRefs: (decision.cases || []).map(item => item.id),
        knowledgeDelivery: { createRefs: [], updateRefs: [], preserveHistory: true, implementationVerificationRefs: [], issueClosureRefs: task.issueIds || [], targetBaselineStatus: 'unassessed' },
        migrationNotice: '这是旧任务的按需语义投影，不是一份新规范，也没有自动补全未知内容。'
      };
      task.impactInvestigation ||= { investigationVersion: 'IMPACT/1.0', items: [], uncheckedCategories: [...IMPACT_CATEGORIES], legacyPathCheck: { status: 'unknown', conclusion: '旧任务尚未完成影响调查。' }, authorizationComplete: true };
      task.deliveryState = 'semantic-assessment-required';
    }
    return { ...read, data: task };
  }
  function listTasks(project, ctx) { return Access.listRecords(project, ctx, 'task').data.map(task => readTask(project, ctx, task.id)).filter(item => item.ok).map(item => item.data); }
  function readIssue(project, ctx, issueId) { return Access.readRecord(project, ctx, 'issue', issueId); }
  function listIssues(project, ctx) { return Access.listRecords(project, ctx, 'issue').data; }

  function semanticContractFor(project, ctx, input) {
    const decision = input.rootId ? Reading.decisionProjection(project, ctx, input.rootId) : { ok: true, root: null, approvedRules: [], observations: [], cases: [], mechanisms: [], conformance: { status: 'unverified' } };
    if (!decision.ok) return { ok: false, code: decision.code, errors: ['关联功能在当前权限和基线下不可读'] };
    const taskType = input.taskType || (input.targetBehavior ? 'business-rule-change' : 'clarification'), strictness = COMPLETE_TASK_TYPES.has(taskType) ? 'complete' : 'simplified';
    return { ok: true, decision, contract: {
      contractVersion: 'TSC/1.0', taskType, strictness, baseBaselineId: ctx.baselineId,
      businessIntent: { goal: String(input.businessGoal || input.description || '').trim(), problem: String(input.problem || input.description || '').trim(), reason: String(input.reason || input.problem || input.description || '').trim(), requestedVersion: input.requestedVersion || `REQ-${C.now().slice(0, 10)}` },
      existingBehavior: { specificationRefs: decision.approvedRules.map(item => ({ ruleId: item.entity.id, version: item.version?.version, baselineId: ctx.baselineId })), observedBehaviorRefs: decision.observations.map(item => item.id), conformance: decision.conformance.status },
      targetBehavior: { narrative: String(input.targetBehavior || '').trim() || (taskType === 'clarification' ? '' : strictness === 'simplified' ? '不改变外部业务行为，仅执行批准的内部工程调整。' : ''), decisionChanges: [], defaultAndException: String(input.defaultAndException || '沿用已批准规范；变化部分待候选业务决策明确。'), applicableScope: input.applicableScope || decision.root?.props?.applicableScope || decision.approvedRules[0]?.version?.scope || '待确认适用范围' },
      businessDifference: { added: [], modified: strictness === 'simplified' ? ['内部技术实现，外部业务行为保持不变'] : [], removed: [], unchanged: strictness === 'simplified' ? decision.approvedRules.map(item => `${item.entity.name}@${item.version?.version}`) : [], unknown: taskType === 'clarification' ? ['目标行为、保留约束与验收方式待澄清'] : strictness === 'complete' ? ['新增、修改、删除和保留行为将在候选目标形成后逐项确认'] : [] },
      retainedConstraints: decision.approvedRules.map(item => ({ ruleId: item.entity.id, version: item.version?.version, reason: '未被明确变更的已批准规则必须继续成立' })),
      acceptanceCaseRefs: decision.cases.map(item => item.id),
      knowledgeDelivery: { createRefs: [], updateRefs: decision.approvedRules.map(item => ({ ruleId: item.entity.id, baseVersion: item.version?.version })), preserveHistory: true, implementationVerificationRefs: [], issueClosureRefs: [], targetBaselineStatus: 'not-created' },
      sourceRefs: clone(input.sourceRefs || []), originDescription: String(input.currentBehavior || '').trim() || null
    } };
  }

  function createChangeTask(project, ctx, input) {
    W.assert(ctx, 'task:create');
    const description = String(input.description || `${input.businessGoal || ''}\n${input.problem || ''}`.trim()).trim();
    if (!description) return { ok: false, errors: ['请填写需求描述；信息不足时会创建待澄清需求，而不是自动生成完整合同'] };
    const taskType = input.taskType || (input.targetBehavior ? 'business-rule-change' : 'clarification');
    if (COMPLETE_TASK_TYPES.has(taskType) && !String(input.targetBehavior || '').trim()) return { ok: false, errors: ['行为变化任务必须描述目标业务行为；不能只填写技术修改位置'] };
    input = { ...input, description, title: String(input.title || description.split('\n')[0]).trim().slice(0, 60) || '待澄清需求', businessGoal: input.businessGoal || description, problem: input.problem || description };
    const built = semanticContractFor(project, ctx, input); if (!built.ok) return built;
    const task = C.createTask(project, { rootId: input.rootId, title: input.title.trim(), purpose: input.purpose || 'development', by: input.by || '当前用户' }, ctx);
    task.semanticContract = built.contract;
    task.knowledgeIds = [...new Set([...task.knowledgeIds, ...built.decision.approvedRules.map(item => item.entity.id), ...built.decision.observations.map(item => item.id), ...built.decision.cases.map(item => item.id)])];
    task.impactInvestigation = investigateImpact(project, ctx, task);
    task.deliveryState = taskType === 'clarification' ? 'clarification-required' : 'candidate-target-required';
    task.history.push({ at: C.now(), event: 'semantic-contract-created', baseBaselineId: ctx.baselineId, taskType, impactCandidateCount: task.impactInvestigation.items.length });
    return { ok: true, task, decision: built.decision };
  }

  function proposeDecisionChange(project, ctx, taskId, input) {
    W.assert(ctx, 'decision:propose');
    const taskRead = readTask(project, ctx, taskId); if (!taskRead.ok) return taskRead;
    const task = project.tasks.find(item => item.id === taskId), ruleRead = Access.readEntity(project, ctx, input.ruleId);
    if (input.proposalId && (project.decisionRecords || []).some(record => record.decisionId === input.proposalId && record.approval)) return { ok: false, code: 'DECISION_LOCKED', errors: ['已处理业务决定不能继续编辑，请创建新的候选修订'] };
    if (!ruleRead.ok || ruleRead.data.typeId !== 'Rule') return { ok: false, errors: ['候选目标必须引用当前可读的已批准规则，不能另写无版本规范副本'] };
    const rule = ruleRead.data, version = C.ruleVersion(rule, { version: input.baseVersion || rule.currentVersion, strict: true });
    if (!version || !D.isApprovedTarget(rule, version, ctx)) return { ok: false, errors: ['只能基于当前批准规则版本提出候选变化'] };
    const row = (version.rows || []).find(item => item.id === input.rowId);
    if (!row) return { ok: false, errors: ['请选择要调整的真实判断行'] };
    const condition = input.condition || null;
    if (condition && !D.SUPPORTED_OPERATORS.includes(condition.op)) return { ok: false, errors: ['该条件类型暂不支持建模模拟'] };
    const proposalId = input.proposalId || `DEC-${taskId}-${String((task.semanticContract.targetBehavior.decisionChanges || []).length + 1).padStart(2, '0')}`;
    const change = { proposalId, ruleId: rule.id, baseVersion: version.version, baseBaselineId: ctx.baselineId, proposedRevision: `proposal-${(task.semanticContract.targetBehavior.decisionChanges || []).length + 1}`, operation: 'patch-decision-row', rowId: row.id, addCondition: clone(condition), inputSemantics: clone(input.inputSemantics || null), outcomeId: input.outcomeId || row.outcomeId, approvalStatus: 'proposed', decisionOwner: input.decisionOwner || '业务负责人', naturalLanguage: input.naturalLanguage || `在“${rule.name}”的 ${row.id} 判断行上形成候选条件变化。` };
    task.semanticContract.taskType = 'business-rule-change'; task.semanticContract.strictness = 'complete';
    task.semanticContract.targetBehavior.decisionChanges = [...(task.semanticContract.targetBehavior.decisionChanges || []).filter(item => item.proposalId !== proposalId), change];
    task.semanticContract.targetBehavior.narrative = input.targetBehavior || change.naturalLanguage;
    task.semanticContract.targetBehavior.defaultAndException = input.defaultAndException || '默认分支和异常未被本提案修改，仍需在验收中回归。';
    task.semanticContract.businessDifference = { added: condition ? [{ type: 'condition', naturalLanguage: `新增判断条件：${D.describeCondition(condition, version)}`, ruleId: rule.id, rowId: row.id, condition: clone(condition), approvalStatus: 'proposed' }] : [], modified: [{ ruleId: rule.id, from: `${rule.id}@${version.version}`, to: change.proposedRevision, rowId: row.id, before: D.describeCondition(D.conditionFromRow(row), version), after: condition ? `${D.describeCondition(D.conditionFromRow(row), version)} 且 ${D.describeCondition(condition, version)}` : D.describeCondition(D.conditionFromRow(row), version), outcomeId: change.outcomeId }], removed: [], unchanged: (task.semanticContract.existingBehavior.specificationRefs || []).filter(ref => ref.ruleId !== rule.id).map(ref => `${ref.ruleId}@${ref.version}`), unknown: ['候选目标尚未批准，不进入正式业务判断'] };
    task.semanticContract.retainedConstraints = (task.semanticContract.retainedConstraints || []).map(ref => ref.ruleId === rule.id ? { ...ref, reason: `除 ${row.id} 的明确候选变化外，其他判断行、默认分支和异常必须继续成立` } : ref);
    task.semanticContract.knowledgeDelivery.updateRefs = [...new Set([...(task.semanticContract.knowledgeDelivery.updateRefs || []).map(ref => typeof ref === 'string' ? ref : ref.ruleId), rule.id])];
    task.impactInvestigation = investigateImpact(project, ctx, task);
    task.deliveryState = 'business-approval-pending'; task.updatedAt = C.now();
    project.decisionRecords ||= [];
    const record = { decisionId: proposalId, taskId, knowledgeRefs: [task.rootId, rule.id].filter(Boolean), question: change.naturalLanguage, evidenceRefs: rule.sourceIds || [], options: [{ id: 'maintain', label: '保持当前批准规则' }, { id: 'proposed', label: change.naturalLanguage }], recommendation: { optionId: 'proposed', rationale: input.recommendationReason || '候选建议需结合影响调查和验收案例复核。', notApproval: true }, decisionOwner: change.decisionOwner, approval: null, effectiveScope: null, impactedKnowledgeAndImplementation: (task.impactInvestigation.items || []).map(item => item.entityId).filter(Boolean), version: change.proposedRevision };
    const recordIndex = project.decisionRecords.findIndex(item => item.decisionId === proposalId);
    if (recordIndex >= 0) project.decisionRecords[recordIndex] = record; else project.decisionRecords.push(record);
    task.history.push({ at: C.now(), event: 'decision-proposed', proposalId, ruleId: rule.id, baseVersion: version.version });
    return { ok: true, task, change, autoApproved: false };
  }

  function decideBusinessChange(project, ctx, taskId, proposalId, input = {}) {
    W.assert(ctx, 'decision:approve');
    const taskRead = readTask(project, ctx, taskId); if (!taskRead.ok) return taskRead;
    const task = project.tasks.find(item => item.id === taskId), record = (project.decisionRecords || []).find(item => item.decisionId === proposalId && item.taskId === taskId), change = task.semanticContract?.targetBehavior?.decisionChanges?.find(item => item.proposalId === proposalId);
    if (!record || !change) return { ok: false, code: 'DECISION_NOT_FOUND', errors: ['候选业务决定不存在或不属于当前任务'] };
    if (record.approval) return { ok: false, code: 'DECISION_LOCKED', errors: ['该业务决定已经处理，不能重复批准或拒绝'] };
    if (!['approved', 'rejected'].includes(input.decision) || !String(input.by || '').trim() || !String(input.rationale || '').trim()) return { ok: false, errors: ['批准或拒绝必须记录处理人及理由'] };
    if (input.decision === 'rejected') {
      record.approval = { decision: 'rejected', optionId: 'maintain', by: input.by, at: C.now(), rationale: input.rationale };
      change.approvalStatus = 'rejected'; task.deliveryState = 'business-rejected'; task.history.push({ at: C.now(), event: 'decision-rejected', proposalId, by: input.by });
      return { ok: true, task, record, draftCreated: false };
    }
    if (ctx.baselineId !== project.versionLayers.baseline || ctx.versionMode === 'draft') return { ok: false, code: 'BASELINE_CONFLICT', errors: ['只能基于当前批准基线批准变化，不能从草稿或历史视图直接发布'] };
    const ruleRead = Access.readEntity(project, ctx, change.ruleId); if (!ruleRead.ok) return { ok: false, code: ruleRead.code, errors: ['目标规则在当前上下文不可读'] };
    const rule = ruleRead.data;
    if (rule.currentVersion !== change.baseVersion) return { ok: false, code: 'BASE_VERSION_CONFLICT', errors: [`规则当前为 ${rule.currentVersion}，候选基于 ${change.baseVersion}，请重新形成候选`] };
    const version = C.ruleVersion(rule, { version: change.baseVersion, strict: true }), definition = clone(version), row = (definition.rows || []).find(item => item.id === change.rowId);
    if (!row) return { ok: false, errors: ['候选判断行已不存在，请重新形成候选'] };
    if (!(definition.outcomes || []).some(outcome => outcome.id === change.outcomeId)) return { ok: false, errors: ['候选结果不属于当前规则输出'] };
    if (change.addCondition) {
      const inputId = change.addCondition.inputId, semantics = change.inputSemantics;
      if (!semantics?.label || !semantics?.businessMeaning) return { ok: false, errors: ['新增判断输入在批准前必须说明业务名称和业务含义'] };
      if (!(definition.inputs || []).some(item => item.id === inputId)) definition.inputs.push({ id: inputId, label: semantics.label, businessMeaning: semantics.businessMeaning, kind: semantics.kind || 'string', required: true });
      row.condition = { op: 'all', conditions: [D.conditionFromRow(row), clone(change.addCondition)] }; delete row.when;
    }
    row.outcomeId = change.outcomeId;
    definition.version = 'draft'; definition.status = 'draft'; definition.factKind = 'target_specification'; definition.semanticRole = 'business_decision'; definition.summary = change.naturalLanguage;
    C.execute(project, 'DefineRule', { entityId: rule.id, name: rule.name, description: rule.description, scope: definition.scope, definition, by: input.by }, ctx);
    record.approval = { decision: 'approved', optionId: 'proposed', by: input.by, at: C.now(), rationale: input.rationale };
    record.effectiveScope = definition.scope; change.approvalStatus = 'approved';
    task.semanticContract.businessDifference.unknown = (task.semanticContract.businessDifference.unknown || []).filter(item => !item.includes('未批准'));
    const ws = C.workspace(project); ws.deliveryLinks ||= [];
    if (!ws.deliveryLinks.some(link => link.proposalId === proposalId)) ws.deliveryLinks.push({ taskId, proposalId, ruleId: rule.id, baseVersion: change.baseVersion, knowledgeChangeId: `KC-SIM-${taskId}`, manifestId: `KDM-SIM-${taskId}` });
    task.delivery ||= { deliveryProtocol: 'V1.2', simulation: true, knowledgeChanges: [], deliveryManifest: { id: `KDM-SIM-${taskId}`, protocol: 'V1.2 Knowledge Delivery Manifest', status: 'draft-knowledge', approvedBaselineId: null } };
    task.delivery.knowledgeChanges ||= [];
    const knowledgeId = `KC-SIM-${taskId}`;
    if (!task.delivery.knowledgeChanges.some(item => item.id === knowledgeId)) task.delivery.knowledgeChanges.push({ id: knowledgeId, status: 'draft', kind: 'approved-business-decision-draft', decisionRefs: [{ ruleId: rule.id, baseVersion: change.baseVersion, proposalId }], mayModifyApprovedBaseline: false });
    task.delivery.deliveryManifest ||= { id: `KDM-SIM-${taskId}`, protocol: 'V1.2 Knowledge Delivery Manifest', status: 'draft-knowledge', approvedBaselineId: null };
    task.semanticContract.knowledgeDelivery.targetBaselineStatus = 'draft'; task.semanticContract.knowledgeDelivery.changeSetId = ws.id;
    task.deliveryState = 'knowledge-draft-pending'; task.history.push({ at: C.now(), event: 'decision-approved-draft-created', proposalId, changeSetId: ws.id, by: input.by });
    return { ok: true, task, record, draftCreated: true, changeSet: ws };
  }

  function recordLegacyPathReview(project, ctx, taskId, input = {}) {
    W.assert(ctx, 'impact:write');
    const taskRead = readTask(project, ctx, taskId); if (!taskRead.ok) return taskRead;
    if (!String(input.rationale || '').trim()) return { ok: false, errors: ['请记录旧路径、兼容与重复实现的人工调查依据；原型没有自动完成代码调查'] };
    const task = project.tasks.find(item => item.id === taskId);
    task.impactInvestigation.legacyPathCheck = { status: 'checked', conclusion: input.rationale, checkedBy: input.by || '当前用户', checkedAt: C.now(), evidenceRefs: clone(input.evidenceRefs || []), automaticCodeInvestigation: false };
    task.history.push({ at: C.now(), event: 'legacy-path-reviewed', rationale: input.rationale });
    return { ok: true, task };
  }

  function categoryFor(entity) {
    return ({ Feature: 'target-feature', Requirement: 'target-feature', Scenario: 'target-feature', Rule: 'business-rule', Actor: 'caller', Surface: 'frontend-input-state', API: 'api-contract', Component: 'backend-implementation', Module: 'backend-implementation', DataEntity: 'database-object', Event: 'event', Configuration: 'configuration', Test: 'test', BusinessCase: 'test', Resource: 'knowledge', ObservedBehavior: 'knowledge' })[entity?.typeId] || 'knowledge';
  }
  function shortestPaths(projection, fromIds, maxDepth = 3) {
    const paths = new Map(fromIds.map(id => [id, []])), queue = [...fromIds];
    for (let depth = 0; depth < maxDepth; depth++) {
      const current = queue.splice(0);
      current.forEach(id => {
        projection.assertions.forEach(assertion => {
          if (!['constrains', 'blocks', 'implements', 'triggers', 'reads', 'writes', 'dependsOn', 'affects', 'produces', 'transitions', 'changes', 'uses', 'requires', 'appliesTo'].includes(assertion.predicate)) return;
          let other = null, from = null, to = null;
          if (assertion.subject === id && assertion.object) { other = assertion.object; from = id; to = other; }
          else if (assertion.object === id) { other = assertion.subject; from = id; to = other; }
          if (!other || paths.has(other)) return;
          paths.set(other, [...paths.get(id), { from, predicate: assertion.predicate, to, assertionId: assertion.assertionId, direction: assertion.subject === id ? 'outgoing' : 'incoming' }]); queue.push(other);
        });
      });
    }
    return paths;
  }
  function investigateImpact(project, ctx, task) {
    const changes = task.semanticContract?.targetBehavior?.decisionChanges || [];
    if (!task.rootId && !changes.length) return { investigationVersion: 'IMPACT/1.0', generatedFrom: { baselineId: ctx.baselineId, relationClosure: 'no-associated-feature-yet', allReachableAreImpacts: false }, items: [], uncheckedCategories: [], legacyPathCheck: { status: 'unknown', conclusion: '待澄清关联功能后，再基于现有知识形成影响候选；没有执行代码调查。' }, redundancyFindings: [], authorizationComplete: true, analysisBoundary: '仅基于现有知识的影响候选，未检查真实仓库' };
    const decision = Reading.decisionProjection(project, ctx, task.rootId), changedRuleIds = changes.map(change => change.ruleId).filter(Boolean), fromIds = [...new Set([task.rootId, ...changedRuleIds].filter(Boolean))], paths = decision.ok ? shortestPaths(decision.projection, fromIds) : new Map(), items = [];
    const add = (entityId, category, status, rationale, path = [], evidenceRefs = []) => { const id = `${task.id}-IMP-${items.length + 1}`; if (items.some(item => item.entityId === entityId && item.category === category)) return; items.push({ id, entityId, category, status, completion: status === 'required' ? 'pending' : 'unknown', decision: status === 'required' ? 'modify-or-define' : status === 'no-change' ? 'checked-no-change' : 'investigate', path: clone(path), relationSemantics: path.map(step => step.predicate).join(' → ') || '任务目标', evidenceRefs: clone(evidenceRefs), rationale, checkedBy: null }); };
    add(task.rootId, 'target-feature', 'required', '从目标业务行为开始定义变化，不以用户点名的文件或参数作为完整边界。', [], [task.rootId]);
    changedRuleIds.forEach(ruleId => add(ruleId, 'business-rule', 'required', '该已批准规则版本具有显式候选修订，需要形成新的知识提案并完成业务批准。', paths.get(ruleId) || [], [ruleId]));
    if (decision.ok) {
      paths.forEach((path, id) => {
        if (!path.length || fromIds.includes(id)) return;
        const entity = Access.readEntity(project, ctx, id).data; if (!entity || ['Project', 'Domain'].includes(entity.typeId)) return;
        const category = categoryFor(entity), rulePreserved = entity.typeId === 'Rule' && !changedRuleIds.includes(id);
        add(id, category, 'potential', rulePreserved ? '既有规则未被候选目标点名修改，需要回归确认保持不变，而不是自动视为无影响。' : '由语义路径形成的候选影响；需要进一步证据判断必须修改、无需修改或转其他 Task。', path, path.map(step => step.assertionId));
      });
      decision.cases.filter(entity => changedRuleIds.includes(entity.props?.decisionRef?.ruleId) || entity.props?.decisionRef?.ruleId === task.rootId).forEach(entity => add(entity.id, 'test', 'potential', '变更规则的正例、反例、边界和未知案例需要复核并补充回归，不得通过修改案例来迎合新规则。', [{ from: entity.props.decisionRef.ruleId, predicate: 'verifiedBy', to: entity.id }], entity.sourceIds || []));
      decision.mechanisms.filter(entity => entity.props?.redundancyStatus === 'duplicate-candidate').forEach(entity => add(entity.id, 'legacy-path', 'potential', '发现重复实现候选；应调查调用方、无效兼容和死代码后主动提出收敛，但不得借此自动改变业务语义。', paths.get(entity.id) || [], entity.sourceIds || []));
    }
    const previousItems = task.impactInvestigation?.items || [];
    items.forEach(item => {
      const previous = previousItems.find(old => old.entityId === item.entityId && old.category === item.category);
      if (previous) Object.assign(item, { status: previous.status, completion: previous.completion, decision: previous.decision, rationale: previous.rationale, checkedBy: previous.checkedBy, checkedAt: previous.checkedAt, deferredTaskId: previous.deferredTaskId, evidenceRefs: [...new Set([...(item.evidenceRefs || []), ...(previous.evidenceRefs || [])])] });
    });
    return { investigationVersion: 'IMPACT/1.0', generatedFrom: { baselineId: ctx.baselineId, relationClosure: 'authorized-bounded-semantic-candidates', maxDepth: 3, allReachableAreImpacts: false }, items, uncheckedCategories: IMPACT_CATEGORIES.filter(category => items.some(item => item.category === category && ['unknown', 'potential'].includes(item.status))), legacyPathCheck: { ...(task.impactInvestigation?.legacyPathCheck || {}), status: task.impactInvestigation?.legacyPathCheck?.status === 'checked' ? 'checked' : items.some(item => item.category === 'legacy-path') ? 'potential' : 'unknown', conclusion: task.impactInvestigation?.legacyPathCheck?.conclusion || '尚未检查旧调用路径；这里只提供基于现有知识的候选，不宣称已检查所有代码路径。' }, redundancyFindings: items.filter(item => item.category === 'legacy-path').map(item => item.id), authorizationComplete: true, analysisBoundary: '仅基于现有知识的影响候选，未检查真实仓库' };
  }

  function recordImpactDecision(project, ctx, taskId, impactId, input) {
    W.assert(ctx, 'impact:write');
    const read = readTask(project, ctx, taskId); if (!read.ok) return read;
    if (!['required', 'potential', 'no-change', 'deferred', 'unknown'].includes(input.status)) return { ok: false, errors: ['无效影响判断状态'] };
    if (['no-change', 'deferred'].includes(input.status) && !String(input.rationale || '').trim()) return { ok: false, errors: ['无需修改或转其他 Task 时必须记录调查依据'] };
    if (input.status === 'deferred' && !String(input.deferredTaskId || '').trim()) return { ok: false, errors: ['转其他 Task 时必须填写承接任务'] };
    const task = project.tasks.find(item => item.id === taskId), item = task.impactInvestigation.items.find(impact => impact.id === impactId);
    if (!item) return { ok: false, errors: ['影响项不存在'] };
    Object.assign(item, { status: input.status, completion: input.completion || (input.status === 'no-change' ? 'checked' : input.status === 'required' ? 'pending' : 'unknown'), rationale: String(input.rationale || item.rationale || '').trim(), deferredTaskId: input.deferredTaskId || null, decision: input.status === 'no-change' ? 'checked-no-change' : input.status === 'deferred' ? 'deferred' : input.status === 'required' ? 'modify-or-deliver' : 'investigate', checkedBy: input.checkedBy || '当前用户', checkedAt: C.now(), evidenceRefs: clone(input.evidenceRefs || item.evidenceRefs || []) });
    task.impactInvestigation.uncheckedCategories = IMPACT_CATEGORIES.filter(category => task.impactInvestigation.items.some(impact => impact.category === category && ['unknown', 'potential'].includes(impact.status)));
    task.history.push({ at: C.now(), event: 'impact-reviewed', impactId, status: input.status, completion: item.completion });
    return { ok: true, item, task };
  }

  function attachSimulatedDelivery(project, ctx, taskId, input) {
    W.assert(ctx, 'delivery:record');
    const read = readTask(project, ctx, taskId); if (!read.ok) return read;
    if (!String(input.codeRevision || '').trim() || !String(input.testCommand || '').trim()) return { ok: false, errors: ['模拟交付也必须明确代码版本和测试命令'] };
    const task = project.tasks.find(item => item.id === taskId), caseRefs = task.semanticContract.acceptanceCaseRefs || [], manifestId = `KDM-SIM-${taskId}`, knowledgeId = `KC-SIM-${taskId}`, testId = `TEST-SIM-${taskId}`, previous = task.delivery || {}, previousManifest = previous.deliveryManifest || {};
    const existingChanges = previous.knowledgeChanges || [], knowledgeChange = existingChanges.find(item => item.id === knowledgeId) || { id: knowledgeId, status: 'proposed', kind: 'PR-candidate-knowledge', decisionRefs: task.semanticContract.targetBehavior.decisionChanges || [], mayModifyApprovedBaseline: false };
    task.delivery = {
      ...previous, deliveryProtocol: 'V1.2', simulation: true, requirementVersion: task.semanticContract.businessIntent.requestedVersion,
      approvedBusinessBaselineId: previousManifest.approvedBaselineId || previous.approvedBusinessBaselineId || task.semanticContract.baseBaselineId,
      pullRequest: { id: `PR-SIM-${taskId}`, title: '明确标注的模拟 PR，不是真实仓库事件', status: 'merged', codeRevision: input.codeRevision, candidateKnowledgeRefs: [knowledgeId] },
      implementation: { status: 'merged', codeRevision: input.codeRevision, changeRefs: task.impactInvestigation.items.filter(item => item.status === 'required').map(item => item.entityId).filter(Boolean), evidenceRefs: input.evidenceRefs || [] },
      tests: [{ id: testId, status: 'passed', command: `模拟记录：${input.testCommand}`, testedCodeRevision: input.codeRevision, caseRefs, evidence: '用户在原型中录入的模拟测试记录，未连接真实测试执行器', simulation: true }],
      knowledgeChanges: [...existingChanges.filter(item => item.id !== knowledgeId), knowledgeChange],
      deliveryManifest: { ...previousManifest, id: manifestId, protocol: 'V1.2 Knowledge Delivery Manifest', status: 'generated-after-merge', codeRevision: input.codeRevision, testRevision: testId, knowledgeChangeRefs: [knowledgeId], approvedBaselineId: previousManifest.approvedBaselineId || null, deploymentVersion: input.deploymentVersion || 'simulated-deployment', reconciliation: previousManifest.reconciliation || 'knowledge-approval-pending' },
      acceptanceRecords: [{ id: `ACC-SIM-${taskId}`, status: 'passed', caseRefs, testRef: testId, codeRevision: input.codeRevision, simulation: true }]
    };
    project.knowledgeDeliveryManifests ||= [];
    const manifestIndex = project.knowledgeDeliveryManifests.findIndex(item => item.id === manifestId);
    if (manifestIndex >= 0) project.knowledgeDeliveryManifests[manifestIndex] = task.delivery.deliveryManifest; else project.knowledgeDeliveryManifests.push(task.delivery.deliveryManifest);
    task.history.push({ at: C.now(), event: 'simulated-delivery-attached', manifestId, codeRevision: input.codeRevision });
    task.deliveryState = evaluateDelivery(project, ctx, taskId).state;
    return { ok: true, task, simulation: true };
  }

  function evaluateDelivery(project, ctx, taskId) {
    const read = readTask(project, ctx, taskId); if (!read.ok) return { ok: false, state: 'forbidden', blockers: [{ code: read.code, reason: '任务在当前权限下不可读' }], warnings: [] };
    const task = read.data, blockers = [], warnings = [], delivery = task.delivery || null, contract = task.semanticContract;
    const add = (code, reason) => blockers.push({ code, reason });
    if (!contract?.businessIntent?.goal || !contract.businessIntent.problem || !contract.targetBehavior?.narrative) add('SEMANTIC_CONTRACT_INCOMPLETE', '业务目的、具体问题或目标行为尚未明确。');
    if (contract.strictness === 'complete' && (contract.businessDifference?.unknown || []).length) add('BUSINESS_DIFFERENCE_UNKNOWN', contract.businessDifference.unknown.join('；'));
    if (task.activeContractId) {
      const rawPack = (project.contextPackages || []).find(pack => pack.packageId === task.activeContractId);
      if (!rawPack?.valid || rawPack.reviewStatus === 'requires-review') add('CONTRACT_STALE', '当前正式知识契约无效或需要复核。');
      else {
        const contractView = Access.readContract(project, { ...ctx, versionMode: 'historical', baselineId: rawPack.versionBinding.baselineId, readPurpose: 'formal' }, rawPack.packageId);
        if (!contractView.ok || !contractView.data.authorization?.executionAllowed) add('CONTRACT_NOT_EXECUTION_READY', '当前权限或适用条件无法完整读取正式契约。');
      }
    } else add('FORMAL_CONTRACT_MISSING', '尚未生成有效正式知识契约。');
    const issues = listIssues(project, ctx).filter(issue => issue.taskId === taskId && !['resolved', 'cancelled'].includes(issue.status));
    if (issues.length) add('UNRESOLVED_ISSUES', `${issues.length} 个关联业务问题尚未关闭。`);
    const pendingDecisions = (project.decisionRecords || []).filter(record => record.taskId === taskId && !record.approval && contract.targetBehavior?.decisionChanges?.length);
    if (pendingDecisions.length) add('BUSINESS_DECISION_UNAPPROVED', `${pendingDecisions.length} 项候选业务决定尚未由责任人批准。`);
    const rejectedDecisions = (project.decisionRecords || []).filter(record => record.taskId === taskId && record.approval?.decision === 'rejected' && contract.targetBehavior?.decisionChanges?.some(change => change.proposalId === record.decisionId && change.approvalStatus !== 'superseded'));
    if (rejectedDecisions.length) add('BUSINESS_DECISION_REJECTED', `${rejectedDecisions.length} 项候选已被拒绝；可以维持原规范关闭需求，或形成新的候选，不能按变更已批准验收。`);
    const investigation = task.impactInvestigation || { items: [], authorizationComplete: false };
    if (investigation.authorizationComplete === false || investigation.excludedCount > 0) add('IMPACT_AUTHORIZATION_INCOMPLETE', '部分影响内容因权限或版本不可读，不能宣称完整交付。');
    (investigation.items || []).forEach(item => {
      if (['unknown', 'potential'].includes(item.status)) add('IMPACT_UNRESOLVED', `${item.category}：尚未完成影响判断。`);
      if (item.status === 'required' && !['done', 'verified'].includes(item.completion)) {
        if (item.category === 'knowledge' && item.completion === 'pending-knowledge-approval') warnings.push({ code: 'KNOWLEDGE_APPROVAL_PENDING', reason: '知识交付项已生成，但仍待批准。' });
        else add('REQUIRED_IMPACT_INCOMPLETE', `${item.category}：必要修改或交付尚未完成。`);
      }
      if (item.status === 'deferred' && !item.deferredTaskId) add('DEFERRED_IMPACT_UNLINKED', `${item.category}：转其他 Task 但未关联承接任务。`);
      if (item.status === 'no-change' && !item.rationale) add('NO_CHANGE_EVIDENCE_MISSING', `${item.category}：无需修改缺少调查依据。`);
    });
    if (investigation.legacyPathCheck?.status !== 'checked') add('LEGACY_PATH_UNVERIFIED', investigation.legacyPathCheck?.conclusion || '旧路径与兼容约束尚未完成检查。');
    if (!delivery || delivery.implementation?.status !== 'merged') add('IMPLEMENTATION_NOT_MERGED', '实现尚未形成已合并的代码记录。');
    const caseRefs = contract.acceptanceCaseRefs || [], tests = delivery?.tests || [], acceptanceRecords = delivery?.acceptanceRecords || [];
    if (!tests.length || tests.some(test => test.status !== 'passed')) add('TESTS_NOT_PASSED', '测试尚未全部通过。');
    if (delivery?.implementation?.codeRevision && tests.some(test => test.testedCodeRevision !== delivery.implementation.codeRevision)) add('TEST_VERSION_MISMATCH', '测试验证版本与实现代码版本不一致。');
    const testedCases = new Set(tests.flatMap(test => test.caseRefs || []));
    caseRefs.filter(id => !testedCases.has(id)).forEach(id => add('ACCEPTANCE_CASE_NOT_TESTED', `验收案例 ${Access.readEntity(project, ctx, id).data?.name || '当前不可读'} 尚未关联测试。`));
    if (contract.strictness === 'complete' && !acceptanceRecords.some(record => record.status === 'passed')) add('ACCEPTANCE_NOT_PASSED', '关键业务验收尚未通过。');
    const manifest = delivery?.deliveryManifest;
    if (!manifest || manifest.status !== 'generated-after-merge') add('DELIVERY_MANIFEST_MISSING', '代码合并后的 Knowledge Delivery Manifest 尚未生成。');
    if (manifest && delivery?.implementation?.codeRevision && manifest.codeRevision !== delivery.implementation.codeRevision) add('MANIFEST_VERSION_MISMATCH', '交付清单与实现代码版本不一致。');
    const knowledgeApproved = !!(delivery?.knowledgeChanges || []).length && delivery.knowledgeChanges.every(change => change.status === 'approved') && !!manifest?.approvedBaselineId;
    const nonKnowledgeBlockers = blockers.filter(item => !['KNOWLEDGE_APPROVAL_PENDING'].includes(item.code));
    let state = 'not-ready';
    if (!nonKnowledgeBlockers.length && !knowledgeApproved) state = 'implementation-complete-knowledge-pending';
    if (!nonKnowledgeBlockers.length && knowledgeApproved) state = 'fully-delivered';
    if (state === 'implementation-complete-knowledge-pending') warnings.push({ code: 'KNOWLEDGE_APPROVAL_PENDING', reason: '代码与测试记录已完成，但知识提案和批准基线仍未完成；不能显示完整交付。' });
    return { ok: state === 'fully-delivered', state, blockers, warnings, implementationComplete: !!delivery && delivery.implementation?.status === 'merged', testsPassed: tests.length > 0 && tests.every(test => test.status === 'passed'), knowledgeApproved, manifestId: manifest?.id || null, versions: { requirement: delivery?.requirementVersion || contract.businessIntent?.requestedVersion || null, approvedBusinessBaseline: delivery?.approvedBusinessBaselineId || contract.baseBaselineId, code: delivery?.implementation?.codeRevision || null, tested: tests.map(test => test.testedCodeRevision), deployment: manifest?.deploymentVersion || null, knowledgeProposal: (delivery?.knowledgeChanges || []).map(change => ({ id: change.id, status: change.status })), manifestApprovedBaseline: manifest?.approvedBaselineId || null } };
  }

  function completeTask(project, ctx, taskId, input = {}) {
    W.assert(ctx, 'task:accept');
    const closure = evaluateDelivery(project, ctx, taskId);
    if (!closure.ok) return { ok: false, errors: [...closure.blockers.map(item => item.reason), ...closure.warnings.map(item => item.reason)], closure };
    return { ...P.completeTask(project, taskId, { ...input, context: ctx }), closure };
  }

  function exportTaskContract(project, ctx, taskId) {
    W.assert(ctx, 'delivery:export');
    const task = readTask(project, ctx, taskId); if (!task.ok) return task;
    const closure = evaluateDelivery(project, ctx, taskId), issues = listIssues(project, ctx).filter(issue => issue.taskId === taskId);
    let contract = null;
    if (task.data.activeContractId) {
      const raw = (project.contextPackages || []).find(pack => pack.packageId === task.data.activeContractId);
      const view = raw ? Access.readContract(project, { ...ctx, versionMode: 'historical', baselineId: raw.versionBinding.baselineId, readPurpose: 'formal' }, raw.packageId) : null;
      if (view?.ok) contract = view.data;
    }
    return { ok: true, data: { format: 'BusinessAtlas.TaskSemanticContract/1.0', exportedAt: C.now(), readContext: clone(ctx), task: task.data, deliveryClosure: closure, issues, authorizedFormalContract: contract, notice: '导出时重新执行权限、版本与适用性检查；候选目标不是已批准规范。' } };
  }

  return { COMPLETE_TASK_TYPES, IMPACT_CATEGORIES, STATUS_LABELS, readTask, listTasks, readIssue, listIssues, semanticContractFor, createChangeTask, proposeDecisionChange, decideBusinessChange, investigateImpact, recordImpactDecision, recordLegacyPathReview, attachSimulatedDelivery, evaluateDelivery, completeTask, exportTaskContract, categoryFor, shortestPaths };
});
