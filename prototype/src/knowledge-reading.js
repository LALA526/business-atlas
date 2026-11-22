/* Business Atlas V9.3 — authorized business decision, conformance and flow projections. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const P = commonjs ? require('./v6-projection.js') : root.AtlasV6Projection;
  const R = commonjs ? require('./v8-read.js') : root.AtlasV8Read;
  const Access = commonjs ? require('./v9-access.js') : root.AtlasV9Access;
  const D = commonjs ? require('./decision.js') : root.AtlasV93Decision;
  const api = factory(C, P, R, Access, D);
  if (commonjs) module.exports = api;
  root.AtlasV93Reading = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (C, P, R, Access, D) {
  'use strict';
  const clone = C.clone;
  const TECHNICAL_TYPES = new Set(['Surface', 'API', 'Component', 'Module', 'System', 'DataEntity', 'Configuration', 'Framework', 'Dependency', 'DeploymentUnit', 'BuildPipeline', 'Technology', 'Runtime', 'Event']);
  function optionsFor(ctx) { return ctx.versionMode === 'draft' ? { draft: true, permission: ctx.permissionContext, applicableContext: ctx.applicableContext, applicabilityMode: ctx.applicabilityMode, readingTask: ctx.readPurpose } : { baselineId: ctx.baselineId, permission: ctx.permissionContext, applicableContext: ctx.applicableContext, applicabilityMode: ctx.applicabilityMode, readingTask: ctx.readPurpose }; }
  function unique(items) { return [...new Map((items || []).filter(Boolean).map(item => [item.id, item])).values()]; }
  function nameOf(project, ctx, id) { return Access.readEntity(project, ctx, id).data?.name || null; }
  function relatedEntities(projection, id, predicates = []) { return P.neighbors(projection, id).filter(item => !predicates.length || predicates.includes(item.assertion.predicate)).map(item => item.entity).filter(Boolean); }
  function collectRuleReferences(project, projection, root) {
    const candidates = new Map(), add = (entity, via) => { if (entity?.typeId === 'Rule') candidates.set(entity.id, { entity, via }); };
    relatedEntities(projection, root.id, ['constrains', 'blocks', 'uses', 'affects', 'appliesTo']).forEach(entity => add(entity, 'direct'));
    const rootActionIds = new Set(relatedEntities(projection, root.id, ['implements', 'triggers', 'contains', 'uses']).filter(entity => entity.typeId === 'Action').map(entity => entity.id));
    rootActionIds.forEach(actionId => relatedEntities(projection, actionId, ['constrains', 'blocks', 'uses', 'affects', 'appliesTo']).forEach(entity => add(entity, `action:${actionId}`)));
    (projection.flows || []).forEach(flow => {
      const includeWholeFlow = root.typeId === 'Scenario' || (candidates.size === 0 && root.props?.scenarioId === flow.scenarioId);
      const relevantNodeIds = new Set(flow.nodes.filter(node => includeWholeFlow || rootActionIds.has(node.actionId)).map(node => node.id));
      flow.nodes.filter(node => node.ruleId && relevantNodeIds.has(node.id)).forEach(node => add(C.getEntity(project, node.ruleId, projection.options), `flow-node:${node.id}`));
      flow.edges.filter(edge => edge.when?.ruleId && (relevantNodeIds.has(edge.from) || relevantNodeIds.has(edge.to))).forEach(edge => add(C.getEntity(project, edge.when.ruleId, projection.options), `flow-edge:${edge.id}`));
    });
    projection.nodes.filter(node => node.entity.typeId === 'BusinessObject').forEach(node => (node.entity.transitions || []).forEach(transition => {
      const ruleId = transition.guardRuleId || transition.decisionRef?.ruleId;
      if (ruleId) add(C.getEntity(project, ruleId, projection.options), `state-guard:${transition.id}`);
    }));
    return [...candidates.values()];
  }

  function ruleView(project, ctx, rule) {
    const version = C.ruleVersion(rule), rows = [...(version?.exceptions || []).map(row => ({ ...row, exception: true })), ...(version?.rows || [])];
    return {
      entity: rule, version, factKind: version?.factKind || rule.factKind || 'target_specification', semanticRole: version?.semanticRole || rule.semanticRole || 'business_decision',
      approved: D.isApprovedTarget(rule, version, ctx), completeness: D.semanticCompleteness(rule), priority: D.priorityFor(version, rows),
      rows: rows.map(row => ({ ...clone(row), condition: D.conditionFromRow(row), explanation: D.describeCondition(D.conditionFromRow(row), version), outcome: (() => { const outcome = (version?.outcomes || []).find(outcome => outcome.id === row.outcomeId); return outcome ? { type: 'unknown', ...clone(outcome), ...(version?.outcomeSemantics?.[outcome.id] || {}) } : null; })() })),
      outcomes: (version?.outcomes || []).map(outcome => ({ type: 'unknown', ...clone(outcome), ...(version?.outcomeSemantics?.[outcome.id] || {}) })),
      defaultOutcome: version?.defaultOutcome || version?.default || null,
      exceptionHandling: version?.exceptionHandling || ((version?.exceptions || []).length ? `${version.exceptions.length} 条例外规则` : '未记录专门异常；不能据此推断没有异常'),
      sources: R.getSources(project, ctx, rule.id).map(ref => ({ resourceId: ref.resourceId, name: ref.entity?.name || ref.confirmedBy || '人工确认', locator: ref.locator, sourceVersion: ref.entity?.props?.sourceVersion || null, trust: ref.entity?.props?.trust || 'human-confirmation' }))
    };
  }

  function conformanceFor(project, ctx, root, rules, observations, mechanisms) {
    const currentCodeVersion = project.versionLayers?.codeRevision || null;
    if (!rules.length) return { status: 'missing_target', code: 'MISSING_TARGET_SPECIFICATION', label: '缺少已批准目标规范', explanation: observations.length || mechanisms.length ? '当前只有实现观察或技术机制，不能据此批准目标业务规则。' : '尚未建立目标业务规范，也没有足够实现观察。', observedVersion: observations[0]?.props?.observedVersion || null, currentCodeVersion, targetRefs: [], observationRefs: observations.map(item => item.id), implementationEvidenceRefs: mechanisms.flatMap(item => item.sourceIds || []) };
    const assessments = rules.map(ruleViewItem => {
      const version = ruleViewItem.version?.version, allForRule = observations.filter(observation => observation.props?.targetDecisionRef?.ruleId === ruleViewItem.entity.id), matching = allForRule.filter(observation => !observation.props.targetDecisionRef.version || observation.props.targetDecisionRef.version === version);
      if (!allForRule.length) return { ruleId: ruleViewItem.entity.id, ruleVersion: version, status: 'missing_observation', code: 'MISSING_OBSERVED_BEHAVIOR', label: '缺少当前实际观察', explanation: '存在目标规范和技术映射，但没有可对应的当前行为观察，尚未验证实现符合性。', currentCodeVersion, observationRefs: [] };
      if (!matching.length) return { ruleId: ruleViewItem.entity.id, ruleVersion: version, status: 'requires_reverification', code: 'TARGET_VERSION_CHANGED', label: '目标规则已变化，需重新核验', explanation: `当前目标为 ${version}，已有观察绑定 ${[...new Set(allForRule.map(item => item.props.targetDecisionRef.version || '未指定版本'))].join('、')}，旧结论不能沿用。`, currentCodeVersion, observationRefs: allForRule.map(item => item.id) };
      const details = matching.map(observation => {
        const props = observation.props || {}, observedVersion = props.observedVersion || props.sourceVersion || null, explicit = props.conformance || {};
        const stale = (code, explanation) => ({ observationId: observation.id, status: 'requires_reverification', code, label: '依据已变化，需重新核验', explanation, observedVersion, currentCodeVersion, targetRef: clone(props.targetDecisionRef), observationRevision: observation.revision, targetRuleRevision: ruleViewItem.entity.revision, evidenceRevisions: clone(explicit.evidenceRevisions || {}) });
        if (currentCodeVersion && observedVersion && currentCodeVersion !== observedVersion) return stale('CODE_VERSION_CHANGED', `观察版本 ${observedVersion} 与当前代码版本 ${currentCodeVersion} 不同，旧符合性结论不能自动沿用。`);
        if (props.factKind === 'inferred' || props.confirmationStatus === 'unverified' || props.confidence === 'inferred') return { observationId: observation.id, status: 'unverified', code: 'INFERENCE_NOT_CONFIRMED', label: '观察推断尚未确认', explanation: '推断或技术分析不能显示为已确认业务行为。', observedVersion, currentCodeVersion };
        const bindingComplete = explicit.targetRuleRevision && explicit.observationRevision && explicit.codeRevision && explicit.environment && explicit.evidenceRevisions && explicit.confirmedBy;
        if (!bindingComplete) return { observationId: observation.id, status: 'unverified', code: 'CONFORMANCE_BINDING_MISSING', label: '符合性依据不完整', explanation: '旧标签没有完整绑定目标修订、观察修订、代码版本、环境、证据修订和确认人，不能继续作为当前一致性的充分依据。', observedVersion, currentCodeVersion, targetRef: clone(props.targetDecisionRef) };
        if (explicit.targetRuleRevision !== ruleViewItem.entity.revision) return stale('TARGET_REVISION_CHANGED', `符合性绑定目标修订 ${explicit.targetRuleRevision}，当前为 ${ruleViewItem.entity.revision}。`);
        if (explicit.observationRevision !== observation.revision) return stale('OBSERVATION_REVISION_CHANGED', `符合性绑定观察修订 ${explicit.observationRevision}，当前为 ${observation.revision}。`);
        if (explicit.codeRevision !== currentCodeVersion) return stale('CONFORMANCE_CODE_CHANGED', `符合性确认基于代码 ${explicit.codeRevision}，当前为 ${currentCodeVersion || '未声明'}。`);
        if (explicit.environment !== ctx.applicableContext?.environment) return stale('ENVIRONMENT_CHANGED', `符合性确认环境为 ${explicit.environment}，当前阅读环境为 ${ctx.applicableContext?.environment || '未声明'}。`);
        for (const [evidenceId, revision] of Object.entries(explicit.evidenceRevisions || {})) {
          const evidence = Access.readEntity(project, ctx, evidenceId);
          if (!evidence.ok) return stale('EVIDENCE_UNAVAILABLE', `符合性证据 ${evidenceId} 在当前权限或基线下不可读。`);
          if (evidence.data.revision !== revision) return stale('EVIDENCE_REVISION_CHANGED', `证据 ${evidence.data.name} 已从 ${revision} 变为 ${evidence.data.revision}。`);
        }
        const comparison = explicit.comparison || {};
        if (comparison.mode === 'deterministic-case') {
          const evaluation = D.evaluateRule(project, ctx, ruleViewItem.entity.id, props.input || {}, { version });
          const actual = comparison.actualOutcomeId;
          if (evaluation.outcome?.id !== comparison.expectedOutcomeId || actual !== comparison.expectedOutcomeId) return { observationId: observation.id, status: 'inconsistent', code: 'DETERMINISTIC_MISMATCH', label: '已确认不一致', explanation: `目标结果 ${comparison.expectedOutcomeId}，规则评估 ${evaluation.outcome?.id || evaluation.code}，观察结果 ${actual || '未记录'}。`, observedVersion, currentCodeVersion, evaluation, targetRef: clone(props.targetDecisionRef) };
        }
        const status = explicit.status === 'conformant' ? 'consistent' : explicit.status === 'nonconformant' ? 'inconsistent' : explicit.status === 'partial' ? 'partial' : 'unverified';
        const labels = { consistent: '已确认一致', inconsistent: '已确认不一致', partial: '仅部分适用', unverified: '尚未验证' };
        return { observationId: observation.id, status, code: explicit.status ? explicit.status.toUpperCase() : 'UNVERIFIED', label: labels[status], explanation: `${explicit.rationale || '尚未记录符合性理由。'} 已核对目标修订、观察修订、代码、环境和证据版本。`, observedVersion, currentCodeVersion, targetRef: clone(props.targetDecisionRef), observationRevision: observation.revision, targetRuleRevision: ruleViewItem.entity.revision, environment: explicit.environment, confirmedBy: explicit.confirmedBy, implementationEvidenceRefs: clone(props.implementationEvidenceRefs || []) };
      });
      const rank = { inconsistent: 6, requires_reverification: 5, missing_observation: 4, unverified: 3, partial: 2, missing_target: 1, consistent: 0 };
      const worst = details.slice().sort((a, b) => rank[b.status] - rank[a.status])[0];
      return { ruleId: ruleViewItem.entity.id, ruleVersion: version, ...worst, observations: details, observationRefs: matching.map(item => item.id) };
    });
    const rank = { inconsistent: 6, requires_reverification: 5, missing_observation: 4, unverified: 3, partial: 2, missing_target: 1, consistent: 0 };
    const overall = assessments.slice().sort((a, b) => rank[b.status] - rank[a.status])[0];
    return { ...overall, assessments, targetRefs: rules.map(item => ({ ruleId: item.entity.id, version: item.version?.version, baselineId: ctx.baselineId })), mechanismRefs: mechanisms.map(item => item.id) };
  }

  function decisionProjection(project, ctx, rootId) {
    const rootRead = Access.readEntity(project, ctx, rootId);
    if (!rootRead.ok) return { ok: false, code: rootRead.code, root: null, targetRules: [], observations: [], mechanisms: [], cases: [] };
    const root = rootRead.data, options = optionsFor(ctx), projection = P.projectKnowledge(project, rootId, { ...options, unlimited: true });
    if (!projection.ok) return { ok: false, code: projection.code, root: null, targetRules: [], observations: [], mechanisms: [], cases: [] };
    const referenced = collectRuleReferences(project, projection, root), ruleMap = new Map();
    if (root.typeId === 'Rule') ruleMap.set(root.id, root);
    referenced.forEach(item => ruleMap.set(item.entity.id, item.entity));
    (P.focusFacts(project, projection, root.id)?.rules || []).forEach(rule => ruleMap.set(rule.id, rule));
    const targetRules = [...ruleMap.values()].map(rule => ruleView(project, ctx, rule));
    const targetRuleIds = new Set(targetRules.map(item => item.entity.id));
    const scopedActionIds = new Set(root.typeId === 'Scenario' ? projection.flows.flatMap(flow => flow.nodes.map(node => node.actionId)).filter(Boolean) : P.neighbors(projection, root.id).filter(item => ['implements', 'triggers', 'contains', 'uses'].includes(item.assertion.predicate) && item.entity?.typeId === 'Action').map(item => item.entity.id));
    const semanticIds = new Set([root.id, ...targetRuleIds, ...scopedActionIds]);
    projection.flows.forEach(flow => flow.nodes.filter(node => root.typeId === 'Scenario' || scopedActionIds.has(node.actionId)).forEach(node => ['actionId', 'ruleId', 'eventId'].forEach(key => { if (node[key]) semanticIds.add(node[key]); })));
    const allAuthorized = Access.listEntities(project, ctx).data;
    const observations = allAuthorized.filter(entity => entity.typeId === 'ObservedBehavior').filter(entity => {
      const props = entity.props || {};
      return entity.id === root.id || semanticIds.has(props.subjectId) || targetRuleIds.has(props.targetDecisionRef?.ruleId) || (root.props?.observedBehaviorRefs || []).includes(entity.id);
    });
    observations.forEach(observation => semanticIds.add(observation.id));
    const cases = allAuthorized.filter(entity => entity.typeId === 'BusinessCase').filter(entity => targetRuleIds.has(entity.props?.decisionRef?.ruleId) || entity.props?.subjectId === root.id);
    const scopedIds = new Set([root.id, ...scopedActionIds, ...targetRuleIds]), mechanismIds = new Set();
    const addTechnicalRelations = ids => projection.assertions.forEach(assertion => {
      if (ids.has(assertion.subject) && assertion.object) { const entity = projection.nodes.find(node => node.entity.id === assertion.object)?.entity; if (entity && (TECHNICAL_TYPES.has(entity.typeId) || entity.props?.factKind === 'implementation_mechanism')) mechanismIds.add(entity.id); }
      if (ids.has(assertion.object)) { const entity = projection.nodes.find(node => node.entity.id === assertion.subject)?.entity; if (entity && (TECHNICAL_TYPES.has(entity.typeId) || entity.props?.factKind === 'implementation_mechanism')) mechanismIds.add(entity.id); }
    });
    addTechnicalRelations(scopedIds); addTechnicalRelations(mechanismIds);
    const mechanisms = unique([root, ...projection.nodes.map(node => node.entity)].filter(entity => entity.id === root.id && TECHNICAL_TYPES.has(entity.typeId) || mechanismIds.has(entity.id) || entity.factKind === 'implementation_mechanism' && mechanismIds.has(entity.id)));
    const actorIds = new Set([...(root.props?.primaryActorIds || []), ...(root.participants || []), ...(root.actorIds || []), ...(root.actorId ? [root.actorId] : [])]);
    scopedActionIds.forEach(actionId => { const action = Access.readEntity(project, ctx, actionId).data; if (action?.actorId) actorIds.add(action.actorId); });
    const actors = [...actorIds].map(id => Access.readEntity(project, ctx, id).data).filter(Boolean);
    const visibleKnowledge = new Set(allAuthorized.map(entity => entity.id));
    const relatedIds = new Set([root.id, ...targetRuleIds, ...observations.map(item => item.id), ...mechanisms.map(item => item.id)]);
    const unknowns = (project.unknowns || []).filter(item => item.status !== 'resolved' && (item.relatedIds || []).some(id => relatedIds.has(id) && visibleKnowledge.has(id))).map(clone);
    const authorizedAssertionIds = new Set(projection.assertions.map(assertion => assertion.assertionId));
    const conflicts = (project.conflictRecords || []).filter(item => item.status !== 'resolved' && (item.assertionIds || []).some(id => authorizedAssertionIds.has(id))).map(clone);
    const conformance = conformanceFor(project, ctx, root, targetRules, observations, mechanisms);
    const flows = projection.flows.map(flow => flowModel(project, ctx, projection, flow));
    const sources = unique([...targetRules.flatMap(item => item.sources.map(source => ({ id: source.resourceId, name: source.name, ...source }))), ...observations.flatMap(item => (item.sourceIds || []).map(id => ({ id, name: nameOf(project, ctx, id) })))].filter(item => item.id && item.name));
    return {
      ok: true, root, projection, targetRules, proposalRules: targetRules.filter(item => !item.approved), approvedRules: targetRules.filter(item => item.approved), observations, mechanisms, cases, actors, flows, unknowns, conflicts, sources, conformance,
      authorization: { baselineId: ctx.baselineId, versionMode: ctx.versionMode, excludedCount: (projection.excluded || []).length + (projection.missing || []).length, flowReferencesComplete: !(projection.flowReferences || []).some(reference => reference.status !== 'visible'), complete: !(projection.excluded || []).length && !(projection.missing || []).length },
      note: '目标规范、当前观察和技术机制共享知识与版本，但任何一层都不会自动冒充另一层。'
    };
  }

  function flowModel(project, ctx, projection, flow) {
    const stageIds = [...new Set(flow.nodes.map(node => node.stageId).filter(Boolean))];
    const stages = stageIds.map((stageId, index) => {
      const stageEntity = Access.readEntity(project, ctx, stageId).data;
      const nodes = flow.nodes.filter(node => node.stageId === stageId).map(node => flowNode(project, ctx, node));
      const nodeIds = new Set(flow.nodes.filter(node => node.stageId === stageId).map(node => node.id));
      const edges = flow.edges.filter(edge => nodeIds.has(edge.from) || nodeIds.has(edge.to)).map(edge => flowEdge(project, ctx, flow, edge));
      return { id: stageId, index: index + 1, name: stageEntity?.name || (nodes.length === 1 ? nodes[0].name : `阶段 ${index + 1}`), goal: stageEntity?.goal || stageEntity?.description || null, nodes, edges };
    });
    return { id: flow.id, revision: flow.revision || null, scenarioId: flow.scenarioId, stages, nodes: flow.nodes.map(node => flowNode(project, ctx, node)), edges: flow.edges.map(edge => flowEdge(project, ctx, flow, edge)), omittedReferenceCount: flow.omittedReferenceCount || 0 };
  }
  function flowNode(project, ctx, node) {
    const knowledgeId = node.actionId || node.ruleId || node.eventId || null, entity = knowledgeId ? Access.readEntity(project, ctx, knowledgeId).data : null;
    return { ...clone(node), knowledgeId, name: entity?.name || node.name || ({ start: '开始', end: '结束', decision: '业务判断', exception: '异常处理' }[node.type] || '业务环节'), businessName: entity?.name || null };
  }
  function flowEdge(project, ctx, flow, edge) {
    const from = flow.nodes.find(node => node.id === edge.from), to = flow.nodes.find(node => node.id === edge.to);
    const rule = edge.when?.ruleId ? Access.readEntity(project, ctx, edge.when.ruleId).data : null, version = C.ruleVersion(rule), outcome = version?.outcomes?.find(item => item.id === edge.when?.outcomeId);
    return { ...clone(edge), fromName: flowNode(project, ctx, from).name, toName: flowNode(project, ctx, to).name, ruleName: rule?.name || null, outcomeLabel: outcome?.label || edge.when?.outcomeId || null, semanticLabel: edge.label || outcome?.label || ({ next: '下一步', return: '回流', when: '条件分支', error: '异常' }[edge.type] || edge.type) };
  }

  function exportDecisionProjection(project, ctx, rootId) {
    const view = decisionProjection(project, ctx, rootId);
    if (!view.ok) return { ok: false, code: view.code };
    const exported = { ...view, projection: undefined, exportedAt: C.now(), exportReadContext: clone(ctx), authorizationNotice: view.authorization.complete ? '导出时已重新授权。' : '当前授权或版本闭包不完整，不得作为正式执行依据。' };
    return { ok: true, data: exported };
  }

  return { optionsFor, ruleView, conformanceFor, decisionProjection, flowModel, collectRuleReferences, exportDecisionProjection };
});
