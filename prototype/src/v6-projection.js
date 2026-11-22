/* Business Atlas V6 — knowledge projection, presentation facts and complete formal contracts. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const A = commonjs ? require('./v9-applicability.js') : root.AtlasV9Applicability;
  const W = commonjs ? require('./write-access.js') : root.AtlasWriteAccess;
  const api = factory(G, C, A, W);
  if (commonjs) module.exports = api;
  root.AtlasV6Projection = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (G, C, A, W) {
  'use strict';
  const clone = C.clone;
  const REQUIRED_PREDICATES = new Set(['contains', 'belongsTo', 'implements', 'triggers', 'constrains', 'blocks', 'reads', 'writes', 'dependsOn', 'affects', 'produces', 'transitions', 'changes', 'requires', 'uses', 'cites', 'supports', 'appliesTo']);
  const CAUSAL_PREDICATES = new Set(['triggers', 'produces', 'transitions', 'changes']);
  function projectKnowledge(p, rootId, options = {}) {
    const root = C.getEntity(p, rootId, options);
    if (!root) return { ok: false, code: 'NOT_FOUND', errors: ['指定知识对象不存在于当前版本范围'], root: null, nodes: [], assertions: [], missing: [{ id: rootId, reason: '对象或修订不存在' }] };
    const permission = options.permission || { role: 'admin', clearance: 'restricted' };
    if (!G.permissionAllows(root, permission)) return { ok: false, code: 'FORBIDDEN', errors: ['当前权限不能读取此知识对象'], root: null, nodes: [], assertions: [], missing: [], excluded: [{ id: rootId, reason: '根对象权限不足' }] };
    const nodes = new Map([[rootId, { entity: root, distance: 0, via: [] }]]), assertions = new Map(), excluded = [], missing = [];
    let frontier = [rootId], distance = 0, maxDepth = options.unlimited === false ? Math.max(0, options.depth ?? 2) : 100;
    while (frontier.length && distance < maxDepth) {
      const next = [];
      frontier.forEach(id => {
        C.getAssertions(p, id, { ...options, permission: null }).forEach(a => {
          if (options.requiredOnly && !REQUIRED_PREDICATES.has(a.predicate)) return;
          if (options.approvedOnly && (a.verificationStatus !== 'confirmed' || a.factKind !== 'approved_specification')) return;
          const otherId = a.subject === id ? a.object : a.subject; if (!otherId) return;
          const other = C.getEntity(p, otherId, { ...options, permission: null });
          if (!other) { missing.push({ id: otherId, assertionId: a.assertionId, reason: '关联对象或版本不存在' }); return; }
          if (!G.permissionAllows(other, permission)) { excluded.push({ id: otherId, assertionId: a.assertionId, reason: '关联对象权限不足' }); return; }
          assertions.set(a.assertionId, a);
          if (!nodes.has(otherId)) { nodes.set(otherId, { entity: other, distance: distance + 1, via: [id] }); next.push(otherId); }
        });
      });
      frontier = next; distance++;
    }
    const flows = [...nodes.values()].map(n => n.entity).filter(e => e.typeId === 'Scenario').map(e => C.getFlow(p, e.id, options)).filter(Boolean);
    const flowReferences = [];
    // Flow references are semantic facts too. Resolve every action/rule/event through the
    // same revision and permission boundary, then prune unreadable flow content instead of
    // appending the raw Flow and accidentally leaking or omitting a required decision.
    flows.forEach(flow => {
      const references = [];
      flow.nodes.forEach(node => ['actionId', 'ruleId', 'eventId'].filter(key => node[key]).forEach(key => references.push({ id: node[key], flowId: flow.id, nodeId: node.id, key })));
      flow.edges.filter(edge => edge.when?.ruleId).forEach(edge => references.push({ id: edge.when.ruleId, flowId: flow.id, edgeId: edge.id, key: 'when.ruleId' }));
      references.forEach(reference => {
        const other = C.getEntity(p, reference.id, { ...options, permission: null });
        if (!other) { const issue = { ...reference, reason: 'Flow 引用对象或版本不存在' }; missing.push(issue); flowReferences.push({ ...reference, status: 'missing' }); return; }
        if (!G.permissionAllows(other, permission)) { const issue = { ...reference, reason: 'Flow 引用对象权限不足' }; excluded.push(issue); flowReferences.push({ ...reference, status: 'excluded' }); return; }
        flowReferences.push({ ...reference, status: 'visible' });
        if (!nodes.has(other.id)) nodes.set(other.id, { entity: other, distance: 1, via: [`flow:${flow.id}`] });
      });
    });
    const visibleIds = new Set(nodes.keys()), authorizedFlows = flows.map(flow => {
      const nodeAllowed = node => ['actionId', 'ruleId', 'eventId'].every(key => !node[key] || visibleIds.has(node[key])), edgeAllowed = edge => !edge.when?.ruleId || visibleIds.has(edge.when.ruleId);
      const visibleNodes = flow.nodes.filter(nodeAllowed), visibleNodeIds = new Set(visibleNodes.map(node => node.id));
      return { ...clone(flow), nodes: clone(visibleNodes), edges: clone(flow.edges.filter(edge => visibleNodeIds.has(edge.from) && visibleNodeIds.has(edge.to) && edgeAllowed(edge))), omittedReferenceCount: flowReferences.filter(reference => reference.flowId === flow.id && reference.status !== 'visible').length };
    });
    return { ok: true, root, nodes: [...nodes.values()], assertions: [...assertions.values()], flows: authorizedFlows, flowReferences, missing, excluded, options: { ...options, permission, project: p } };
  }
  function neighbors(projection, id) {
    const related = [];
    projection.assertions.forEach(a => {
      if (a.subject !== id && a.object !== id) return;
      const outgoing = a.subject === id, otherId = outgoing ? a.object : a.subject, node = projection.nodes.find(n => n.entity.id === otherId);
      related.push({ assertion: a, outgoing, entity: node?.entity || null, otherId });
    });
    return related;
  }
  function focusFacts(p, projection, focusId) {
    if (!projection.ok) return null;
    const node = projection.nodes.find(n => n.entity.id === focusId), entity = node?.entity || (focusId === projection.root.id ? projection.root : null);
    if (!entity) return { missing: true, entity: null, errors: ['该对象尚未进入当前业务上下文，可直接补充关联。'] };
    const related = neighbors(projection, entity.id), otherTypes = predicate => related.filter(r => !predicate || predicate.includes(r.assertion.predicate)).map(r => r.entity).filter(Boolean);
    const rules = entity.typeId === 'Rule' ? [entity] : otherTypes(['constrains', 'blocks', 'uses', 'affects', 'appliesTo']).filter(e => e.typeId === 'Rule');
    const transitions = [];
    if (entity.typeId === 'BusinessObject') transitions.push(...(entity.transitions || []));
    projection.nodes.forEach(n => { if (n.entity.typeId === 'BusinessObject') (n.entity.transitions || []).filter(t => t.actionRef === entity.id).forEach(t => transitions.push({ ...t, objectId: n.entity.id })); });
    const sources = new Map();
    (entity.sourceIds || []).forEach(id => { const source = C.getEntity(p, id, projection.options); if (source) sources.set(id, { entity: source, locator: '对象级来源' }); });
    related.forEach(r => (r.assertion.sourceRefs || []).forEach(ref => { const source = ref.kind === 'human_confirmation' ? null : C.getEntity(p, ref.resourceId, projection.options); if (source) sources.set(`${source.id}:${ref.locator}`, { entity: source, locator: ref.locator, assertionId: r.assertion.assertionId }); }));
    const flowFacts = [];
    projection.flows.forEach(flow => {
      const selectedNode = flow.nodes.find(n => n.actionId === entity.id || n.eventId === entity.id || n.ruleId === entity.id);
      if (selectedNode) flowFacts.push({ flow, node: selectedNode, incoming: flow.edges.filter(e => e.to === selectedNode.id), outgoing: flow.edges.filter(e => e.from === selectedNode.id) });
    });
    return {
      missing: false, entity, revision: entity.revision, ruleVersion: entity.typeId === 'Rule' ? C.ruleVersion(entity) : null,
      meaning: entity.description || entity.goal || entity.purpose || entity.conclusion || '',
      inputs: entity.input || entity.inputs || entity.props?.input || entity.props?.payload || null,
      preconditions: entity.pre || (entity.typeId === 'Rule' ? C.ruleVersion(entity)?.scope : null),
      outputs: entity.output || entity.props?.output || null,
      rules, transitions,
      technical: (() => { const all = [...related]; related.filter(r => r.entity?.typeId === 'Action').forEach(action => { const actionId = action.entity.id; neighbors(projection, actionId).forEach(r => { if (r.entity && ['Surface', 'API', 'DataEntity', 'Configuration', 'Module', 'System', 'Component', 'Framework', 'Dependency', 'Runtime', 'DeploymentUnit', 'ArchitectureDecision', 'BuildPipeline', 'Technology'].includes(r.entity.typeId) && !all.some(existing => existing.assertion.assertionId === r.assertion.assertionId)) all.push(r); }); }); projection.nodes.filter(n => ['Surface', 'API', 'DataEntity', 'Configuration', 'Module', 'System', 'Component', 'Framework', 'Dependency', 'Runtime', 'DeploymentUnit', 'ArchitectureDecision', 'BuildPipeline', 'Technology'].includes(n.entity.typeId)).forEach(node => { neighbors(projection, node.entity.id).forEach(r => { if (!all.some(existing => existing.assertion.assertionId === r.assertion.assertionId)) all.push(r); }); }); return all.filter(r => r.entity && ['Surface', 'API', 'DataEntity', 'Configuration', 'Module', 'System', 'Component', 'Framework', 'Dependency', 'Runtime', 'DeploymentUnit', 'ArchitectureDecision', 'BuildPipeline', 'Technology'].includes(r.entity.typeId)); })(),
      events: otherTypes(['produces', 'triggers']).filter(e => e.typeId === 'Event'),
      upstream: related.filter(r => !r.outgoing), downstream: related.filter(r => r.outgoing), related,
      evidence: [...sources.values()], flowFacts,
      issues: projection.options.permission?.role === 'external' ? [] : (p.issues || []).filter(i => i.objectId === entity.id && G.permissionAllows(i, projection.options.permission)),
      tasks: projection.options.permission?.role === 'external' ? [] : (p.tasks || []).filter(t => (t.rootId === entity.id || t.knowledgeIds?.includes(entity.id)) && G.permissionAllows(C.getEntity(p, t.rootId, projection.options), projection.options.permission)),
      dataWrites: related.filter(r => r.assertion.predicate === 'writes'), dataReads: related.filter(r => r.assertion.predicate === 'reads')
    };
  }
  function compose(projection, focusId, task = 'understand') {
    if (!projection.ok) return { layout: 'missing', sections: [], focus: null };
    const focus = focusFacts(projection.options.project, projection, focusId || projection.root.id);
    const types = new Set(projection.nodes.map(n => n.entity.typeId));
    const sections = [];
    if (projection.flows.some(f => f.nodes.length)) sections.push({ id: 'flows', priority: 1, component: 'CausalStructure' });
    if (types.has('Rule')) sections.push({ id: 'rules', priority: 2, component: 'DecisionContext' });
    if (projection.nodes.some(n => n.entity.typeId === 'BusinessObject' && n.entity.transitions?.length)) sections.push({ id: 'states', priority: 3, component: 'StateContext' });
    if (['Surface', 'API', 'DataEntity', 'Configuration', 'Component', 'Framework', 'Dependency', 'Runtime', 'DeploymentUnit', 'ArchitectureDecision', 'BuildPipeline', 'Technology'].some(t => types.has(t))) sections.push({ id: 'implementation', priority: 4, component: 'ImplementationContext' });
    if (types.has('Event')) sections.push({ id: 'events', priority: 5, component: 'EventContext' });
    sections.push({ id: 'evidence', priority: 6, component: 'EvidenceContext' });
    return { layout: projection.flows.length ? 'causal-workbench' : types.has('Term') ? 'concept-reader' : 'semantic-workbench', sections: sections.sort((a, b) => task === 'implement' ? b.priority - a.priority : a.priority - b.priority), focus };
  }
  function reasonFor(a) {
    return ({ constrains: '直接业务约束', blocks: '禁止或阻断约束', implements: '实现映射', triggers: '触发关系', reads: '必要数据读取', writes: '必要数据写入', produces: '行为产生事件', transitions: '状态变化', requires: '需求依赖', cites: '证据定位', contains: '组成结构', belongsTo: '业务归属', affects: '配置影响', uses: '使用规则或资源', changes: '业务对象变化', dependsOn: '技术依赖', supports: '事实支持' })[a.predicate] || '关联知识';
  }
  function generateFormalContract(p, input = {}) {
    W.assert(input.context, 'contract:generate');
    const permission = input.permission || { role: 'reader', clearance: 'restricted' };
    const rootId = input.rootId || input.requirementId;
    const baselineId = input.baselineId || p.versionLayers.baseline;
    const root = C.getEntity(p, rootId, { baselineId });
    if (!root) return { ok: false, code: 'NOT_FOUND', errors: ['任务根对象不在指定基线中'] };
    if (!G.permissionAllows(root, permission)) return { ok: false, code: 'FORBIDDEN', errors: ['当前权限不能生成交付契约'] };
    if (input.applicableContext && input.applicabilityMode === 'current') {
      const applicability = A.evaluate(root, input.applicableContext, p);
      if (applicability.status !== 'applicable') return { ok: false, code: 'APPLICABILITY_UNKNOWN', errors: [applicability.status === 'unknown' ? '根知识适用条件不足，不能生成当前条件正式契约' : '根知识不适用当前条件'], applicability };
    }
    const baseline = C.baselineById(p, baselineId);
    const projection = projectKnowledge(p, rootId, { baselineId, permission, approvedOnly: true, requiredOnly: true, unlimited: true, applicableContext: input.applicableContext, applicabilityMode: input.applicabilityMode || 'explore' });
    const nodes = projection.nodes.map(n => n.entity), assertions = projection.assertions;
    const missing = [...projection.missing], excluded = [...projection.excluded], errors = [];
    if ((projection.flowReferences || []).some(reference => reference.status !== 'visible')) errors.push('流程中的必要动作、规则或事件引用未能按当前权限和基线完整解析');
    if (baseline?.approvalStatus !== 'approved') errors.push('批准基线不可用');
    if (!['published', 'approved'].includes(root.status)) errors.push('根对象尚未批准');
    const acceptance = root.acceptance || root.props?.acceptance || [];
    if (!acceptance.length) { errors.push('根对象缺少明确验收条件'); missing.push({ id: root.id, reason: '根对象缺少明确验收条件' }); }
    const rules = nodes.filter(n => n.typeId === 'Rule');
    const rootHasEvidence = (root.sourceIds || []).length || assertions.some(a => a.predicate === 'cites' && a.subject === root.id);
    if (!rootHasEvidence) missing.push({ id: root.id, reason: '任务根对象缺少可信来源或验收证据' });
    rules.forEach(rule => {
      const version = C.ruleVersion(rule); if (!version?.scope) missing.push({ id: rule.id, reason: '必要规则缺少适用范围' });
      const hasEvidence = (rule.sourceIds || []).length || assertions.some(a => a.predicate === 'cites' && a.subject === rule.id);
      if (!hasEvidence) missing.push({ id: rule.id, reason: '必要规则缺少可信来源' });
    });
    const requiredIds = new Set(nodes.map(n => n.id));
    const unknowns = (p.unknowns || []).filter(u => u.status !== 'resolved' && (!u.relatedIds?.length || u.relatedIds.some(id => requiredIds.has(id))));
    unknowns.filter(u => u.blocksFormal && !u.approvedWaiver).forEach(u => errors.push(`关键未知：${u.title}`));
    const conflicts = (p.conflictRecords || []).filter(c => c.status !== 'resolved' && c.assertionIds?.some(id => assertions.some(a => a.assertionId === id)));
    conflicts.filter(c => c.blocking !== false).forEach(c => errors.push(`阻断性事实冲突：${c.title}`));
    if (missing.length) errors.push(`${missing.length} 项必要知识缺失或版本不可读`);
    if (excluded.length) errors.push(`${excluded.length} 项必要知识因权限无法纳入`);
    if (errors.length) return { ok: false, code: excluded.length ? 'PERMISSION_INCOMPLETE' : missing.length ? 'INCOMPLETE_CONTEXT' : 'UNSAFE_CONTEXT', errors, missing, excluded, unknowns: clone(unknowns), explorationAvailable: true };
    const task = input.taskId ? p.tasks.find(t => t.id === input.taskId) : (p.tasks || []).find(t => t.rootId === rootId && t.status !== 'completed') || C.createTask(p, { rootId, title: input.taskIntent || `实现${root.name}`, purpose: input.purpose }, input.context);
    if (!task) return { ok: false, code: 'TASK_NOT_FOUND', errors: ['执行任务不存在'] };
    const packageId = `KCP6-${p.meta.id}-${String((p.contextPackages?.length || 0) + 1).padStart(4, '0')}`;
    const evidenceMap = new Map();
    assertions.forEach(a => (a.sourceRefs || []).forEach(ref => { if (ref.kind === 'human_confirmation') { evidenceMap.set(ref.resourceId + ref.locator, { kind: 'human_confirmation', confirmedBy: ref.confirmedBy, locator: ref.locator }); } else { const source = C.getEntity(p, ref.resourceId, { baselineId }); if (!source) missing.push({ id: ref.resourceId, reason: '证据版本不可读' }); else if (!G.permissionAllows(source, permission)) excluded.push({ id: ref.resourceId, reason: '证据权限不足' }); else evidenceMap.set(source.id + ref.locator, { ...clone(source), locator: ref.locator }); } }));
    if (missing.length || excluded.length) return { ok: false, code: 'EVIDENCE_INCOMPLETE', errors: ['必要证据无法按权限和版本完整读取'], missing, excluded };
    const pack = {
      ok: true, packageId, projectId: p.meta.id, taskId: task.id, rootId, mode: 'formal', purpose: input.purpose || task.purpose || 'development', generatedAt: C.now(), status: 'current', valid: true,
      taskIntent: input.taskIntent || task.title,
      businessContext: nodes.filter(n => ['Project', 'Domain', 'Capability', 'Feature', 'Scenario', 'Actor', 'BusinessObject'].includes(n.typeId)).map(clone),
      approvedRequirements: [clone(root)], constraints: rules.map(clone),
      behavior: { actions: nodes.filter(n => n.typeId === 'Action').map(clone), events: nodes.filter(n => n.typeId === 'Event').map(clone), transitions: nodes.filter(n => n.typeId === 'BusinessObject').flatMap(n => clone(n.transitions || [])), assertions: clone(assertions) },
      implementationContext: nodes.filter(n => ['Surface', 'System', 'Module', 'API', 'DataEntity', 'Configuration'].includes(n.typeId)).map(clone),
      acceptance: clone(acceptance), evidence: [...evidenceMap.values()], unknowns: clone(unknowns), conflicts: clone(conflicts), missing: [], excluded: [],
      inclusionReasons: assertions.map(a => ({ assertionId: a.assertionId, subject: a.subject, predicate: a.predicate, object: a.object, reason: reasonFor(a), revision: a.revision })),
      versionBinding: { baselineId, baselineApproved: true, entries: Object.fromEntries(nodes.map(n => [n.id, n.revision])), assertions: Object.fromEntries(assertions.map(a => [a.assertionId, a.revision])), flows: Object.fromEntries(projection.flows.map(f => [f.id, f.revision])), flowSemantics: Object.fromEntries(projection.flows.map(f => [f.id, C.flowSemanticFingerprint(f)])) },
      feedbackContract: { required: ['完成工作与产物定位', '测试结果', '新发现冲突', '待澄清问题', '与契约的偏差'], destination: 'pending_verification', mayModifyApprovedBaseline: false },
      includedFacts: nodes.map(n => ({ id: n.id, revision: n.revision, typeId: n.typeId })), includedAssertions: assertions.map(a => a.assertionId), completeness: { depthLimited: false, requiredClosure: true, rootPermissionChecked: true, dependencyPermissionsChecked: true, acceptanceChecked: true, evidenceChecked: true }
    };
    p.contextPackages.unshift(pack); task.status = 'active'; task.activeContractId = packageId; task.contractIds = [...new Set([...(task.contractIds || []), packageId])]; task.knowledgeIds = [...new Set([...(task.knowledgeIds || []), ...nodes.map(n => n.id)])]; task.updatedAt = C.now(); task.history.push({ at: C.now(), event: 'contract-generated', packageId, baselineId });
    return pack;
  }
  function exploreContract(p, input = {}) {
    const permission = input.permission || { role: 'reader', clearance: 'restricted' }, root = C.getEntity(p, input.rootId, { baselineId: input.baselineId });
    if (!root) return { ok: false, code: 'NOT_FOUND', errors: ['对象不存在'] };
    if (!G.permissionAllows(root, permission)) return { ok: false, code: 'FORBIDDEN', errors: ['当前权限不能读取根对象'] };
    const projection = projectKnowledge(p, input.rootId, { baselineId: input.baselineId, permission, unlimited: false, depth: input.depth ?? 2 });
    return { ok: true, mode: 'exploration', root: clone(root), projection, limitations: ['探索结果受深度控制，不保证必要依赖完整，不可作为正式交付。'] };
  }
  function reviewTask(p, taskId, input = {}) {
    W.assert(input.context, 'contract:generate');
    const task = p.tasks.find(t => t.id === taskId); if (!task) throw new Error('任务不存在');
    const pack = generateFormalContract(p, { taskId, rootId: task.rootId, baselineId: input.baselineId || p.versionLayers.baseline, permission: input.permission || { role: 'reader', clearance: 'restricted' }, context: input.context, applicableContext: input.applicableContext, applicabilityMode: input.applicabilityMode, purpose: task.purpose, taskIntent: task.title });
    if (!pack.ok) return pack;
    (p.issues || []).filter(i => i.taskId === taskId && !['resolved', 'cancelled'].includes(i.status)).forEach(issue => { issue.reviewStatus = 'included-in-contract-review'; issue.reviewPackageId = pack.packageId; });
    task.status = 'active'; task.reviewReason = null; task.history.push({ at: C.now(), event: 'review-completed', packageId: pack.packageId }); return pack;
  }
  function completeTask(p, taskId, input = {}) {
    W.assert(input.context, 'task:accept');
    const task = p.tasks.find(t => t.id === taskId); if (!task) throw new Error('任务不存在');
    const pack = p.contextPackages.find(c => c.packageId === task.activeContractId);
    if (!pack || !pack.valid || pack.reviewStatus === 'requires-review') return { ok: false, errors: ['当前契约无效或需要复核'] };
    if ((p.issues || []).some(i => i.taskId === taskId && !['resolved', 'cancelled'].includes(i.status))) return { ok: false, errors: ['仍有未解决问题'] };
    const acceptance = { by: input.by || '验收人', result: input.result || '通过', evidence: input.evidence || '模拟验收记录', at: C.now(), packageId: pack.packageId, baselineId: pack.versionBinding.baselineId };
    task.acceptance.push(acceptance); task.status = 'completed'; task.deliveryState = 'fully-delivered'; task.updatedAt = C.now(); task.history.push({ at: C.now(), event: 'accepted', ...acceptance }); return { ok: true, task, acceptance };
  }
  return { REQUIRED_PREDICATES, CAUSAL_PREDICATES, projectKnowledge, neighbors, focusFacts, compose, generateFormalContract, exploreContract, reviewTask, completeTask, reasonFor };
});
