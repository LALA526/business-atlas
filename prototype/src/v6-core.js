/* Business Atlas V6 — Knowledge Commands, Change Sets, draft workspace and revision archives. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const K = commonjs ? require('../v4-core.js') : root.AtlasCore;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const VM = commonjs ? require('./project-factory.js') : root.AtlasProjectFactory;
  const A = commonjs ? require('./v9-applicability.js') : root.AtlasV9Applicability;
  const W = commonjs ? require('./write-access.js') : root.AtlasWriteAccess;
  const api = factory(K, G, VM, A, W);
  if (commonjs) module.exports = api;
  root.AtlasV6 = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (K, G, VM, A, W) {
  'use strict';
  const clone = G.clone, now = G.now;
  const PREFIX = { Project: 'PRJ', Domain: 'DOM', Capability: 'CAP', Feature: 'FEAT', Scenario: 'SCN', Action: 'ACT', Actor: 'ACTOR', Rule: 'RULE', BusinessObject: 'OBJ', Surface: 'UI', API: 'API', DataEntity: 'DATA', Configuration: 'CFG', Event: 'EVT', Term: 'TERM', Resource: 'SRC', Requirement: 'REQ', System: 'SYS', Module: 'MOD', Exception: 'EXC' };
  const COMMANDS = {
    CreateEntity: { label: '创建知识对象', types: ['*'], schema: ['typeId', 'name', 'description'], permission: 'editor' },
    SetProperty: { label: '设置业务属性', types: ['*'], schema: ['entityId', 'path', 'value'], permission: 'editor' },
    DefineRule: { label: '定义规则与判断', types: ['Rule'], schema: ['name', 'scope', 'ruleKind', 'definition'], permission: 'editor' },
    AddBehavior: { label: '添加业务行为', types: ['Scenario'], schema: ['scenarioId', 'kind', 'name'], permission: 'editor' },
    ConnectBehavior: { label: '连接业务因果', types: ['Scenario'], schema: ['scenarioId', 'from', 'to', 'type'], permission: 'editor' },
    DefineTransition: { label: '定义状态转移', types: ['BusinessObject'], schema: ['objectId', 'fromState', 'toState', 'triggerEvent', 'guard', 'actionRef'], permission: 'editor' },
    LinkKnowledge: { label: '建立知识关联', types: ['*'], schema: ['subject', 'predicate', 'object', 'qualifiers'], permission: 'editor' },
    BindImplementation: { label: '关联技术实现', types: ['Feature', 'Action', 'Surface', 'API'], schema: ['rootId', 'resourceType', 'relation'], permission: 'editor' },
    AttachEvidence: { label: '添加事实依据', types: ['*'], schema: ['targetId', 'resource', 'locator'], permission: 'editor' },
    CreateIssue: { label: '创建待确认问题', types: ['*'], schema: ['title', 'objectId', 'impact', 'assignee'], permission: 'editor' },
    ProposeChange: { label: '提交变更集', types: ['ChangeSet'], schema: ['reason'], permission: 'editor' },
    PublishRevision: { label: '审核并发布', types: ['ChangeSet'], schema: ['confirmedBy'], permission: 'owner' },
    RetireEntity: { label: '停用知识对象', types: ['*'], schema: ['entityId', 'reason'], permission: 'editor' },
    RetireAssertion: { label: '停用知识关联', types: ['Assertion'], schema: ['assertionId', 'reason'], permission: 'editor' },
    DefineFlowStructure: { label: '定义显式流程结构', types: ['Scenario'], schema: ['flow'], permission: 'editor' },
    Undo: { label: '撤销上一步', types: ['ChangeSet'], schema: [], permission: 'editor' }
  };
  const structuralPredicates = new Set(['contains', 'belongsTo', 'implements', 'references', 'requires', 'uses', 'dependsOn']);
  function baselineById(p, id) { return (p.baselines || []).find(b => b.id === id) || null; }
  function workspace(p, create = true) {
    if (p.draftWorkspace || !create) return p.draftWorkspace || null;
    const id = `CS-${p.meta.id}-${String((p.changeSets?.length || 0) + 1).padStart(4, '0')}`;
    p.changeSets ||= [];
    p.draftWorkspace = { id, projectId: p.meta.id, title: '未命名业务变更', reason: '', status: 'draft', baseBaselineId: p.versionLayers?.baseline || null, entities: {}, assertions: {}, flows: {}, deletedEntityIds: [], deletedAssertionIds: [], operations: [], undoStack: [], validation: null, reviewDecisions: [], issueIds: [], createdAt: now(), updatedAt: now() };
    p.changeSets.unshift(p.draftWorkspace); return p.draftWorkspace;
  }
  function initProject(p) {
    if (!K.schemaFor(p, 'Event')) K.registerType(p, { id: 'Event', name: '业务事件', dimension: '业务认知', fields: [K.field('eventType', '事件类型'), K.field('topic', '主题'), K.field('payload', '业务载荷', 'json')], relations: ['triggers', 'produces', 'references'] });
    p.changeSets ||= []; p.publishedFlows ||= {}; p.flowRevisions ||= {}; p.assertionRevisionStore ||= {}; p.tasks ||= []; p.issues ||= []; p.contextPackages ||= []; p.contractFeedback ||= [];
    (p.assertions || []).forEach(a => { p.assertionRevisionStore[`${a.assertionId}@${a.revision}`] ||= G.deepFreeze(clone(a)); });
    (p.flows || []).forEach(f => { const rev = f.revision || 'r1'; p.flowRevisions[`${f.id}@${rev}`] ||= G.deepFreeze(clone({ ...f, revision: rev })); p.publishedFlows[f.id] ||= p.flowRevisions[`${f.id}@${rev}`]; });
    if (!p.viewDefinitions) p.viewDefinitions = (p.panoramas || []).map(pan => ({ id: pan.id, rootId: pan.rootId, title: pan.title, preferredMode: 'read', groupStrategy: 'semantic', focusIds: pan.entityIds || [], relationTypes: [] }));
    p.panoramas = undefined;
    if (p.draftWorkspace) {
      p.draftWorkspace.undoStack ||= [];
      const index = p.changeSets.findIndex(cs => cs.id === p.draftWorkspace.id);
      if (index >= 0) p.changeSets[index] = p.draftWorkspace; else p.changeSets.unshift(p.draftWorkspace);
    }
    (p.baselines || []).forEach(baseline => {
      const match = String(baseline.source || '').match(/Change Set\s+(CS-[\w-]+)/);
      const changeSet = match && p.changeSets.find(cs => cs.id === match[1]);
      if (changeSet && changeSet.status !== 'published') {
        changeSet.status = 'published'; changeSet.publishedAt = baseline.createdAt; changeSet.reviewDecisions = baseline.confirmations || changeSet.reviewDecisions;
        if (!changeSet.operations.length) changeSet.operations.push({ id: `OP-${changeSet.id}-RECOVERY`, command: 'AuditRecovery', at: baseline.createdAt, summary: '从批准基线恢复变更索引（旧版本刷新持久化迁移）' });
      }
    });
    return p;
  }
  function initProjects(projects) { Object.values(projects).forEach(initProject); return projects; }
  function createProjectSet() { const projects = VM.createProjectSet(); Object.values(projects).forEach(project => { project._v7Seed = true; }); return initProjects(projects); }
  function createBlankProject(id, name, goal) { const p = initProject(VM.createBlankProject(id, name, goal || '通过统一操作逐步建立业务知识。')); p.meta.status = 'draft'; p.profile.positioning = goal || ''; workspace(p); return p; }
  function getEntityUnchecked(p, id, options = {}) {
    if (!id) return null;
    const bare = String(id).split('@')[0];
    if (options.draft && p.draftWorkspace?.entities?.[bare]) return p.draftWorkspace.entities[bare];
    if (options.baselineId) {
      const baseline = baselineById(p, options.baselineId), rev = baseline?.entries?.[bare];
      return rev ? p.knowledgeRevisions?.[`${bare}@${rev}`] || null : null;
    }
    return G.getEntity(p, bare, options.revision ? { revision: options.revision } : {});
  }
  function getEntity(p, id, options = {}) {
    const entity = getEntityUnchecked(p, id, options);
    if (!entity || (options.permission && !G.permissionAllows(entity, options.permission))) return null;
    if (options.applicableContext && options.applicabilityMode === 'current') {
      const applicability = A.evaluate(entity, options.applicableContext, p);
      if (applicability.status !== 'applicable') return null;
    }
    return entity;
  }
  function allEntities(p, options = {}) {
    const map = new Map();
    G.allEntities(p, { permission: options.permission }).forEach(e => { if (!options.baselineId || baselineById(p, options.baselineId)?.entries?.[e.id]) map.set(e.id, options.baselineId ? getEntity(p, e.id, options) : e); });
    Object.values(p.publishedEntities || {}).forEach(e => { if (!options.baselineId || baselineById(p, options.baselineId)?.entries?.[e.id]) map.set(e.id, options.baselineId ? getEntity(p, e.id, options) : e); });
    if (options.draft) Object.values(p.draftWorkspace?.entities || {}).forEach(e => map.set(e.id, e));
    const visible = [...map.values()].filter(Boolean).filter(e => options.includeRetired || e.status !== 'retired').filter(e => !options.permission || G.permissionAllows(e, options.permission));
    if (!options.applicableContext) return visible;
    const annotated = A.annotate(visible, options.applicableContext, p);
    return options.applicabilityMode === 'current' ? annotated.filter(e => e.applicability.status === 'applicable') : annotated;
  }
  function nextId(p, typeOrPrefix) {
    const schema = K.schemaFor(p, typeOrPrefix), prefix = schema?.idPrefix || PREFIX[typeOrPrefix] || String(typeOrPrefix).replace(/[^A-Za-z0-9]/g, '').slice(0, 4).toUpperCase() || 'KN';
    p.identitySequences ||= {}; const used = new Set([...allEntities(p, { draft: true, includeRetired: true }).map(e => e.id)]);
    let number = p.identitySequences[typeOrPrefix] || 0, id; do { number++; id = `${prefix}-${p.meta.id.replace(/\D/g, '') || 'X'}-${String(number).padStart(4, '0')}`; } while (used.has(id)); p.identitySequences[typeOrPrefix] = number; return id;
  }
  function getAssertion(p, id, options = {}) {
    if (options.draft && p.draftWorkspace?.assertions?.[id]) return p.draftWorkspace.assertions[id];
    const revision = options.revision || (options.baselineId ? baselineById(p, options.baselineId)?.assertionRevisions?.[id] : null);
    if (revision) return p.assertionRevisionStore?.[`${id}@${revision}`] || p.knowledgeRevisions?.[`${id}@${revision}`] || null;
    return (p.assertions || []).find(a => a.assertionId === id) || null;
  }
  function getAssertions(p, id, options = {}) {
    let assertions;
    if (options.baselineId) {
      const baseline = baselineById(p, options.baselineId);
      assertions = Object.keys(baseline?.assertionRevisions || {}).map(assertionId => getAssertion(p, assertionId, { baselineId: options.baselineId })).filter(Boolean);
    } else {
      const map = new Map((p.assertions || []).map(a => [a.assertionId, a]));
      if (options.draft) Object.values(p.draftWorkspace?.assertions || {}).forEach(a => map.set(a.assertionId, a));
      assertions = [...map.values()];
    }
    const visible = assertions.filter(a => !id || a.subject === id || a.object === id).filter(a => a.status !== 'retired').filter(a => !options.predicate || a.predicate === options.predicate).filter(a => {
      if (!options.permission) return true;
      const s = getEntity(p, a.subject, options), o = a.object ? getEntity(p, a.object, options) : null;
      return G.permissionAllows(s, options.permission) && (!a.object || G.permissionAllows(o, options.permission));
    });
    if (!options.applicableContext) return visible;
    const annotated = A.annotate(visible, options.applicableContext, p);
    return options.applicabilityMode === 'current' ? annotated.filter(a => a.applicability.status === 'applicable') : annotated;
  }
  function getFlow(p, scenarioId, options = {}) {
    if (options.draft) {
      const flow = Object.values(p.draftWorkspace?.flows || {}).find(f => f.scenarioId === scenarioId || f.id === scenarioId); if (flow) return flow;
    }
    if (options.baselineId) {
      const baseline = baselineById(p, options.baselineId), rev = baseline?.flowRevisions?.[scenarioId] || Object.entries(baseline?.flowRevisions || {}).find(([id]) => p.flowRevisions?.[`${id}@${Object.values(baseline.flowRevisions)[0]}`]?.scenarioId === scenarioId)?.[1];
      const key = Object.entries(baseline?.flowRevisions || {}).find(([flowId]) => flowId === scenarioId || p.flowRevisions?.[`${flowId}@${baseline.flowRevisions[flowId]}`]?.scenarioId === scenarioId);
      if (key) return p.flowRevisions?.[`${key[0]}@${key[1]}`] || null;
      return null;
    }
    const current = Object.values(p.publishedFlows || {}).find(f => f.scenarioId === scenarioId || f.id === scenarioId); if (current) return current;
    return (p.flows || []).find(f => f.scenarioId === scenarioId || f.id === scenarioId) || { id: `FLOW-${scenarioId}`, scenarioId, nodes: [], edges: [], layout: {} };
  }
  function mutate(p, command, input, fn) {
    const ws = workspace(p); if (ws.status !== 'draft') throw new Error('当前变更集已锁定，请创建新的业务变更');
    const originalSequences = clone(p.identitySequences || {}), staging = clone(ws), previousWorkspace = p.draftWorkspace;
    p.draftWorkspace = staging;
    try {
      const result = fn(staging);
      staging.undoStack = [...ws.undoStack, clone({ entities: ws.entities, assertions: ws.assertions, flows: ws.flows, deletedEntityIds: ws.deletedEntityIds, deletedAssertionIds: ws.deletedAssertionIds })].slice(-30);
      staging.operations = [...ws.operations, { id: `OP-${ws.id}-${String(ws.operations.length + 1).padStart(3, '0')}`, command, input: clone(input), at: now(), by: input.by || '当前用户', summary: result.summary || COMMANDS[command]?.label || command }];
      staging.updatedAt = now(); staging.validation = null;
      p.identitySequences = clone(p.identitySequences || originalSequences);
      Object.keys(ws).forEach(key => delete ws[key]); Object.assign(ws, staging); p.draftWorkspace = ws;
      const changeSetIndex = p.changeSets.findIndex(cs => cs.id === ws.id); if (changeSetIndex >= 0) p.changeSets[changeSetIndex] = ws;
      return result;
    } catch (error) {
      p.draftWorkspace = previousWorkspace; p.identitySequences = originalSequences;
      throw error;
    }
  }
  function entityFromInput(p, input) {
    const typeId = input.typeId, id = input.id || nextId(p, typeId);
    const common = { id, typeId, name: input.name?.trim(), description: input.description || input.goal || '', revision: 'r0', status: 'draft', projectId: p.meta.id, sourceIds: clone(input.sourceIds || []), sensitivity: input.sensitivity || 'internal', permissions: input.permissions || { read: ['*'], edit: ['editor', 'admin'], approve: ['owner'] }, props: clone(input.props || {}) };
    if (!common.name) throw new Error('请填写名称');
    const schema = K.schemaFor(p, typeId);
    if (!schema || schema.instantiable === false || schema.editable === false) throw new Error(`知识类型 ${typeId} 未注册或不允许实例化`);
    if (schema.permissions?.create && !schema.permissions.create.includes('editor') && !schema.permissions.create.includes('admin')) throw new Error(`当前用户不能创建 ${schema.name || typeId}`);
    if (typeId === 'Rule') {
      const version = input.version || { version: 'draft', status: 'draft', scope: input.scope || '', supportsEvaluation: false, constraint: input.description || '', inputs: [], outcomes: [], rows: [], exceptions: [] };
      return { ...common, owner: input.owner || '当前用户', currentVersion: 'draft', versions: { draft: clone(version) }, acceptance: clone(input.acceptance || []) };
    }
    const entity = { ...common, ...clone(input.extra || {}) };
    if (['Surface', 'API', 'DataEntity', 'Configuration', 'Event', 'System', 'Module'].includes(typeId)) entity.props = { ...common.props, ...clone(input.extra || {}) };
    if (schema.custom || ['ManufacturingBatch', 'LegalMaterial', 'AdministrativeMatter'].includes(typeId)) { const errors = K.validateEntity(p, entity); if (errors.length) throw new Error(errors.join('；')); }
    return entity;
  }
  function insertEntity(p, ws, entity) {
    if (getEntity(p, entity.id, { draft: true })) throw new Error(`稳定 ID ${entity.id} 已存在`);
    ws.entities[entity.id] = entity; return entity;
  }
  function typeMatches(actual, expected) { return expected === '*' || (Array.isArray(expected) ? expected : [expected]).includes(actual); }
  function validateSourceRefs(p, refs, options = {}) {
    const errors = [];
    (refs || []).forEach((ref, index) => {
      if (ref.kind === 'human_confirmation') { if (!ref.confirmedBy || !ref.locator) errors.push(`来源 ${index + 1} 缺少人工确认人与确认记录`); return; }
      const resource = getEntity(p, ref.resourceId, { draft: options.draft, baselineId: options.baselineId });
      if (!resource) { errors.push(`来源 ${ref.resourceId || '空'} 不存在或不在当前项目`); return; }
      if (resource.typeId !== 'Resource' && resource.typeId !== 'Evidence') errors.push(`${ref.resourceId} 不是有效来源对象`);
      if (!ref.locator || (typeof ref.locator === 'object' && !Object.keys(ref.locator).length)) errors.push(`来源 ${ref.resourceId} 缺少精确定位`);
      if (options.forConfirmed && resource.typeId === 'Resource' && resource.props?.trust !== 'confirmed') errors.push(`来源 ${ref.resourceId} 自身尚未确认，不能支持已确认断言`);
    });
    return errors;
  }
  function validateAssertion(p, a, options = {}) {
    const errors = [], subject = getEntity(p, a.subject, { draft: options.draft, baselineId: options.baselineId }), object = a.object ? getEntity(p, a.object, { draft: options.draft, baselineId: options.baselineId }) : null, predicate = G.PREDICATES[a.predicate];
    if (!subject) errors.push(`主语 ${a.subject} 不存在`);
    if (a.object && !object) errors.push(`宾语 ${a.object} 不存在`);
    if (!predicate) errors.push(`关系 ${a.predicate} 未注册`);
    if (predicate && subject && !typeMatches(subject.typeId, predicate.from)) errors.push(`${subject.typeId} 不能作为 ${a.predicate} 的主语`);
    if (predicate && object && !typeMatches(object.typeId, predicate.to)) errors.push(`${object.typeId} 不能作为 ${a.predicate} 的宾语`);
    if (a.predicate === 'affects' && subject?.typeId === 'Configuration') {
      const q = a.qualifiers || {}; if (!(q.environment || q.environments?.length) || !(q.organization || q.region || q.businessScope) || !(q.configVersion || q.version)) errors.push('配置影响必须声明环境、业务范围和配置版本');
    }
    if (['triggers', 'transitions', 'produces'].includes(a.predicate) && !a.qualifiers?.condition && !a.qualifiers?.operation && !a.qualifiers?.after && !a.qualifiers?.mode) errors.push(`${G.PREDICATES[a.predicate]?.label || a.predicate} 需要明确业务条件或触发时机`);
    errors.push(...validateSourceRefs(p, a.sourceRefs, { draft: options.draft, baselineId: options.baselineId, forConfirmed: a.verificationStatus === 'confirmed' }));
    if (a.verificationStatus === 'confirmed' && !a.sourceRefs?.length) errors.push('已确认断言必须具有可信来源或人工确认记录');
    if (a.verificationStatus === 'confirmed' && !a.owner) errors.push('已确认断言必须具有确认人');
    return errors;
  }
  function insertAssertion(p, ws, input) {
    const id = input.assertionId || `AST-${p.meta.id}-${String(Object.keys(ws.assertions).length + (p.assertions?.length || 0) + 1).padStart(5, '0')}`;
    const existing = getAssertion(p, id, { draft: true }); if (existing) throw new Error(`断言 ${id} 已存在`);
    const a = { assertionId: id, projectId: p.meta.id, subject: input.subject, predicate: input.predicate, object: input.object, qualifiers: clone(input.qualifiers || {}), sourceRefs: clone(input.sourceRefs || []), factKind: input.factKind || 'approved_specification', verificationStatus: 'unverified', validTime: clone(input.validTime || { from: now().slice(0, 10), to: null }), revision: 'a0', owner: input.owner || '', explanation: input.explanation || '', status: 'draft', createdAt: now() };
    const errors = validateAssertion(p, a, { draft: true }); if (errors.length) throw new Error(errors.join('；'));
    ws.assertions[id] = a; return a;
  }
  function ensureFlow(p, ws, scenarioId) {
    const existing = Object.values(ws.flows).find(f => f.scenarioId === scenarioId); if (existing) return existing;
    const base = getFlow(p, scenarioId); const flow = clone(base || { id: `FLOW-${scenarioId}`, scenarioId, nodes: [], edges: [], layout: {} });
    flow.id ||= `FLOW-${scenarioId}`; flow.nodes ||= []; flow.edges ||= []; flow.layout ||= {}; flow.revision = 'r0'; ws.flows[flow.id] = flow; return flow;
  }
  function execute(p, command, input = {}, context = null) {
    if (!COMMANDS[command]) throw new Error(`未注册知识命令 ${command}`);
    const capability = command === 'PublishRevision' ? 'baseline:publish' : command === 'ProposeChange' ? 'changes:submit' : command === 'CreateIssue' ? 'issue:write' : 'knowledge:draft';
    W.assert(context, capability);
    if (command === 'Undo') { const ws = workspace(p, false), previous = ws?.undoStack.pop(); if (!previous) throw new Error('没有可撤销的操作'); Object.assign(ws, previous); ws.operations.push({ id: `OP-${ws.id}-${Date.now()}`, command, at: now(), summary: '撤销上一步操作' }); return { ok: true, summary: '已撤销上一步操作' }; }
    if (command === 'ProposeChange') return proposeChange(p, { ...input, context });
    if (command === 'PublishRevision') return publishRevision(p, { ...input, context });
    if (command === 'CreateIssue') return createIssue(p, { ...input, context });
    return mutate(p, command, input, ws => {
      switch (command) {
        case 'CreateEntity': { const entity = insertEntity(p, ws, entityFromInput(p, input)); return { ok: true, entity, summary: `创建${input.name}` }; }
        case 'SetProperty': { const assertionId = input.assertionId, entity = assertionId ? (ws.assertions[assertionId] || clone(getAssertion(p, assertionId))) : (ws.entities[input.entityId] || clone(getEntity(p, input.entityId))); if (!entity) throw new Error('要修改的对象不存在'); const path = String(input.path || '').split('.'); let target = entity; while (path.length > 1) { const key = path.shift(); target = target[key] ||= {}; } target[path[0]] = clone(input.value); entity.status = 'draft'; if (assertionId) ws.assertions[assertionId] = entity; else ws.entities[entity.id] = entity; return { ok: true, entity, assertion: assertionId ? entity : null, summary: assertionId ? `修改${assertionId}的语义属性` : `修改${entity.name}的业务属性` }; }
        case 'DefineRule': { const current = input.entityId ? getEntity(p, input.entityId) : null, entity = ws.entities[input.entityId] || (current ? clone(current) : insertEntity(p, ws, entityFromInput(p, { ...input, typeId: 'Rule', id: input.entityId }))); if (entity.typeId !== 'Rule') throw new Error('当前对象不是业务规则'); const definition = clone(input.definition || {}); if (!String(input.scope || definition.scope || '').trim()) throw new Error('规则必须明确适用范围，不能默认全项目生效'); const version = { ...(entity.versions?.draft || {}), ...definition, version: 'draft', status: 'draft', scope: input.scope || definition.scope, summary: definition.summary || input.description || entity.description }; entity.name = input.name || entity.name; entity.description = input.description || entity.description; entity.scope = version.scope; entity.versions = { ...(entity.versions || {}), draft: version }; entity.currentVersion = 'draft'; ws.entities[entity.id] = entity; if (input.actionIds?.length) input.actionIds.forEach(actionId => insertAssertion(p, ws, { subject: entity.id, predicate: definition.ruleKind === 'constraint' && definition.constraintType === 'prohibit' ? 'blocks' : 'constrains', object: actionId, qualifiers: { condition: version.scope }, sourceRefs: clone(input.sourceRefs || []), owner: input.owner || '' })); return { ok: true, entity, summary: `定义规则${entity.name}` }; }
        case 'AddBehavior': { const scenario = getEntity(p, input.scenarioId, { draft: true }); if (!scenario || scenario.typeId !== 'Scenario') throw new Error('请选择有效业务场景'); const kind = input.kind || 'action', flow = ensureFlow(p, ws, scenario.id); let entity = null, nodeId = input.nodeId || `N-${nextId(p, kind === 'event' ? 'EVT' : kind === 'decision' ? 'RULE' : 'ACT')}`;
          if (['action', 'event', 'exception'].includes(kind)) { const typeId = kind === 'event' ? 'Event' : 'Action'; entity = insertEntity(p, ws, entityFromInput(p, { ...input, typeId, extra: kind === 'action' ? { scenarioId: scenario.id, actorId: input.actorId || null, purpose: input.purpose || input.description || '', pre: input.pre || [], input: input.inputs || [], output: input.outputs || [], objectChanges: [], exceptions: [] } : {} })); }
          if (kind === 'decision' && !input.ruleId) throw new Error('判断节点必须关联已创建规则');
          flow.nodes.push({ id: nodeId, type: kind, name: input.name || entity?.name || ({ parallel: '并行开始', join: '并行汇合', subprocess: '子流程', case: '案件活动' }[kind] || '业务环节'), actionId: kind === 'event' ? null : entity?.id || input.actionId, eventId: kind === 'event' ? entity?.id : null, ruleId: input.ruleId || null, layout: { x: input.x || flow.nodes.length * 180, y: input.y || 0 } });
          if (entity && kind !== 'event') insertAssertion(p, ws, { subject: entity.id, predicate: 'belongsTo', object: scenario.id, qualifiers: { structure: 'behavior' }, explanation: '人工添加的场景行为' });
          return { ok: true, entity, flow, nodeId, summary: `添加${input.name || kind}` }; }
        case 'ConnectBehavior': { const flow = ensureFlow(p, ws, input.scenarioId), from = flow.nodes.find(n => n.id === input.from), to = flow.nodes.find(n => n.id === input.to); if (!from || !to) throw new Error('连接两端必须都是已创建环节'); if (input.from === input.to && input.type !== 'return') throw new Error('普通环节不能连接到自身'); if (input.type === 'when' && (!input.ruleId || !input.outcomeId)) throw new Error('条件分支必须选择规则输出'); if (input.type === 'when') { const rule = getEntity(p, input.ruleId, { draft: true }), version = ruleVersion(rule); if (!version?.outcomes?.some(o => o.id === input.outcomeId)) throw new Error('规则输出不存在，请先检查规则定义'); } if (input.type === 'parallel' && !flow.nodes.some(n => n.type === 'join')) throw new Error('并行分支前请先添加并行汇合'); const edge = { id: input.id || `E-${flow.id}-${String(flow.edges.length + 1).padStart(3, '0')}`, from: input.from, to: input.to, type: input.type || 'next', ...(input.ruleId ? { when: { ruleId: input.ruleId, outcomeId: input.outcomeId } } : {}), label: input.label || input.condition || '', verificationStatus: 'unverified' }; flow.edges.push(edge); if (input.type === 'when' && input.ruleId) insertAssertion(p, ws, { subject: input.ruleId, predicate: 'appliesTo', object: input.scenarioId, qualifiers: { condition: input.label || input.outcomeId, outcomeId: input.outcomeId }, explanation: '规则输出驱动场景分支' }); return { ok: true, edge, flow, summary: `连接${from.name} → ${to.name}` }; }
        case 'DefineTransition': { const object = ws.entities[input.objectId] || clone(getEntity(p, input.objectId, { draft: true })); if (!object || object.typeId !== 'BusinessObject') throw new Error('请选择有效业务对象'); const states = object.states || object.props?.states || []; if (!states.includes(input.fromState) || !states.includes(input.toState)) throw new Error('原状态或目标状态不在合法状态集合中'); if (!getEntity(p, input.actionRef, { draft: true })) throw new Error('导致转移的业务动作不存在'); if (!input.triggerEvent?.trim() || !input.guard?.trim()) throw new Error('请填写触发事件和转移条件'); const transition = { id: input.id || `TR-${object.id}-${String((object.transitions || []).length + 1).padStart(3, '0')}`, fromState: input.fromState, toState: input.toState, triggerEvent: input.triggerEvent, guard: input.guard, actionRef: input.actionRef, effect: input.effect || '', sourceRefs: clone(input.sourceRefs || []), verificationStatus: 'unverified' }; object.transitions ||= []; const index = object.transitions.findIndex(t => t.id === transition.id); if (index >= 0) object.transitions[index] = transition; else object.transitions.push(transition); ws.entities[object.id] = object; insertAssertion(p, ws, { subject: input.actionRef, predicate: 'transitions', object: object.id, qualifiers: { condition: input.guard, fromState: input.fromState, toState: input.toState, triggerEvent: input.triggerEvent }, sourceRefs: clone(input.sourceRefs || []) }); return { ok: true, object, transition, summary: `定义${input.fromState} → ${input.toState}` }; }
        case 'LinkKnowledge': { const assertion = insertAssertion(p, ws, input); return { ok: true, assertion, summary: `建立${G.PREDICATES[input.predicate]?.label || input.predicate}关联` }; }
        case 'BindImplementation': { let resource = input.resourceId ? getEntity(p, input.resourceId, { draft: true }) : null; if (!resource) resource = insertEntity(p, ws, entityFromInput(p, { ...input, typeId: input.resourceType, id: input.newResourceId, extra: input.extra || {} })); const links = input.links || [{ subject: input.subject || resource.id, predicate: input.predicate, object: input.object || resource.id }]; const assertions = links.map(link => insertAssertion(p, ws, { ...link, subject: link.subject === 'NEW_RESOURCE' ? resource.id : link.subject, object: link.object === 'NEW_RESOURCE' ? resource.id : link.object, qualifiers: { ...link.qualifiers, condition: input.condition || link.qualifiers?.condition || '人工建立的技术映射', operation: input.operation || link.qualifiers?.operation, fields: input.fields || link.qualifiers?.fields, transaction: input.transaction || link.qualifiers?.transaction }, sourceRefs: clone(input.sourceRefs || []), explanation: input.explanation || '通过技术映射命令建立' })); return { ok: true, entity: resource, assertions, summary: `关联${resource.name}` }; }
        case 'AttachEvidence': { const resource = insertEntity(p, ws, entityFromInput(p, { typeId: 'Resource', id: input.resourceId, name: input.name, description: input.summary, props: { resourceType: input.resourceType || 'document', uri: input.uri || `atlas://${p.meta.id}/manual/${Date.now()}`, locator: { section: input.locator }, sourceVersion: input.sourceVersion || 'v1', summary: input.summary || '', parseStatus: 'structured', owner: input.owner || '当前用户', trust: input.trust || 'unverified' } })); const target = clone(getEntity(p, input.targetId, { draft: true })), assertion = insertAssertion(p, ws, { subject: input.targetId, predicate: 'cites', object: resource.id, qualifiers: { locator: input.locator }, sourceRefs: [{ resourceId: resource.id, locator: input.locator }], owner: input.confirmedBy || '', explanation: `支持${target?.name || input.targetId}` }); if (target) { target.sourceIds ||= []; if (!target.sourceIds.includes(resource.id)) target.sourceIds.push(resource.id); ws.entities[target.id] = target; } return { ok: true, entity: resource, assertion, summary: `为${target?.name || input.targetId}添加依据` }; }
        case 'RetireEntity': { const entity = ws.entities[input.entityId] || clone(getEntity(p, input.entityId)); if (!entity) throw new Error('对象不存在'); entity.status = 'retired'; entity.retireReason = input.reason || '人工停用'; ws.entities[entity.id] = entity; if (!ws.deletedEntityIds.includes(entity.id)) ws.deletedEntityIds.push(entity.id); return { ok: true, entity, summary: `停用${entity.name}` }; }
        case 'RetireAssertion': { const assertion = ws.assertions[input.assertionId] || clone(getAssertion(p, input.assertionId)); if (!assertion) throw new Error('关联不存在'); if (assertion.predicate === 'cites') { const sourceOwner = ws.entities[assertion.subject] || clone(getEntity(p, assertion.subject)); if (sourceOwner) { sourceOwner.sourceIds = (sourceOwner.sourceIds || []).filter(id => id !== assertion.object); ws.entities[sourceOwner.id] = sourceOwner; } } assertion.status = 'retired'; assertion.retireReason = input.reason || '人工停用'; ws.assertions[assertion.assertionId] = assertion; if (!ws.deletedAssertionIds.includes(assertion.assertionId)) ws.deletedAssertionIds.push(assertion.assertionId); return { ok: true, assertion, summary: `停用${assertion.assertionId}` }; }
        case 'DefineFlowStructure': {
          const source = clone(input.flow), scenario = getEntity(p, source?.scenarioId, { draft: true });
          if (!source?.id || !scenario || scenario.typeId !== 'Scenario') throw new Error('流程必须关联有效业务场景');
          if (Object.values(ws.flows).some(flow => flow.id === source.id)) throw new Error(`流程 ${source.id} 已存在；重复或冲突导入不会覆盖`);
          const nodes = new Set((source.nodes || []).map(node => node.id));
          if (nodes.size !== (source.nodes || []).length) throw new Error('流程节点 ID 必须唯一');
          (source.nodes || []).forEach(node => { ['actionId', 'ruleId', 'eventId'].filter(key => node[key]).forEach(key => { if (!getEntity(p, node[key], { draft: true })) throw new Error(`流程节点 ${node.id} 的 ${key} 引用无效`); }); });
          (source.edges || []).forEach(edge => { if (!nodes.has(edge.from) || !nodes.has(edge.to)) throw new Error(`流程边 ${edge.id} 的端点无效`); });
          const flow = { ...source, scenarioId: scenario.id, nodes: clone(source.nodes || []), edges: clone(source.edges || []), layout: clone(source.layout || {}), revision: 'r0', status: 'draft' };
          ws.flows[flow.id] = flow; return { ok: true, flow, summary: `定义显式流程 ${flow.id}` };
        }
        default: throw new Error(`命令 ${command} 尚未实现`);
      }
    });
  }
  function ruleVersion(entity, options = {}) {
    if (!entity) return null;
    if (options.version && options.strict) return entity.versions?.[options.version] || null;
    return entity.versions?.[options.version || entity.currentVersion] || entity.versions?.draft || entity.selectedVersion || null;
  }
  function flowSemanticFingerprint(flow) {
    if (!flow) return null;
    const semantic = { id: flow.id, scenarioId: flow.scenarioId, nodes: (flow.nodes || []).map(node => { const { layout, ...facts } = node; return facts; }), edges: (flow.edges || []).map(edge => { const { layout, ...facts } = edge; return facts; }) };
    const text = JSON.stringify(semantic); let hash = 2166136261;
    for (let index = 0; index < text.length; index++) { hash ^= text.charCodeAt(index); hash = Math.imul(hash, 16777619); }
    return `flow-sem-${(hash >>> 0).toString(16).padStart(8, '0')}`;
  }
  function validateWorkspace(p) {
    const ws = workspace(p, false), errors = [], warnings = []; if (!ws || !ws.operations.length) errors.push('变更集为空，请先建立业务知识');
    Object.values(ws?.entities || {}).forEach(entity => {
      if (!entity.name) errors.push(`${entity.id}: 名称不能为空`);
      if (entity.typeId === 'Rule') { const v = ruleVersion(entity); if (!v?.scope?.trim()) errors.push(`${entity.id}: 规则缺少适用范围`); if (v?.supportsEvaluation) { if (!v.inputs?.length || !v.outcomes?.length || !v.rows?.length) errors.push(`${entity.id}: 决策规则需要输入、结果和判断行`); v.rows?.forEach(row => { if (!v.outcomes.some(o => o.id === row.outcomeId)) errors.push(`${entity.id}: 判断行 ${row.id} 引用未定义结果`); }); } else if (!v?.constraint?.trim()) warnings.push(`${entity.id}: 尚未形式化求值，将保留为人工约束`); }
      if (entity.typeId === 'BusinessObject') { const states = new Set(entity.states || []); (entity.transitions || []).forEach(t => { if (!states.has(t.fromState) || !states.has(t.toState)) errors.push(`${entity.id}: 转移 ${t.id} 使用未定义状态`); if (!getEntity(p, t.actionRef, { draft: true })) errors.push(`${entity.id}: 转移 ${t.id} 的动作不存在`); if (!t.guard) errors.push(`${entity.id}: 转移 ${t.id} 缺少条件`); }); }
    });
    Object.values(ws?.assertions || {}).forEach(a => { if (a.status !== 'retired') validateAssertion(p, a, { draft: true }).forEach(e => errors.push(`${a.assertionId}: ${e}`)); });
    Object.values(ws?.flows || {}).forEach(flow => { const nodes = new Map(flow.nodes.map(n => [n.id, n])); flow.edges.forEach(e => { if (!nodes.has(e.from) || !nodes.has(e.to)) errors.push(`${flow.id}: 边 ${e.id} 引用无效节点`); if (e.type === 'when' && !e.when?.outcomeId) errors.push(`${flow.id}: 条件边 ${e.id} 缺少规则结果`); }); flow.nodes.filter(n => n.type === 'decision').forEach(n => { const branches = flow.edges.filter(e => e.from === n.id); if (!branches.length) errors.push(`${flow.id}: 判断 ${n.name || n.id} 没有输出分支`); branches.filter(e => e.type === 'when').forEach(e => { if (!e.when?.ruleId || !e.when?.outcomeId) { errors.push(`${flow.id}: 条件边 ${e.id} 缺少规则或结果引用`); return; } const rule = getEntity(p, e.when.ruleId, { draft: true }), v = ruleVersion(rule); if (!v?.outcomes?.some(o => o.id === e.when.outcomeId)) errors.push(`${flow.id}: 分支 ${e.id} 引用的规则输出不存在`); }); }); if (flow.nodes.some(n => n.type === 'parallel') && !flow.edges.some(e => e.type === 'join')) errors.push(`${flow.id}: 并行结构缺少汇合`); });
    return { ok: !errors.length, errors, warnings, validatedAt: now(), entityCount: Object.keys(ws?.entities || {}).length, assertionCount: Object.keys(ws?.assertions || {}).length, flowCount: Object.keys(ws?.flows || {}).length };
  }
  function proposeChange(p, input = {}) {
    W.assert(input.context, 'changes:submit');
    const ws = workspace(p, false); if (!ws || ws.status !== 'draft') return { ok: false, errors: ['没有可提交的草稿变更集'] };
    if (!String(input.reason || ws.reason).trim()) return { ok: false, errors: ['请填写本次业务修改原因'] };
    ws.reason = input.reason || ws.reason; ws.title = input.title || ws.title; ws.validation = validateWorkspace(p); if (!ws.validation.ok) return { ok: false, errors: ws.validation.errors, validation: ws.validation };
    ws.status = 'pending'; ws.updatedAt = now(); return { ok: true, changeSet: ws, validation: ws.validation };
  }
  function createBaseline(p, changeSet, confirmedBy, context) {
    W.assert(context, 'baseline:publish');
    const entries = {}, assertionRevisions = {}, flowRevisions = {};
    Object.values(p.publishedEntities || {}).forEach(e => { if (e.status !== 'retired') entries[e.id] = e.revision; });
    (p.assertions || []).forEach(a => { if (a.status !== 'retired') assertionRevisions[a.assertionId] = a.revision; });
    Object.values(p.publishedFlows || {}).forEach(f => { flowRevisions[f.id] = f.revision; });
    const baseline = { id: `BL-${p.meta.id}-CS-${String(p.baselines.length + 1).padStart(3, '0')}`, projectId: p.meta.id, createdAt: now(), approvalStatus: 'approved', entries, assertionRevisions, flowRevisions, source: `Change Set ${changeSet.id} 原子发布`, confirmations: [{ by: confirmedBy, decision: 'approved', changeSetId: changeSet.id, at: now() }] };
    p.baselines.push(baseline); p.versionLayers.baseline = baseline.id; p.meta.status = 'published'; changeSet.publishedBaselineId = baseline.id; return baseline;
  }
  function markTasksForReview(p, changedIds, changeSetId) {
    const impacted = [];
    (p.contextPackages || []).forEach(pack => {
      const ids = Object.keys(pack.versionBinding?.entries || {}).concat(Object.keys(pack.versionBinding?.assertions || {}));
      const flowSemanticChanged = Object.keys(pack.versionBinding?.flows || {}).some(flowId => {
        if (!changedIds.has(flowId)) return false;
        const before = pack.versionBinding.flowSemantics?.[flowId];
        // Legacy packs have no fingerprint and must be reviewed conservatively. New packs
        // distinguish layout-only revisions from real node/edge/decision changes.
        return !before || before !== flowSemanticFingerprint(p.publishedFlows[flowId]);
      });
      if (ids.some(id => changedIds.has(id)) || flowSemanticChanged) { pack.reviewStatus = 'requires-review'; pack.valid = false; pack.staleReason = `知识 ${changeSetId} 已发布新修订${flowSemanticChanged ? '，流程语义发生变化' : ''}`; const task = p.tasks.find(t => t.id === pack.taskId); if (task) { task.status = 'needs-review'; task.reviewReason = pack.staleReason; impacted.push(task.id); } }
    });
    (p.tasks || []).forEach(task => { if (task.status !== 'completed' && task.knowledgeIds?.some(id => changedIds.has(id)) && !impacted.includes(task.id)) { task.status = 'needs-review'; impacted.push(task.id); } });
    return impacted;
  }
  function publishRevision(p, input = {}) {
    W.assert(input.context, 'baseline:publish');
    if (!input.__atomic) {
      const staging = clone(p);
      try {
        const result = publishRevision(staging, { ...input, __atomic: true });
        if (result.ok) {
          const originalPackages = p.contextPackages || [], originalTasks = p.tasks || [];
          originalPackages.forEach(old => { const next = staging.contextPackages.find(pack => pack.packageId === old.packageId); if (next) { Object.keys(old).forEach(key => delete old[key]); Object.assign(old, next); } });
          originalTasks.forEach(old => { const next = staging.tasks.find(task => task.id === old.id); if (next) { Object.keys(old).forEach(key => delete old[key]); Object.assign(old, next); } });
          staging.contextPackages = originalPackages; staging.tasks = originalTasks;
          Object.keys(p).forEach(key => delete p[key]); Object.assign(p, staging);
        }
        return result;
      } catch (error) { return { ok: false, errors: [error.message] }; }
    }
    const ws = workspace(p, false); if (!ws || ws.status !== 'pending') return { ok: false, errors: ['变更集尚未提交或已经发布'] };
    if (!String(input.confirmedBy || '').trim()) return { ok: false, errors: ['请填写审核确认人'] };
    const validation = validateWorkspace(p); if (!validation.ok) return { ok: false, errors: validation.errors };
    const deliveryLinks = ws.deliveryLinks || [];
    deliveryLinks.forEach(link => {
      const decision = (p.decisionRecords || []).find(record => record.decisionId === link.proposalId && record.taskId === link.taskId);
      if (!decision || decision.approval?.decision !== 'approved') throw new Error(`业务决定 ${link.proposalId} 尚未由 Owner 批准，不能发布关联知识`);
      const draft = ws.entities[link.ruleId], current = G.getEntity(p, link.ruleId);
      if (!draft || current?.currentVersion !== link.baseVersion) throw new Error(`${link.ruleId} 的当前版本已不再匹配候选基线 ${link.baseVersion}，请重新提出变化`);
      if (!p.tasks.some(task => task.id === link.taskId)) throw new Error(`交付关联任务 ${link.taskId} 不存在`);
    });
    const changedIds = new Set(), newEntities = {};
    Object.values(ws.entities).forEach(draft => {
      const previous = G.getEntity(p, draft.id), baseRevision = previous?.revision || null, revision = draft.status === 'retired' ? previous?.revision || 'r1' : baseRevision ? G.nextRevision(baseRevision) : 'r1';
      if (previous && baseRevision) p.knowledgeRevisions[`${draft.id}@${baseRevision}`] ||= G.deepFreeze(clone(previous));
      const entity = clone(draft); entity.revision = revision; entity.status = draft.status === 'retired' ? 'retired' : 'published'; entity.publishedAt = now();
      if (entity.typeId === 'Rule' && entity.status !== 'retired') { const version = ruleVersion(draft); entity.currentVersion = revision; entity.versions = { ...(entity.versions || {}), [revision]: { ...version, version: revision, status: 'published' } }; delete entity.versions.draft; }
      if (entity.typeId === 'BusinessObject') (entity.transitions || []).forEach(t => { if (t.verificationStatus !== 'confirmed') { t.sourceRefs = [...(t.sourceRefs || []), { kind: 'human_confirmation', resourceId: `HUMAN-${ws.id}`, locator: `${ws.id} 审核状态转移`, confirmedBy: input.confirmedBy }]; t.verificationStatus = 'confirmed'; } });
      newEntities[entity.id] = G.deepFreeze(entity); p.knowledgeRevisions[`${entity.id}@${revision}`] = newEntities[entity.id]; changedIds.add(entity.id);
    });
    Object.assign(p.publishedEntities, newEntities);
    const newAssertions = new Map((p.assertions || []).map(a => [a.assertionId, a]));
    Object.values(ws.assertions).forEach(draft => {
      const previous = (p.assertions || []).find(a => a.assertionId === draft.assertionId), revision = draft.status === 'retired' ? previous?.revision || 'a1' : previous ? G.nextRevision(previous.revision) : 'a1', a = clone(draft);
      if (previous) p.assertionRevisionStore[`${a.assertionId}@${previous.revision}`] ||= G.deepFreeze(clone(previous));
      a.revision = revision; a.status = draft.status === 'retired' ? 'retired' : 'published';
      if (a.status !== 'retired') { a.sourceRefs ||= []; if (!a.sourceRefs.length) a.sourceRefs.push({ kind: 'human_confirmation', resourceId: `HUMAN-${ws.id}`, locator: `${ws.id} 审核语义关联`, confirmedBy: input.confirmedBy }); if (!a.sourceRefs.some(r => r.kind === 'human_confirmation')) a.sourceRefs.push({ kind: 'human_confirmation', resourceId: `HUMAN-${ws.id}`, locator: `${ws.id} 人工复核`, confirmedBy: input.confirmedBy }); const sourceProblems = validateSourceRefs(p, a.sourceRefs, { forConfirmed: true }); const unresolvedFact = ['unknown', 'conflicting'].includes(a.factKind) || a.verificationStatus === 'conflicted'; a.verificationStatus = unresolvedFact ? (a.factKind === 'conflicting' ? 'conflicted' : 'unverified') : sourceProblems.length ? 'unverified' : 'confirmed'; a.owner = unresolvedFact || sourceProblems.length ? '' : input.confirmedBy; }
      const errors = validateAssertion(p, a, { draft: false }); if (errors.length && a.status !== 'retired') throw new Error(`${a.assertionId}: ${errors.join('；')}`);
      p.assertionRevisionStore[`${a.assertionId}@${revision}`] = G.deepFreeze(clone(a)); p.knowledgeRevisions[`${a.assertionId}@${revision}`] = G.deepFreeze({ ...clone(a), id: a.assertionId, typeId: 'Assertion', name: `${a.subject} ${a.predicate} ${a.object || ''}`, description: a.explanation || '' });
      newAssertions.set(a.assertionId, G.deepFreeze(a)); changedIds.add(a.assertionId);
    });
    p.assertions = [...newAssertions.values()];
    Object.values(ws.flows).forEach(draft => { const previous = Object.values(p.publishedFlows).find(f => f.id === draft.id), revision = previous ? G.nextRevision(previous.revision) : 'r1', flow = G.deepFreeze({ ...clone(draft), revision, status: 'published', publishedAt: now() }); if (previous) p.flowRevisions[`${previous.id}@${previous.revision}`] ||= G.deepFreeze(clone(previous)); p.flowRevisions[`${flow.id}@${revision}`] = flow; p.publishedFlows[flow.id] = flow; changedIds.add(flow.id); });
    ws.reviewDecisions.push({ by: input.confirmedBy, decision: 'approved', at: now(), comment: input.comment || '已审核变更集中的实体、断言、流程、来源与适用范围' }); ws.undoStack = []; ws.status = 'published'; ws.publishedAt = now();
    const baseline = createBaseline(p, ws, input.confirmedBy, input.context), impactedTasks = markTasksForReview(p, changedIds, ws.id), publishedRevisions = Object.fromEntries(Object.values(newEntities).map(entity => [entity.id, { revision: entity.revision, ruleVersion: entity.typeId === 'Rule' ? entity.currentVersion : undefined }]));
    deliveryLinks.forEach(link => {
      const task = p.tasks.find(item => item.id === link.taskId), published = publishedRevisions[link.ruleId], knowledgeId = link.knowledgeChangeId || `KC-SIM-${task.id}`, manifestId = link.manifestId || `KDM-SIM-${task.id}`;
      const change = (task.delivery?.knowledgeChanges || []).find(item => item.id === knowledgeId);
      if (change) Object.assign(change, { status: 'approved', changeSetId: ws.id, approvedBaselineId: baseline.id, publishedRefs: [{ ruleId: link.ruleId, version: published.ruleVersion }] });
      if (task.delivery?.deliveryManifest?.id === manifestId) Object.assign(task.delivery.deliveryManifest, { approvedBaselineId: baseline.id, reconciliation: 'reconciled' });
      (p.knowledgeDeliveryManifests || []).filter(manifest => manifest.id === manifestId).forEach(manifest => Object.assign(manifest, { approvedBaselineId: baseline.id, reconciliation: 'reconciled' }));
      if (task.delivery) task.delivery.approvedBusinessBaselineId = baseline.id;
      const delivery = task.semanticContract?.knowledgeDelivery;
      if (delivery) {
        delivery.targetBaselineStatus = 'approved'; delivery.changeSetId = ws.id; delivery.approvedBaselineId = baseline.id;
        delivery.updateRefs = (delivery.updateRefs || []).map(ref => typeof ref === 'string' ? { ruleId: ref, publishedVersion: published.ruleVersion, baselineId: baseline.id, changeSetId: ws.id } : { ...ref, publishedVersion: published.ruleVersion, baselineId: baseline.id, changeSetId: ws.id });
      }
      (task.impactInvestigation?.items || []).filter(item => item.category === 'knowledge' && item.status === 'required').forEach(item => Object.assign(item, { completion: 'verified', checkedBy: input.confirmedBy, evidenceRefs: [...new Set([...(item.evidenceRefs || []), ws.id, baseline.id])] }));
      task.deliveryState = 'knowledge-approved-contract-review-required'; task.history.push({ at: now(), event: 'knowledge-published', proposalId: link.proposalId, changeSetId: ws.id, baselineId: baseline.id, published });
    });
    const changeSetIndex = p.changeSets.findIndex(cs => cs.id === ws.id); if (changeSetIndex >= 0) p.changeSets[changeSetIndex] = ws;
    p.draftWorkspace = null; return { ok: true, changeSet: ws, baseline, impactedTasks, publishedRevisions };
  }
  function createIssue(p, input = {}) {
    W.assert(input.context, 'issue:write');
    if (!input.title?.trim() || !input.objectId) throw new Error('问题描述和关联知识不能为空');
    const issue = { id: input.id || `Q-${p.meta.id}-${String((p.issues?.length || 0) + 1).padStart(4, '0')}`, projectId: p.meta.id, taskId: input.taskId || null, contractId: input.contractId || null, objectId: input.objectId, title: input.title, location: input.location || '', sourceRefs: clone(input.sourceRefs || []), description: input.description || input.title, impact: input.impact || '', assignee: input.assignee || '业务负责人', classification: input.classification || 'pending', status: 'waiting', decision: null, changeSetId: null, replies: [], createdAt: now(), updatedAt: now() };
    p.issues.unshift(issue); const task = p.tasks.find(t => t.id === issue.taskId); if (task) { task.issueIds ||= []; task.issueIds.push(issue.id); task.status = 'blocked'; } return { ok: true, issue, summary: '问题已关联原任务与知识' };
  }
  function decideIssue(p, issueId, input = {}) {
    W.assert(input.context, 'decision:approve');
    const issue = p.issues.find(i => i.id === issueId); if (!issue || issue.status === 'resolved') throw new Error('问题不存在或已关闭');
    if (!input.decision || !input.classification) throw new Error('请选择问题分类和处理决定');
    issue.classification = input.classification; issue.decision = input.decision; issue.decisionAction = input.action; issue.decidedBy = input.by || '业务负责人'; issue.decidedAt = now(); issue.status = input.action === 'pause' ? 'paused' : 'decided'; issue.replies.push({ by: issue.decidedBy, text: input.decision, at: now() });
    if (input.action === 'change-knowledge') { const ws = workspace(p); ws.reason = `处理问题 ${issue.id}：${input.decision}`; ws.issueIds = [...new Set([...ws.issueIds, issue.id])]; issue.changeSetId = ws.id; }
    if (input.action === 'implementation-task') { const task = createTask(p, { rootId: issue.objectId, title: `实现修复：${issue.title}`, purpose: 'development' }); issue.implementationTaskId = task.id; }
    issue.updatedAt = now(); return { ok: true, issue };
  }
  function createTask(p, input = {}, context = null) {
    W.assert(context, 'task:create');
    const root = input.rootId ? getEntity(p, input.rootId) : null; if (input.rootId && !root) throw new Error('任务根知识不存在');
    const task = { id: input.id || `TASK-${p.meta.id}-${String((p.tasks?.length || 0) + 1).padStart(4, '0')}`, projectId: p.meta.id, rootId: root?.id || null, knowledgeIds: root ? [root.id] : [], title: input.title || (root ? `实现${root.name}` : '待澄清需求'), purpose: input.purpose || 'development', status: 'draft', activeContractId: null, contractIds: [], issueIds: [], acceptance: [], history: [{ at: now(), by: input.by || '当前用户', event: 'created' }], createdAt: now(), updatedAt: now() };
    p.tasks.unshift(task); return task;
  }
  return {
    clone, now, COMMANDS, PREFIX, structuralPredicates, initProject, initProjects, createProjectSet, createBlankProject, workspace, baselineById,
    getEntity, allEntities, getAssertion, getAssertions, getFlow, nextId, ruleVersion, flowSemanticFingerprint, validateSourceRefs, validateAssertion, validateWorkspace,
    execute, proposeChange, publishRevision, createBaseline, markTasksForReview, createIssue, decideIssue, createTask
  };
});
