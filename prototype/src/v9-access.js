/* Business Atlas V9 — non-bypassable read boundary, revision events and authorized views. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const R = commonjs ? require('./v8-read.js') : root.AtlasV8Read;
  const A = commonjs ? require('./v9-applicability.js') : root.AtlasV9Applicability;
  const api = factory(C, G, R, A);
  if (commonjs) module.exports = api;
  root.AtlasV9Access = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (C, G, R, A) {
  'use strict';
  const clone = C.clone;
  const CODES = ['OK', 'NOT_FOUND', 'FORBIDDEN', 'VERSION_UNAVAILABLE', 'CONTEXT_INSUFFICIENT', 'APPLICABILITY_UNKNOWN', 'DATA_INTEGRITY_ERROR'];
  const listeners = new Map();
  const topics = {
    CreateEntity: 'knowledge.changed', SetProperty: 'knowledge.changed', DefineRule: 'knowledge.changed', AddBehavior: 'knowledge.changed', ConnectBehavior: 'knowledge.changed', DefineTransition: 'knowledge.changed', DefineFlowStructure: 'knowledge.changed', LinkKnowledge: 'assertion.changed', BindImplementation: 'assertion.changed', AttachEvidence: 'knowledge.changed', RetireEntity: 'knowledge.changed', RetireAssertion: 'assertion.changed', Undo: 'knowledge.changed', CreateIssue: 'governance.issue.changed', ProposeChange: 'governance.workflow.changed', PublishRevision: 'baseline.published'
  };
  function result(code, data = null, extra = {}) { return { ok: code === 'OK', code, data: code === 'OK' ? data : null, ...extra }; }
  function on(topic, listener) { if (!listeners.has(topic)) listeners.set(topic, new Set()); listeners.get(topic).add(listener); return () => listeners.get(topic)?.delete(listener); }
  function emit(topic, payload) { [...(listeners.get(topic) || []), ...(listeners.get('*') || [])].forEach(listener => listener(clone(payload), topic)); }
  function touch(project, topic = 'knowledge.changed', detail = {}) {
    project._readRevision = (project._readRevision || 0) + 1;
    project._lastChangedAt = C.now();
    const event = { projectId: project.meta.id, revision: project._readRevision, topic, at: project._lastChangedAt, ...clone(detail) };
    emit(topic, event); emit('project.changed', event); return event;
  }
  function integrateCommands() {
    if (C.__v9AccessIntegrated) return;
    const execute = C.execute;
    C.execute = function (project, command, input) {
      const output = execute.apply(this, arguments);
      if (output?.ok !== false) touch(project, topics[command] || 'knowledge.changed', { command, changeSetId: project.draftWorkspace?.id || output?.changeSet?.id });
      return output;
    };
    ['createIssue', 'decideIssue', 'createTask', 'publishRevision', 'proposeChange'].forEach(name => {
      const original = C[name]; if (typeof original !== 'function') return;
      C[name] = function (project) { const output = original.apply(this, arguments); if (output?.ok !== false) touch(project, name === 'createIssue' || name === 'decideIssue' ? 'governance.issue.changed' : name === 'publishRevision' ? 'baseline.published' : name === 'createTask' ? 'governance.task.changed' : 'governance.workflow.changed', { command: name }); return output; };
    });
    C.__v9AccessIntegrated = true;
  }
  function rawEntity(project, id, ctx) {
    const options = ctx.versionMode === 'draft' ? { draft: true } : { baselineId: ctx.baselineId };
    return C.getEntity(project, id, options);
  }
  function readEntity(project, ctx, id) {
    if (!id || ctx.projectId !== project.meta.id) return result('NOT_FOUND');
    const baseline = ctx.versionMode === 'draft' ? null : C.baselineById(project, ctx.baselineId);
    if (ctx.versionMode !== 'draft' && !baseline) return result('VERSION_UNAVAILABLE');
    const raw = rawEntity(project, id, ctx);
    if (!raw) return result('NOT_FOUND');
    if (!G.permissionAllows(raw, ctx.permissionContext)) return result('FORBIDDEN');
    const applicability = A.evaluate(raw, ctx.applicableContext, project);
    if (ctx.applicabilityMode === 'current' && applicability.status === 'unknown') return result('APPLICABILITY_UNKNOWN', null, { applicability });
    if (ctx.applicabilityMode === 'current' && applicability.status !== 'applicable') return result('NOT_FOUND', null, { applicability });
    return result('OK', { ...clone(raw), applicability }, { readContext: clone(ctx) });
  }
  function listEntities(project, ctx, filter = {}) {
    if (ctx.projectId !== project.meta.id) return result('NOT_FOUND', []);
    const entities = R.listKnowledge(project, ctx, filter).map(entity => ({ ...clone(entity), applicability: A.evaluate(entity, ctx.applicableContext, project) })).filter(entity => ctx.applicabilityMode !== 'current' || entity.applicability.status === 'applicable');
    return result('OK', entities, { count: entities.length, readContext: clone(ctx) });
  }
  function readRelations(project, ctx, id) {
    const root = readEntity(project, ctx, id);
    if (!root.ok && root.code !== 'APPLICABILITY_UNKNOWN') return result(root.code, []);
    const relations = R.getRelations(project, ctx, id).filter(assertion => {
      const subject = readEntity(project, ctx, assertion.subject), object = assertion.object ? readEntity(project, ctx, assertion.object) : { ok: true };
      return subject.ok && object.ok;
    }).map(assertion => ({ ...clone(assertion), applicability: A.evaluate(assertion, ctx.applicableContext, project) })).filter(assertion => ctx.applicabilityMode !== 'current' || assertion.applicability.status === 'applicable');
    return result('OK', relations, { count: relations.length, applicability: A.summarize(relations) });
  }
  function currentPermission(project, id, permission) {
    const current = G.getEntity(project, id);
    return current ? G.permissionAllows(current, permission) : false;
  }
  function authorizedContractView(project, ctx, snapshot) {
    const baselineId = snapshot.versionBinding?.baselineId;
    const visible = new Set(), omitted = new Set(), notApplicable = new Set();
    const candidateIds = new Set([
      ...(snapshot.includedFacts || []).map(item => item.id),
      ...['businessContext', 'approvedRequirements', 'constraints', 'implementationContext', 'evidence'].flatMap(key => (snapshot[key] || []).map(item => item.id).filter(Boolean)),
      snapshot.rootId
    ].filter(Boolean));
    candidateIds.forEach(id => {
      const inSnapshot = C.getEntity(project, id, { baselineId });
      const applicability = inSnapshot ? A.evaluate(inSnapshot, ctx.applicableContext, project) : null;
      if (inSnapshot && currentPermission(project, id, ctx.permissionContext) && G.permissionAllows(inSnapshot, ctx.permissionContext)) {
        if (ctx.applicabilityMode === 'current' && applicability.status !== 'applicable') notApplicable.add(id); else visible.add(id);
      } else omitted.add(id);
    });
    if (snapshot.rootId && !visible.has(snapshot.rootId)) return result('FORBIDDEN');
    const filterItems = items => (items || []).filter(item => !item.id || visible.has(item.id)).map(clone);
    const assertions = (snapshot.behavior?.assertions || []).filter(assertion => visible.has(assertion.subject) && (!assertion.object || visible.has(assertion.object))).filter(assertion => ctx.applicabilityMode !== 'current' || A.evaluate(assertion, ctx.applicableContext, project).status === 'applicable').map(clone);
    const view = {
      ...clone(snapshot),
      businessContext: filterItems(snapshot.businessContext), approvedRequirements: filterItems(snapshot.approvedRequirements), constraints: filterItems(snapshot.constraints),
      implementationContext: filterItems(snapshot.implementationContext), evidence: filterItems(snapshot.evidence),
      unknowns: (snapshot.unknowns || []).filter(item => !item.relatedIds?.length || item.relatedIds.every(id => visible.has(id))).map(clone),
      conflicts: (snapshot.conflicts || []).filter(item => (item.assertionIds || []).every(id => assertions.some(assertion => assertion.assertionId === id))).map(clone),
      behavior: { ...clone(snapshot.behavior || {}), assertions, actions: filterItems(snapshot.behavior?.actions), events: filterItems(snapshot.behavior?.events) },
      includedFacts: (snapshot.includedFacts || []).filter(item => visible.has(item.id)).map(clone), includedAssertions: assertions.map(item => item.assertionId),
      inclusionReasons: (snapshot.inclusionReasons || []).filter(item => visible.has(item.subject) && (!item.object || visible.has(item.object))).map(clone),
      versionBinding: { ...clone(snapshot.versionBinding || {}), entries: Object.fromEntries(Object.entries(snapshot.versionBinding?.entries || {}).filter(([id]) => visible.has(id))), assertions: Object.fromEntries(Object.entries(snapshot.versionBinding?.assertions || {}).filter(([id]) => assertions.some(assertion => assertion.assertionId === id))) },
      excluded: [{ count: omitted.size, reason: omitted.size ? '当前执行者未获部分必要知识授权' : '' }].filter(item => item.count)
    };
    const complete = omitted.size === 0 && notApplicable.size === 0 && snapshot.missing?.length === 0;
    view.authorization = { complete, executionAllowed: complete && snapshot.valid !== false, omittedCount: omitted.size, notApplicableCount: notApplicable.size, checkedAt: C.now(), notice: complete ? '当前权限与适用条件可读取完整契约。' : notApplicable.size ? '当前条件下存在不适用或适用性未知的必要内容，该契约不能用于正式执行。' : '该契约对当前执行者不完整，不能用于正式执行。' };
    delete view.ok;
    return result('OK', view, { authorized: true, complete });
  }
  function readContract(project, ctx, idOrTaskId) {
    const snapshot = (project.contextPackages || []).find(pack => pack.packageId === idOrTaskId || pack.taskId === idOrTaskId);
    if (!snapshot) return result('NOT_FOUND');
    if (ctx.versionMode !== 'draft' && snapshot.versionBinding?.baselineId !== ctx.baselineId) return result('VERSION_UNAVAILABLE');
    return authorizedContractView(project, { ...ctx, baselineId: snapshot.versionBinding?.baselineId }, snapshot);
  }
  function relatedIdsFor(kind, raw) {
    if (!raw) return [];
    if (kind === 'task') {
      const sc = raw.semanticContract || {};
      return [...new Set([raw.rootId, ...(raw.knowledgeIds || []),
        ...(sc.existingBehavior?.specificationRefs || []).map(ref => ref.ruleId), ...(sc.existingBehavior?.observedBehaviorRefs || []),
        ...(sc.targetBehavior?.decisionChanges || []).map(change => change.ruleId), ...(sc.acceptanceCaseRefs || []),
        ...(sc.retainedConstraints || []).map(ref => ref.ruleId || ref.entityId), ...(raw.impactInvestigation?.items || []).map(item => item.entityId)
      ].filter(Boolean))];
    }
    if (kind === 'issue') return [raw.objectId, ...(raw.relatedKnowledgeIds || [])];
    if (kind === 'feedback') return raw.relatedKnowledgeIds || [];
    if (kind === 'agent-run') return [...(raw.sourceRefs || []).map(ref => ref.resourceId), ...(raw.proposedKnowledge || []).flatMap(item => [item.subject, item.object, item.entityId])].filter(Boolean);
    if (kind === 'question-answer') return (raw.citations || []).map(item => item.knowledgeId);
    if (kind === 'diagram') return [raw.rootKnowledgeId, ...(raw.includedKnowledgeIds || [])];
    if (kind === 'import-proposal') return raw.relatedKnowledgeIds || [];
    if (kind === 'contract') return [raw.rootId, ...(raw.approvedRequirements || []).map(item => item.id)];
    return [];
  }
  const RECORDS = {
    baseline: ['baselines', 'id'], workflow: ['workflowInstances', 'id'], 'workflow-definition': ['workflowDefinitions', 'id'], task: ['tasks', 'id'], issue: ['issues', 'id'], 'change-set': ['changeSets', 'id'], feedback: ['feedbackRecords', 'feedbackId'], 'agent-run': ['agentRuns', 'runId'], diagram: ['visualViews', 'viewId'], 'question-answer': ['questionAnswers', 'answerId'], 'import-proposal': ['importProposals', 'proposalId']
  };
  function readRecord(project, ctx, kind, id) {
    if (kind === 'contract') return readContract(project, ctx, id);
    if (['entity', 'rule', 'resource'].includes(kind)) return readEntity(project, ctx, id);
    if (kind === 'assertion') {
      const relations = readRelations(project, ctx, id); const assertion = (project.assertions || []).find(item => item.assertionId === id);
      if (!assertion) return result('NOT_FOUND');
      const subject = readEntity(project, ctx, assertion.subject), object = assertion.object ? readEntity(project, ctx, assertion.object) : { ok: true };
      return subject.ok && object.ok ? result('OK', clone(assertion)) : result('FORBIDDEN');
    }
    if (kind === 'flow') {
      const flow = R.getFlow(project, ctx, id); if (!flow) return result('NOT_FOUND');
      const scenario = readEntity(project, ctx, flow.scenarioId || id); return scenario.ok ? result('OK', clone(flow)) : result('FORBIDDEN');
    }
    const config = RECORDS[kind]; if (!config) return result('NOT_FOUND');
    const raw = (project[config[0]] || []).find(item => item[config[1]] === id); if (!raw) return result('NOT_FOUND');
    if (['task', 'issue', 'change-set', 'baseline', 'agent-run', 'import-proposal'].includes(kind) && (ctx.permissionContext?.role === 'external' || ctx.permissionContext?.clearance === 'public')) return result('FORBIDDEN');
    if (!G.permissionAllows(raw, ctx.permissionContext)) return result('FORBIDDEN');
    const related = relatedIdsFor(kind, raw), visibleIds = related.filter(id => readEntity(project, ctx, id).ok);
    if ((kind === 'task' && !readEntity(project, ctx, raw.rootId).ok) || (kind === 'issue' && !readEntity(project, ctx, raw.objectId).ok)) return result('FORBIDDEN');
    if (related.length && !visibleIds.length && !['feedback', 'import-proposal'].includes(kind)) return result('FORBIDDEN');
    if (kind === 'baseline') {
      const entries = Object.fromEntries(Object.keys(raw.entries || {}).filter(id => readEntity(project, { ...ctx, baselineId: raw.id }, id).ok).map(id => [id, raw.entries[id]]));
      return result('OK', { ...clone(raw), entries, assertionRevisions: Object.fromEntries(Object.entries(raw.assertionRevisions || {}).filter(([assertionId]) => { const a = project.assertionRevisionStore?.[`${assertionId}@${raw.assertionRevisions[assertionId]}`]; return a && entries[a.subject] && (!a.object || entries[a.object]); })) });
    }
    const safe = clone(raw);
    if (kind === 'task') {
      safe.knowledgeIds = (safe.knowledgeIds || []).filter(id => visibleIds.includes(id));
      if (safe.semanticContract) {
        const sc = safe.semanticContract;
        if (sc.existingBehavior) {
          sc.existingBehavior.specificationRefs = (sc.existingBehavior.specificationRefs || []).filter(ref => visibleIds.includes(ref.ruleId));
          sc.existingBehavior.observedBehaviorRefs = (sc.existingBehavior.observedBehaviorRefs || []).filter(id => visibleIds.includes(id));
        }
        if (sc.targetBehavior) sc.targetBehavior.decisionChanges = (sc.targetBehavior.decisionChanges || []).filter(change => !change.ruleId || visibleIds.includes(change.ruleId));
        sc.acceptanceCaseRefs = (sc.acceptanceCaseRefs || []).filter(id => visibleIds.includes(id));
        sc.retainedConstraints = (sc.retainedConstraints || []).filter(ref => !(ref.ruleId || ref.entityId) || visibleIds.includes(ref.ruleId || ref.entityId));
      }
      if (safe.impactInvestigation) {
        const deliveryArtifactIds = new Set([raw.delivery?.deliveryManifest?.id, raw.delivery?.pullRequest?.id, ...(raw.delivery?.tests || []).map(item => item.id), ...(raw.delivery?.knowledgeChanges || []).map(item => item.id)].filter(Boolean));
        const visibleImpactIds = new Set((safe.impactInvestigation.items || []).map(item => item.entityId).filter(Boolean).filter(id => deliveryArtifactIds.has(id) || readEntity(project, ctx, id).ok));
        safe.impactInvestigation.items = (safe.impactInvestigation.items || []).filter(item => !item.entityId || visibleImpactIds.has(item.entityId));
        safe.impactInvestigation.excludedCount = (raw.impactInvestigation?.items || []).length - safe.impactInvestigation.items.length;
        safe.impactInvestigation.authorizationComplete = safe.impactInvestigation.excludedCount === 0;
      }
    }
    if (kind === 'issue') safe.relatedKnowledgeIds = (safe.relatedKnowledgeIds || []).filter(id => visibleIds.includes(id));
    if (kind === 'change-set') {
      const operationAllowed = operation => {
        let allowed = true;
        const inspect = value => {
          if (!allowed || value == null) return;
          if (Array.isArray(value)) return value.forEach(inspect);
          if (typeof value === 'object') return Object.values(value).forEach(inspect);
          if (typeof value === 'string' && C.getEntity(project, value, { draft: true }) && !readEntity(project, ctx, value).ok) allowed = false;
        };
        inspect(operation.input || {}); return allowed;
      };
      safe.operations = (safe.operations || []).filter(operationAllowed);
      safe.entities = Object.fromEntries(Object.keys(safe.entities || {}).filter(id => readEntity(project, ctx, id).ok).map(id => [id, safe.entities[id]]));
      safe.assertions = Object.fromEntries(Object.values(safe.assertions || {}).filter(assertion => readEntity(project, ctx, assertion.subject).ok && (!assertion.object || readEntity(project, ctx, assertion.object).ok)).map(assertion => [assertion.assertionId, assertion]));
      safe.flows = Object.fromEntries(Object.values(safe.flows || {}).filter(flow => readEntity(project, ctx, flow.scenarioId).ok).map(flow => [flow.id, flow]));
      safe.issueIds = (safe.issueIds || []).filter(issueId => readRecord(project, ctx, 'issue', issueId).ok);
      safe.excludedOperationCount = (raw.operations || []).length - safe.operations.length;
    }
    if (kind === 'agent-run') {
      safe.toolCalls = (safe.toolCalls || []).map(call => ({ ...call, inputSummary: redact(call.inputSummary), outputSummary: redact(call.outputSummary), error: redact(call.error) }));
      safe.executionEvents = (safe.executionEvents || []).map(event => ({ ...event, content: redact(event.content) }));
      safe.proposedKnowledge = (safe.proposedKnowledge || []).filter(item => !item.subject || visibleIds.includes(item.subject));
      safe.unresolvedQuestions = safe.unresolvedQuestions || [];
    }
    if (kind === 'diagram') safe.includedKnowledgeIds = (safe.includedKnowledgeIds || []).filter(id => visibleIds.includes(id));
    return result('OK', safe);
  }
  function redact(value) {
    if (value == null) return value;
    return String(value).replace(/(password|passwd|token|secret|api[_-]?key|authorization|connectionstring)\s*[:=]\s*[^\s,;]+/gi, '$1=[REDACTED]').replace(/(bearer\s+)[A-Za-z0-9._~-]+/gi, '$1[REDACTED]').replace(/((?:mongodb|postgres|mysql):\/\/[^\s/:]+:)[^\s@]+@/gi, '$1[REDACTED]@');
  }
  function listRecords(project, ctx, kind) {
    const config = RECORDS[kind];
    if (kind === 'contract') return result('OK', (project.contextPackages || []).map(pack => readContract(project, ctx, pack.packageId)).filter(item => item.ok).map(item => item.data));
    if (!config) return result('NOT_FOUND', []);
    return result('OK', (project[config[0]] || []).map(raw => readRecord(project, ctx, kind, raw[config[1]])).filter(item => item.ok).map(item => item.data));
  }
  function readTimeline(project, ctx, rootId) {
    const knowledge = readEntity(project, ctx, rootId); if (!knowledge.ok) return result(knowledge.code);
    const activities = [
      ...(project.issues || []).filter(item => item.objectId === rootId).map(item => ({ kind: 'issue', at: item.createdAt, id: item.id })),
      ...(project.feedbackRecords || []).filter(item => item.relatedKnowledgeIds?.includes(rootId)).map(item => ({ kind: 'feedback', at: item.createdAt, id: item.feedbackId })),
      ...(project.changeSets || []).filter(item => item.operations?.some(op => JSON.stringify(op.input || {}).includes(rootId))).map(item => ({ kind: 'change-set', at: item.createdAt, id: item.id }))
    ].sort((a, b) => String(a.at).localeCompare(String(b.at)));
    const baseline = C.baselineById(project, ctx.baselineId);
    return result('OK', { knowledgeSnapshot: { mode: ctx.timeMode, baselineId: ctx.baselineId, approvedAt: baseline?.createdAt || null, entity: knowledge.data }, collaborationHistory: activities, note: '后续活动可以关联旧基线，但不会回写当时的业务知识快照。' });
  }
  function safePersist(key, store, storage) {
    const target = storage || (typeof localStorage !== 'undefined' ? localStorage : null); if (!target) return { ok: false, code: 'STORAGE_UNAVAILABLE', error: '浏览器本地存储不可用，请导出备份。' };
    try { const serialized = JSON.stringify(store); target.setItem(key, serialized); return { ok: true, bytes: serialized.length * 2 }; }
    catch (error) { return { ok: false, code: 'STORAGE_QUOTA', error: '浏览器存储空间不足，当前修改未持久化；请导出备份或减少图片与工具日志。', cause: error.name }; }
  }
  return { CODES, result, on, emit, touch, integrateCommands, readEntity, listEntities, readRelations, readContract, authorizedContractView, readRecord, listRecords, readTimeline, redact, safePersist, topics };
});
