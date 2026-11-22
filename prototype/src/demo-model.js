/* Business Atlas V9.3 — compatible semantic extensions and cognitive golden fixtures. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const K = commonjs ? require('../v4-core.js') : root.AtlasCore;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const P = commonjs ? require('./v6-projection.js') : root.AtlasV6Projection;
  const V9 = commonjs ? require('./v9-model.js') : root.AtlasV9Model;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const W = commonjs ? require('./write-access.js') : root.AtlasWriteAccess;
  const api = factory(K, C, P, V9, G, W);
  if (commonjs) module.exports = api;
  root.AtlasV93Model = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (K, C, P, V9, G, W) {
  'use strict';
  const Core = C;
  C = { ...C,
    execute: (project, command, input) => Core.execute(project, command, input, W.SYSTEM_CONTEXT),
    createTask: (project, input) => Core.createTask(project, input, W.SYSTEM_CONTEXT),
    createIssue: (project, input) => Core.createIssue(project, { ...input, context: W.SYSTEM_CONTEXT }),
    proposeChange: (project, input) => Core.proposeChange(project, { ...input, context: W.SYSTEM_CONTEXT }),
    publishRevision: (project, input) => Core.publishRevision(project, { ...input, context: W.SYSTEM_CONTEXT })
  };
  const clone = C.clone;
  const GOLDEN_PROJECT_ID = 'PRJ-COG93';
  const publicRead = { read: ['*'], edit: ['editor', 'admin'], approve: ['owner'] };
  const internal = { sensitivity: 'internal', permissions: publicRead };

  function initProject(project) {
    const types = [
      ['BusinessCase', '业务验证案例', '业务认知', 'CASE', [['caseKind', '案例类型', 'enum', ['positive', 'negative', 'boundary', 'unknown', 'regression']], ['decisionRef', '引用业务决策', 'json'], ['inputs', '业务输入', 'json'], ['expected', '预期业务结果', 'json'], ['confirmationStatus', '确认状态', 'text']]],
      ['ObservedBehavior', '当前观察行为', '系统认知', 'OBS', [['subjectId', '观察对象', 'text'], ['observedVersion', '观察版本', 'text'], ['environment', '观察环境', 'text'], ['method', '观察方式', 'text'], ['input', '观察输入', 'json'], ['actualOutput', '实际输出', 'json'], ['confidence', '可信度', 'text'], ['conformance', '规范符合性', 'json']]]
    ];
    types.forEach(([id, name, dimension, idPrefix, fields]) => {
      if (!K.schemaFor(project, id)) K.registerType(project, { id, name, dimension, idPrefix, fields: fields.map(([fieldId, label, dataType, options]) => K.field(fieldId, label, dataType, dataType === 'enum' ? { options } : {})), relations: ['references', 'cites', 'supports', 'implements', 'verifies', 'uses', 'dependsOn', 'affects'] });
      project.schemaRegistry[id].idPrefix ||= idPrefix;
    });
    project.v93SchemaVersion = 1;
    project.knowledgeDeliveryManifests ||= [];
    project.architectureDecisions ||= [];
    project.decisionRecords ||= [];
    project.meta.v93Migration = 'additive-entities-and-task-semantics; no baseline rewrite';
    return project;
  }

  function add(project, typeId, id, name, description, props = {}, extra = {}) {
    if (C.getEntity(project, id, { draft: true })) return C.getEntity(project, id, { draft: true });
    return C.execute(project, 'CreateEntity', { typeId, id, name, description, props, sourceIds: extra.sourceIds || [], ...internal, ...extra }).entity;
  }
  function link(project, assertionId, subject, predicate, object, qualifiers = {}, sourceRefs = []) {
    if (C.getAssertion(project, assertionId, { draft: true })) return;
    return C.execute(project, 'LinkKnowledge', { assertionId, subject, predicate, object, qualifiers, sourceRefs, explanation: qualifiers.explanation || 'V9.3 统一知识语义关联' });
  }
  function caseEntity(project, id, name, caseKind, ruleId, version, inputs, expected, sourceIds, confirmationStatus = 'confirmed') {
    return add(project, 'BusinessCase', id, name, `${name}；引用 ${ruleId}@${version}，不替代规则。`, { caseKind, decisionRef: { ruleId, version }, inputs, expected, confirmationStatus, applicableCondition: '与所引用规则一致', observedBehaviorRefs: [] }, { sourceIds });
  }
  function setRuleSemantics(project, ruleId, semantics) {
    const rule = C.getEntity(project, ruleId, { draft: true });
    if (!rule) return;
    const versions = clone(rule.versions || {}), current = rule.currentVersion;
    versions[current] = { ...(versions[current] || {}), factKind: 'target_specification', semanticRole: 'business_decision', ...clone(semantics) };
    C.execute(project, 'SetProperty', { entityId: ruleId, path: 'factKind', value: 'target_specification' });
    C.execute(project, 'SetProperty', { entityId: ruleId, path: 'semanticRole', value: 'business_decision' });
    C.execute(project, 'SetProperty', { entityId: ruleId, path: 'versions', value: versions });
  }
  function publishMigration(project, title, reason) {
    const proposed = C.proposeChange(project, { title, reason });
    if (!proposed.ok) throw new Error(`V9.3 migration proposal failed for ${project.meta.id}: ${proposed.errors.join('；')}`);
    const published = C.publishRevision(project, { confirmedBy: '业务负责人与项目知识负责人' });
    if (!published.ok) throw new Error(`V9.3 migration publish failed for ${project.meta.id}: ${published.errors.join('；')}`);
    return published.baseline;
  }

  function seedManufacturing(project) {
    if (project._v93SemanticsBaseline) return;
    if (project.draftWorkspace?.operations?.length && !project._v93OwnMigration) { project._v93SemanticsMigrationDeferred = true; return; }
    project._v93OwnMigration = true;
    const registrationTargetVersion = G.nextRevision(G.currentRevision(C.getEntity(project, 'RULE-M002'))), dispositionTargetVersion = G.nextRevision(G.currentRevision(C.getEntity(project, 'RULE-M001')));
    setRuleSemantics(project, 'RULE-M002', {
      businessPurpose: '阻止无检验依据或已终止批次被异常登记动作继续流转。',
      outcomeSemantics: { REGISTER: { type: 'execute', businessAction: '创建质量异常并隔离受影响批次' }, REJECT: { type: 'reject', businessAction: '拒绝保存且不写入任何业务对象' } },
      exceptionHandling: '接口权限或状态校验失败返回 422；批次版本冲突返回 409，异常、批次与事件均不得部分写入。',
      comparisonNotes: '批次状态与检验依据均使用批准枚举值的精确相等比较；未提供的必要输入返回未知。'
    });
    setRuleSemantics(project, 'RULE-M001', {
      businessPurpose: '保证只有满足质量约束的批次才能返工、报废或放行。',
      outcomeSemantics: { SCRAP: { type: 'update_state', businessAction: '报废并终止批次' }, REWORK: { type: 'execute', businessAction: '返工后重新调查与复检' }, RELEASE: { type: 'execute', businessAction: '允许复检放行并更新批次状态' } },
      exceptionHandling: '缺陷等级、返工可行性或复检结论缺失时无法作出放行决定；规则冲突时要求质量负责人处理。',
      comparisonNotes: '缺陷等级、返工可行性和复检结论采用批准枚举值的精确相等比较。'
    });
    C.execute(project, 'SetProperty', { entityId: 'RULE-M002', path: 'sourceIds', value: ['SRC-M02'] });
    C.execute(project, 'SetProperty', { entityId: 'FEAT-M02', path: 'acceptance', value: clone(C.getEntity(project, 'REQ-M001')?.acceptance || []) });
    link(project, 'AST-M93-RULE-REG-SOURCE', 'RULE-M002', 'cites', 'SRC-M02', { locator: 'QD-2026-021 §3.2' }, [{ resourceId: 'SRC-M02', locator: '§3.2 保存与隔离' }]);
    add(project, 'ObservedBehavior', 'OBS-M-REGISTER', '异常登记保存事务的当前观察', '源码核验与事务回滚测试表明，符合前置条件的请求会同时写入异常、批次隔离和 outbox；冲突时无部分结果。', {
      factKind: 'observed_behavior', semanticRole: 'current_implementation_behavior', subjectId: 'FEAT-M02', observedVersion: 'commit-mq118', environment: 'production', method: 'source-inspection + transaction-test', input: { batchState: '生产中', evidence: 'complete', expectedVersion: 'matching' }, actualOutput: { defect: 'created', batchState: '质量隔离', outbox: 'created atomically', conflictResult: '409 / rollback' }, sourceVersion: 'commit-mq118', confidence: 'confirmed-by-test', applicableScope: '质量异常登记接口 API-M00', targetDecisionRef: { ruleId: 'RULE-M002', version: registrationTargetVersion }, conformance: { status: 'conformant', confirmation: 'confirmed', rationale: '已批准登记前置校验、原子隔离与当前测试观察一致。', targetRuleRevision: registrationTargetVersion, observationRevision: 'r1', codeRevision: 'commit-mq118', environment: 'production', evidenceRevisions: { 'SRC-M03': 'r1' }, confirmedBy: '质量负责人', comparison: { mode: 'deterministic-case', expectedOutcomeId: 'REGISTER', actualOutcomeId: 'REGISTER' } }, implementationEvidenceRefs: ['SRC-M03']
    }, { sourceIds: ['SRC-M03'] });
    add(project, 'ObservedBehavior', 'OBS-M-RELEASE', '批次放行当前实现观察', '实现核验记录表明，批次隔离、处置、复检使用同一批次号，放行记录包含检验时间；尚未覆盖所有目标规则组合。', {
      factKind: 'observed_behavior', semanticRole: 'current_implementation_behavior', subjectId: 'ACT-M009', observedVersion: 'MQ-101', environment: 'implementation-review', method: 'source-and-record-review', input: { scope: '已观察的放行路径' }, actualOutput: { batchCorrelation: 'same batchNo', releaseRecord: 'includes inspection time', coverage: 'partial' }, confidence: 'confirmed-partial', applicableScope: '已审查的放行路径', targetDecisionRef: { ruleId: 'RULE-M001', version: dispositionTargetVersion }, conformance: { status: 'partial', confirmation: 'partially-verified', rationale: '当前记录只覆盖部分放行路径，不能宣称所有规则组合均已验证。', targetRuleRevision: dispositionTargetVersion, observationRevision: 'r1', codeRevision: 'MQ-101', environment: 'implementation-review', evidenceRevisions: { 'SRC-M01': 'r1' }, confirmedBy: '质量负责人', comparison: { mode: 'manual-partial' } }, implementationEvidenceRefs: ['SRC-M01']
    }, { sourceIds: ['SRC-M01'] });
    caseEntity(project, 'CASE-M-REG-POS', '生产中且依据完整：允许登记并隔离', 'positive', 'RULE-M002', registrationTargetVersion, { batchState: '生产中', evidence: 'complete' }, { outcomeId: 'REGISTER', type: 'execute' }, ['SRC-M02']);
    caseEntity(project, 'CASE-M-REG-NEG', '已放行批次：拒绝重复登记', 'negative', 'RULE-M002', registrationTargetVersion, { batchState: '已放行', evidence: 'complete' }, { outcomeId: 'REJECT', type: 'reject' }, ['SRC-M02']);
    caseEntity(project, 'CASE-M-REG-BOUND', '检验依据缺失：拒绝且不写入', 'boundary', 'RULE-M002', registrationTargetVersion, { batchState: '生产中', evidence: 'missing' }, { outcomeId: 'REJECT', type: 'reject' }, ['SRC-M02']);
    caseEntity(project, 'CASE-M-REG-UNKNOWN', '未提供检验依据事实：无法判断', 'unknown', 'RULE-M002', registrationTargetVersion, { batchState: '生产中' }, { status: 'unknown', outcomeId: 'UNKNOWN' }, ['SRC-M02']);
    caseEntity(project, 'CASE-M-RELEASE-POS', '一般缺陷可返工且复检合格：允许放行', 'positive', 'RULE-M001', dispositionTargetVersion, { defect: 'minor', rework: 'feasible', recheck: 'pass' }, { outcomeId: 'RELEASE', type: 'execute' }, ['SRC-M01']);
    caseEntity(project, 'CASE-M-RELEASE-NEG', '严重缺陷：报废且不得放行', 'negative', 'RULE-M001', dispositionTargetVersion, { defect: 'critical', rework: 'feasible', recheck: 'pass' }, { outcomeId: 'SCRAP', type: 'update_state' }, ['SRC-M01']);
    caseEntity(project, 'CASE-M-RELEASE-BOUND', '一般缺陷不可返工：报废', 'boundary', 'RULE-M001', dispositionTargetVersion, { defect: 'minor', rework: 'unfeasible', recheck: 'pass' }, { outcomeId: 'SCRAP', type: 'update_state' }, ['SRC-M01']);
    caseEntity(project, 'CASE-M-RELEASE-UNKNOWN', '复检结论缺失：放行结果未知', 'unknown', 'RULE-M001', dispositionTargetVersion, { defect: 'minor', rework: 'feasible' }, { status: 'unknown', outcomeId: 'UNKNOWN' }, ['SRC-M01']);
    link(project, 'AST-M93-CASE-REG', 'CASE-M-REG-POS', 'references', 'RULE-M002', { role: 'positive-example' });
    link(project, 'AST-M93-OBS-REG', 'OBS-M-REGISTER', 'references', 'FEAT-M02', { role: 'observed-behavior' }, [{ resourceId: 'SRC-M03', locator: 'register.ts:88-146' }]);
    link(project, 'AST-M93-OBS-RELEASE', 'OBS-M-RELEASE', 'references', 'ACT-M009', { role: 'observed-behavior' }, [{ resourceId: 'SRC-M01', locator: 'MQ-101' }]);
    project.versionLayers.codeRevision = 'commit-mq118';
    project.versionLayers.deploymentVersion = 'mq118-production-acceptance';
    const baseline = publishMigration(project, 'V9.3 制造质量业务决策语义', '补充目标规范、当前观察、业务案例和符合性关系；旧基线保持不变。');
    project._v93SemanticsBaseline = baseline.id;
    delete project._v93OwnMigration;
    seedManufacturingDelivery(project, baseline.id);
  }

  function seedManufacturingDelivery(project, baselineId) {
    if (project.tasks.some(task => task.id === 'TASK-M93-DELIVERY')) return;
    const registrationVersion = C.getEntity(project, 'RULE-M002', { baselineId })?.currentVersion;
    const task = C.createTask(project, { id: 'TASK-M93-DELIVERY', rootId: 'REQ-M001', title: '异常登记事务核验与知识同步交付', purpose: 'development' });
    const pack = P.generateFormalContract(project, { taskId: task.id, rootId: task.rootId, baselineId, permission: { role: 'reader', clearance: 'restricted' }, context: W.SYSTEM_CONTEXT, purpose: 'development', taskIntent: task.title });
    task.semanticContract = {
      contractVersion: 'TSC/1.0', taskType: 'internal-refactor', strictness: 'simplified', baseBaselineId: baselineId,
      businessIntent: { goal: '统一异常登记事务核验记录并保持既有登记、隔离和事件行为。', problem: '实现证据与知识交付需要绑定同一代码和测试版本。', reason: '避免代码完成后项目知识失去实现对应关系。', requestedVersion: 'REQ-M93-001' },
      existingBehavior: { specificationRefs: [{ ruleId: 'RULE-M002', version: registrationVersion, baselineId }], observedBehaviorRefs: ['OBS-M-REGISTER'], conformance: 'conformant' },
      targetBehavior: { narrative: '保持已批准登记与隔离判断不变，仅收敛工程核验和知识回写。', decisionChanges: [], defaultAndException: `沿用 RULE-M002@${registrationVersion} 与 API-M00 已批准异常约定。`, applicableScope: 'REQ-M001 原子登记与隔离范围' },
      businessDifference: { added: ['实现、测试、知识交付记录的版本对账'], modified: ['工程核验记录组织方式'], removed: [], unchanged: ['异常登记前置校验', '批次原子隔离', '事务失败不留部分结果', '事件不直接触发放行'] },
      retainedConstraints: [{ ruleId: 'RULE-M002', version: registrationVersion, reason: '内部工程收敛不得改变已批准业务语义' }],
      acceptanceCaseRefs: ['CASE-M-REG-POS', 'CASE-M-REG-NEG', 'CASE-M-REG-BOUND', 'CASE-M-REG-UNKNOWN'],
      knowledgeDelivery: { createRefs: ['KDM-SIM-M93-REG'], updateRefs: [{ ruleId: 'RULE-M002', baseVersion: registrationVersion, proposedRevision: 'knowledge-proposal-only' }], preserveHistory: true, implementationVerificationRefs: ['TEST-SIM-M93-REG'], issueClosureRefs: [], targetBaselineStatus: 'pending-approval' }
    };
    const impacts = [
      ['REQ-M001', 'target-capability', 'required', 'done', '需求与验收绑定保持不变'],
      ['RULE-M002', 'business-rule', 'no-change', 'checked', '明确保留目标规范，仅引用版本'],
      ['API-M00', 'api-contract', 'no-change', 'checked', '已检查接口合同，无外部行为变化'],
      ['DATA-M02', 'data-semantics', 'no-change', 'checked', '已检查质量异常记录字段与事务，无数据语义变化'],
      ['DATA-M03', 'database-object', 'no-change', 'checked', '已检查批次状态与乐观锁，无结构变化'],
      ['EVT-M01', 'event', 'no-change', 'checked', '已检查 outbox 事件，不直接放行'],
      ['TEST-SIM-M93-REG', 'test', 'required', 'done', '模拟测试记录覆盖目标与回归案例'],
      ['KDM-SIM-M93-REG', 'knowledge', 'required', 'pending-knowledge-approval', '代码合并后清单已生成，知识提案仍待审批']
    ];
    task.impactInvestigation = { investigationVersion: 'IMPACT/1.0', generatedFrom: { baselineId, relationClosure: 'semantic-paths-only', allReachableAreImpacts: false }, items: impacts.map(([entityId, category, status, completion, rationale], index) => ({ id: `IMP-M93-${index + 1}`, entityId, category, status, completion, decision: status === 'required' ? 'modify-or-deliver' : 'checked-no-change', path: [{ from: 'REQ-M001', predicate: category === 'business-rule' ? 'constrains' : 'requires', to: entityId }], relationSemantics: category, evidenceRefs: entityId.startsWith('DATA') || entityId === 'API-M00' ? ['SRC-M03'] : ['SRC-M02'], rationale, checkedBy: '模拟开发 Agent / 人工复核' })), uncheckedCategories: [], legacyPathCheck: { status: 'checked', conclusion: '本次没有修改旧调用路径；夜间批处理未知仍保留在原问题中。' }, redundancyFindings: [] };
    task.delivery = {
      deliveryProtocol: 'V1.2', simulation: true, requirementVersion: 'REQ-M93-001', approvedBusinessBaselineId: baselineId,
      pullRequest: { id: 'PR-SIM-M93-REG', title: '模拟：统一登记事务核验与知识候选', status: 'merged', mergedAt: '2026-11-16T09:30:00.000Z', codeRevision: 'commit-mq118', candidateKnowledgeRefs: ['KC-SIM-M93-REG'] },
      implementation: { status: 'merged', codeRevision: 'commit-mq118', changeRefs: ['API-M00', 'DATA-M02', 'DATA-M03'], evidenceRefs: ['SRC-M03'] },
      tests: [{ id: 'TEST-SIM-M93-REG', status: 'passed', command: 'simulated: RegisterDefectTransactionTest + V9.3 decision cases', testedCodeRevision: 'commit-mq118', caseRefs: ['CASE-M-REG-POS', 'CASE-M-REG-NEG', 'CASE-M-REG-BOUND', 'CASE-M-REG-UNKNOWN'], evidence: '明确标注的模拟测试记录，不是实际后端执行日志' }],
      knowledgeChanges: [{ id: 'KC-SIM-M93-REG', status: 'proposed', kind: 'PR-candidate-knowledge', decisionRefs: [{ ruleId: 'RULE-M002', version: registrationVersion }], mayModifyApprovedBaseline: false }],
      deliveryManifest: { id: 'KDM-SIM-M93-REG', protocol: 'V1.2 Knowledge Delivery Manifest', status: 'generated-after-merge', codeRevision: 'commit-mq118', testRevision: 'TEST-SIM-M93-REG', knowledgeChangeRefs: ['KC-SIM-M93-REG'], approvedBaselineId: null, deploymentVersion: 'mq118-production-acceptance', reconciliation: 'knowledge-approval-pending' },
      acceptanceRecords: [{ id: 'ACC-SIM-M93-REG', status: 'passed', caseRefs: ['CASE-M-REG-POS', 'CASE-M-REG-NEG', 'CASE-M-REG-BOUND'], testRef: 'TEST-SIM-M93-REG', codeRevision: 'commit-mq118', simulation: true }]
    };
    task.deliveryState = 'implementation-complete-knowledge-pending';
    task.history.push({ at: C.now(), event: 'simulated-v12-delivery-attached', packageId: pack.packageId, manifestId: 'KDM-SIM-M93-REG' });
    project.knowledgeDeliveryManifests.push(task.delivery.deliveryManifest);
  }

  function createGoldenProject() {
    const project = C.createBlankProject(GOLDEN_PROJECT_ID, '遗留参数与搜索认知治理', '接管老项目时区分目标业务规范、当前观察和实现机制，再推动授权变更。');
    V9.initProject(project); initProject(project);
    project.profile.positioning = '接管老项目：从代码事实形成可信业务认知';
    project.profile.background = '两个认知黄金样本：可选参数分支、123 与 1234 搜索。所有缺失业务语义均明确保留为待确认。';
    project.profile.audience = '业务负责人、BA、开发、测试与接管工程师';
    project.profile.inScope = ['参数应用分支', '业务编号搜索', '目标与实现差异'];
    project.profile.outScope = ['未经批准自动选择匹配规则'];
    add(project, 'Resource', 'SRC-COG-LEGACY', '遗留系统分析记录 COG-93', '明确标注的模拟老项目资料，用于验证认知机制，不代表真实客户规范。', { resourceType: 'legacy-analysis-fixture', uri: 'atlas://cognitive-golden/COG-93', locator: { section: 'parameter and search observations' }, sourceVersion: 'legacy-search-2.4.1', summary: '参数存在性来自已确认分支说明；比较字段未知。搜索行为来自模拟运行观察，目标匹配规则未批准。', parseStatus: 'structured', owner: '接管分析组', trust: 'confirmed' });
    add(project, 'Actor', 'ACTOR-COG-USER', '业务查询人员', '输入业务条件并期望得到可解释的处理结果。');
    add(project, 'Feature', 'FEAT-COG-PARAM', '应用可选参数', '在提供 parameterName 时进入参数应用分支，未提供时继续默认处理；具体业务比较字段和比较方式仍待确认。', { businessGoal: '根据是否提供可选参数选择已确认的处理分支。', primaryActorIds: ['ACTOR-COG-USER'], finalBusinessResult: '应用参数或继续默认处理，不在未知比较依据时虚构结果。', observedBehaviorRefs: [], implementationEvidenceRefs: [] }, { sourceIds: ['SRC-COG-LEGACY'], acceptance: ['提供参数时进入应用分支', '未提供参数时进入默认处理', '比较依据未知时明确待确认'] });
    const parameterVersion = {
      version: 'draft', status: 'draft', factKind: 'target_specification', semanticRole: 'business_decision', businessPurpose: '仅根据可选参数是否存在选择处理分支。',
      scope: '遗留参数应用入口；只覆盖存在性分支，不覆盖参数值与业务字段的比较。', supportsEvaluation: true,
      inputs: [{ id: 'parameterName', label: '可选参数名称', businessMeaning: '调用方提供的 parameterName；当前仅批准其存在性语义', kind: 'string', required: false, source: '调用上下文' }],
      preconditions: [], outcomes: [{ id: 'APPLY', label: '进入参数应用分支', type: 'execute' }, { id: 'DEFAULT', label: '继续默认处理', type: 'no_action' }],
      rows: [{ id: 'PARAM-01', priority: 10, condition: { op: 'exists', inputId: 'parameterName' }, outcomeId: 'APPLY' }],
      defaultOutcome: 'DEFAULT', exceptions: [], exceptionHandling: '参数存在但后续比较依据未知时，不在本规则内推断匹配结果。', priorityPolicy: { strategy: 'single-approved-row-then-default', confirmed: true, source: 'SRC-COG-LEGACY §parameter' }, summary: '提供 parameterName 时进入参数应用分支；未提供或为 null 时继续默认处理。'
    };
    C.execute(project, 'DefineRule', { entityId: 'RULE-COG-PARAM', name: '参数存在性分支', scope: parameterVersion.scope, description: '只表达已批准的存在性判断；不推断具体比较字段。', definition: parameterVersion, owner: '业务负责人', sourceIds: ['SRC-COG-LEGACY'] });
    caseEntity(project, 'CASE-COG-PARAM-POS', '提供参数名：进入应用分支', 'positive', 'RULE-COG-PARAM', 'r1', { parameterName: 'customerId' }, { outcomeId: 'APPLY', type: 'execute' }, ['SRC-COG-LEGACY']);
    caseEntity(project, 'CASE-COG-PARAM-NEG', '未提供参数名：继续默认处理', 'negative', 'RULE-COG-PARAM', 'r1', {}, { outcomeId: 'DEFAULT', type: 'no_action' }, ['SRC-COG-LEGACY']);
    caseEntity(project, 'CASE-COG-PARAM-BOUND', '空字符串仍是一个已提供值：进入应用分支', 'boundary', 'RULE-COG-PARAM', 'r1', { parameterName: '' }, { outcomeId: 'APPLY', type: 'execute' }, ['SRC-COG-LEGACY']);
    add(project, 'Component', 'COMP-COG-PARAM-OLD', '旧参数分支兼容实现', '源码中存在两套参数分支适配器；是否删除需完成调用链与兼容调查。', { factKind: 'implementation_mechanism', semanticRole: 'technical_mechanism', mechanism: 'LegacyParameterAdapter + CompatibleParameterAdapter', codePaths: ['legacy/parameter/adapter.js'], redundancyStatus: 'duplicate-candidate', currentCodeVersion: 'legacy-2.4.1' }, { sourceIds: ['SRC-COG-LEGACY'] });
    link(project, 'AST-COG-PARAM-FEATURE', 'FEAT-COG-PARAM', 'uses', 'RULE-COG-PARAM', { condition: '参数应用分支' }, [{ resourceId: 'SRC-COG-LEGACY', locator: '§parameter branch' }]);
    link(project, 'AST-COG-PARAM-OLD', 'FEAT-COG-PARAM', 'uses', 'COMP-COG-PARAM-OLD', { role: 'implementation-mechanism' }, [{ resourceId: 'SRC-COG-LEGACY', locator: 'legacy/parameter/adapter.js' }]);
    link(project, 'AST-COG-PARAM-CASE', 'CASE-COG-PARAM-POS', 'references', 'RULE-COG-PARAM', { role: 'positive-example' });

    add(project, 'Feature', 'FEAT-COG-SEARCH', '业务编号搜索', '帮助业务查询人员定位记录。当前系统观察可由词元匹配解释，但目标业务匹配规则尚未批准。', { businessGoal: '根据业务编号定位目标记录。', primaryActorIds: ['ACTOR-COG-USER'], finalBusinessResult: '返回符合已批准匹配语义的记录；目标语义当前未知。', targetSpecificationStatus: 'missing', observedBehaviorRefs: ['OBS-COG-SEARCH'], implementationEvidenceRefs: ['SRC-COG-LEGACY'] }, { sourceIds: ['SRC-COG-LEGACY'], acceptance: ['展示 123 与 1234 的当前观察差异', '目标匹配语义未确认时不得自动批准'] });
    add(project, 'API', 'API-COG-SEARCH', '遗留业务记录搜索接口', '当前提供编号搜索能力；接口存在不代表目标匹配语义已经批准。', { method: 'GET', path: '/legacy/records/search', input: { query: '用户输入的编号片段' }, output: { records: '当前实现返回的记录集合' }, failure: '无结果返回空集合；语义是否正确的业务符合性尚未验证', codeVersion: 'legacy-search-2.4.1' }, { sourceIds: ['SRC-COG-LEGACY'] });
    add(project, 'Component', 'COMP-COG-SEARCH', '全文词元匹配器', '当前搜索通过全文词元机制执行；这是技术实现机制，不是目标业务规则。', { factKind: 'implementation_mechanism', semanticRole: 'technical_mechanism', mechanism: 'full-text token matching', technicalDetail: 'FTS tokenizer / token index / CONTAINS query', codePaths: ['legacy/search/token-query.sql'], currentCodeVersion: 'legacy-search-2.4.1', businessMeaningApproval: 'not-approved' }, { sourceIds: ['SRC-COG-LEGACY'] });
    add(project, 'ObservedBehavior', 'OBS-COG-SEARCH', '123 与 1234 的当前搜索观察', '模拟运行测试观察到，查询 123 与查询 1234 返回不同结果；这只能说明当前行为。', { factKind: 'observed_behavior', semanticRole: 'current_implementation_behavior', subjectId: 'FEAT-COG-SEARCH', observedVersion: 'legacy-search-2.4.1', environment: 'legacy-test-fixture', method: 'simulated-runtime-observation', input: { documents: [{ id: 'DOC-123', token: '123' }, { id: 'DOC-1234', token: '1234' }], queries: ['123', '1234'] }, actualOutput: { '123': { matched: ['DOC-123'], notMatched: ['DOC-1234'] }, '1234': { matched: ['DOC-1234'], notMatched: ['DOC-123'] } }, confidence: 'confirmed-observation-only', applicableScope: '指定模拟数据集与 legacy-search-2.4.1，不外推其他数据或版本', targetDecisionRef: null, conformance: { status: 'missing_target', confirmation: 'unverified', rationale: '缺少已批准目标匹配规范，无法判断当前行为是否符合业务。' }, implementationEvidenceRefs: ['SRC-COG-LEGACY'] }, { sourceIds: ['SRC-COG-LEGACY'] });
    link(project, 'AST-COG-SEARCH-OBS', 'OBS-COG-SEARCH', 'references', 'FEAT-COG-SEARCH', { role: 'observed-behavior' }, [{ resourceId: 'SRC-COG-LEGACY', locator: '§search runtime observation' }]);
    link(project, 'AST-COG-SEARCH-API', 'API-COG-SEARCH', 'implements', 'FEAT-COG-SEARCH', { role: 'current-api' }, [{ resourceId: 'SRC-COG-LEGACY', locator: 'legacy search endpoint' }]);
    link(project, 'AST-COG-SEARCH-COMP', 'API-COG-SEARCH', 'dependsOn', 'COMP-COG-SEARCH', { role: 'token-matching-mechanism' }, [{ resourceId: 'SRC-COG-LEGACY', locator: 'token-query.sql' }]);
    project.unknowns.push(
      { id: 'UNK-COG-PARAM', title: 'parameterName 与哪些业务字段按何种语义比较尚未确认', impact: '只能确认存在性分支，不能生成后续确定匹配结果。', relatedIds: ['FEAT-COG-PARAM', 'RULE-COG-PARAM'], blocksFormal: false, status: 'open', owner: '业务负责人' },
      { id: 'UNK-COG-SEARCH', title: '搜索应采用精确、前缀还是任意子串匹配', impact: '影响用户可见搜索结果、索引策略、兼容测试和目标验收。', relatedIds: ['FEAT-COG-SEARCH', 'OBS-COG-SEARCH'], blocksFormal: true, status: 'open', owner: '业务负责人' }
    );
    project.versionLayers.codeRevision = 'legacy-search-2.4.1';
    project.versionLayers.deploymentVersion = 'legacy-search-2.4.1-test';
    const baseline = publishMigration(project, 'V9.3 认知黄金样本基线', '建立参数分支与搜索观察样本；技术观察未自动批准为目标业务规范。');
    project._v93SemanticsBaseline = baseline.id;
    project.decisionRecords.push({ decisionId: 'DEC-COG-SEARCH-MATCH', taskId: 'TASK-COG-SEARCH', knowledgeRefs: ['FEAT-COG-SEARCH', 'OBS-COG-SEARCH'], question: '业务编号搜索应采用哪一种匹配规则？', evidenceRefs: ['OBS-COG-SEARCH', 'COMP-COG-SEARCH'], options: [{ id: 'exact', label: '精确匹配', businessResult: '123 只返回编号 123，不返回 1234', compatibilityImpact: '可能缩小当前结果范围' }, { id: 'prefix', label: '前缀匹配', businessResult: '123 可返回 123 与 1234，但不匹配任意中间片段', compatibilityImpact: '扩大当前词元结果，需要明确排序与空值处理' }, { id: 'substring', label: '任意子串匹配', businessResult: '编号任意位置片段均可命中', compatibilityImpact: '结果范围最大，可能增加误命中与索引成本' }], recommendation: { optionId: 'prefix', rationale: '作为待批准建议：兼顾编号定位与分段输入习惯；需先通过真实用户场景和兼容回归验证。', recommendedBy: '模拟 BA / Agent 分析', notApproval: true }, decisionOwner: '业务负责人', approval: null, effectiveScope: null, impactedKnowledgeAndImplementation: ['FEAT-COG-SEARCH', 'API-COG-SEARCH', 'COMP-COG-SEARCH'], version: 'proposal-r1' });
    const task = C.createTask(project, { id: 'TASK-COG-SEARCH', rootId: 'FEAT-COG-SEARCH', title: '明确并调整业务编号搜索判断条件', purpose: 'development' });
    task.semanticContract = {
      contractVersion: 'TSC/1.0', taskType: 'business-rule-change', strictness: 'complete', baseBaselineId: baseline.id,
      businessIntent: { goal: '让用户按照明确且批准的编号匹配语义找到记录。', problem: '当前词元行为与目标业务规则之间没有批准关系。', reason: '避免把技术现状误当作业务目标。', requestedVersion: 'REQ-COG-SEARCH-001' },
      existingBehavior: { specificationRefs: [], observedBehaviorRefs: ['OBS-COG-SEARCH'], conformance: 'missing_target' },
      targetBehavior: { narrative: '候选：采用前缀匹配；只有业务负责人批准后才成为目标规范。', decisionChanges: [{ proposalId: 'DEC-COG-SEARCH-MATCH', ruleId: null, baseVersion: null, baseBaselineId: baseline.id, proposedRevision: 'proposal-r1', operation: 'create-target-decision', approvalStatus: 'proposed', comparison: { op: 'startsWith', caseSensitive: false, nullHandling: 'not-match', multiMatchHandling: '按批准排序返回，具体排序待确认' } }], defaultAndException: '无输入、空值、多结果排序和异常行为仍需批准。', applicableScope: '业务编号搜索；组织与环境范围待确认' },
      businessDifference: { added: ['目标匹配规则（候选）'], modified: ['123 对 1234 的预期命中关系（待批准）'], removed: [], unchanged: ['保留当前观察与实现证据作为历史，不删除来源'] },
      retainedConstraints: [{ entityId: 'OBS-COG-SEARCH', reason: '当前观察必须保留，不得改写成批准规范' }],
      acceptanceCaseRefs: [], knowledgeDelivery: { createRefs: ['DEC-COG-SEARCH-MATCH'], updateRefs: ['API-COG-SEARCH', 'COMP-COG-SEARCH'], preserveHistory: true, implementationVerificationRefs: [], issueClosureRefs: ['Q-COG-SEARCH'], targetBaselineStatus: 'not-approved' }
    };
    task.impactInvestigation = { investigationVersion: 'IMPACT/1.0', generatedFrom: { baselineId: baseline.id, relationClosure: 'authorized-semantic-paths', allReachableAreImpacts: false }, items: [
      { id: 'IMP-COG-SEARCH-1', entityId: 'FEAT-COG-SEARCH', category: 'target-feature', status: 'required', completion: 'pending', decision: 'define-target', path: [{ from: 'TASK-COG-SEARCH', predicate: 'changes', to: 'FEAT-COG-SEARCH' }], relationSemantics: '目标功能', evidenceRefs: ['SRC-COG-LEGACY'], rationale: '首先明确目标行为，不是只修改 SQL 参数。' },
      { id: 'IMP-COG-SEARCH-2', entityId: 'API-COG-SEARCH', category: 'api-contract', status: 'potential', completion: 'unknown', decision: 'investigate', path: [{ from: 'FEAT-COG-SEARCH', predicate: 'implemented-by', to: 'API-COG-SEARCH' }], relationSemantics: '业务功能到当前 API 实现', evidenceRefs: ['SRC-COG-LEGACY'], rationale: '可能需要复核输入、输出、空值和多结果行为，不能仅因可达就断言必须修改。' },
      { id: 'IMP-COG-SEARCH-3', entityId: 'COMP-COG-SEARCH', category: 'backend-and-database', status: 'potential', completion: 'unknown', decision: 'investigate', path: [{ from: 'API-COG-SEARCH', predicate: 'dependsOn', to: 'COMP-COG-SEARCH' }], relationSemantics: '技术依赖，不是执行流程', evidenceRefs: ['SRC-COG-LEGACY'], rationale: '目标批准后再决定保留词元索引、增加前缀索引或收敛实现。' },
      { id: 'IMP-COG-SEARCH-4', entityId: null, category: 'tests-and-regression', status: 'unknown', completion: 'unknown', decision: 'investigate', path: [], relationSemantics: '必要调查维度', evidenceRefs: [], rationale: '需要精确、前缀、子串、空值、大小写、多结果和 123/1234 回归案例。' },
      { id: 'IMP-COG-SEARCH-5', entityId: null, category: 'legacy-compatibility-deployment', status: 'unknown', completion: 'unknown', decision: 'investigate', path: [], relationSemantics: '旧路径、兼容、部署与迁移', evidenceRefs: [], rationale: '尚未确认调用方、旧查询路径、索引迁移和部署回滚约束。' }
    ], uncheckedCategories: ['frontend-input-state', 'parameter-passing', 'event', 'configuration'], legacyPathCheck: { status: 'unknown', conclusion: '尚未完成调用方与旧路径调查。' }, redundancyFindings: [] };
    const issue = C.createIssue(project, { id: 'Q-COG-SEARCH', taskId: task.id, objectId: 'FEAT-COG-SEARCH', title: '搜索目标匹配规则需要业务批准', location: 'FEAT-COG-SEARCH / DEC-COG-SEARCH-MATCH', impact: '未批准前只能展示当前观察，不能宣称前缀匹配已经生效。', assignee: '业务负责人', sourceRefs: [{ resourceId: 'SRC-COG-LEGACY', locator: '§search target decision' }] });
    issue.decisionId = 'DEC-COG-SEARCH-MATCH'; issue.sensitivity = 'restricted';
    task.deliveryState = 'business-decision-pending';
    return project;
  }

  function initProjects(projects) {
    Object.values(projects || {}).forEach(initProject);
    const manufacturing = projects['PRJ-00003']; if (manufacturing) seedManufacturing(manufacturing);
    if (!projects[GOLDEN_PROJECT_ID]) projects[GOLDEN_PROJECT_ID] = createGoldenProject();
    else seedExistingGolden(projects[GOLDEN_PROJECT_ID]);
    const demoTask = manufacturing?.tasks?.find(task => task.id === 'TASK-M93-DELIVERY');
    if (demoTask && !demoTask.activeContractId) {
      const pack = P.generateFormalContract(manufacturing, { taskId: demoTask.id, rootId: demoTask.rootId, baselineId: manufacturing.versionLayers.baseline, permission: { role: 'reader', clearance: 'restricted' }, context: W.SYSTEM_CONTEXT, purpose: 'development', taskIntent: demoTask.title });
      if (pack.ok) demoTask.deliveryState = 'implementation-complete-knowledge-pending';
    }
    return projects;
  }
  function seedExistingGolden(project) { /* Additive migrations never rewrite an existing approved baseline. */ project._v93SemanticsBaseline ||= project.versionLayers.baseline; }

  return { GOLDEN_PROJECT_ID, initProject, initProjects, createGoldenProject, seedManufacturing, caseEntity };
});
