/* Business Atlas V9 — Atlas Knowledge Package (AKP/0.1) parser, validator, preview and atomic importer. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const K = commonjs ? require('../v4-core.js') : root.AtlasCore;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const M = commonjs ? require('./v9-model.js') : root.AtlasV9Model;
  const Access = commonjs ? require('./v9-access.js') : root.AtlasV9Access;
  const W = commonjs ? require('./write-access.js') : root.AtlasWriteAccess;
  const api = factory(K, G, C, M, Access, W);
  if (commonjs) module.exports = api;
  root.AtlasV9AKP = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (K, G, C, M, Access, W) {
  'use strict';
  const clone = C.clone, FACT_LEVELS = ['observed', 'documented_intent', 'inferred', 'unknown', 'conflicting'];
  const MAX_BYTES = 8 * 1024 * 1024, MAX_ENTITIES = 5000, MAX_ASSERTIONS = 20000;
  const text = (value, max = 20000) => typeof value === 'string' && value.length <= max && !value.includes('\0');
  function hash(value) { let h = 2166136261; const input = typeof value === 'string' ? value : JSON.stringify(value); for (let i = 0; i < input.length; i++) { h ^= input.charCodeAt(i); h = Math.imul(h, 16777619); } return `fnv1a-${(h >>> 0).toString(16).padStart(8, '0')}-${input.length}`; }
  function inspectUntrusted(value, path = '$', errors = []) {
    if (errors.length > 100) return errors;
    if (value && typeof value === 'object') Object.entries(value).forEach(([key, child]) => {
      if (['__proto__', 'prototype', 'constructor'].includes(key)) errors.push(`${path}.${key}: 不允许原型污染字段`);
      if (typeof child === 'string' && child.length > 100000) errors.push(`${path}.${key}: 单个文本字段过长`);
      if (/(?:javascript:|onerror\s*=|onload\s*=)/i.test(child || '')) errors.push(`${path}.${key}: 检测到不可执行内容标记；将作为危险输入拒绝`);
      inspectUntrusted(child, `${path}.${key}`, errors);
    });
    return errors;
  }
  function parse(input) {
    try {
      const raw = typeof input === 'string' ? input : JSON.stringify(input);
      if (new Blob([raw]).size > MAX_BYTES && typeof Blob !== 'undefined') return { ok: false, errors: [`知识包超过 ${MAX_BYTES / 1024 / 1024} MiB 原型上限，请拆分导入`] };
      if (raw.length * 2 > MAX_BYTES * 2) return { ok: false, errors: ['知识包超过原型容量上限'] };
      const pack = typeof input === 'string' ? JSON.parse(input) : clone(input);
      const errors = inspectUntrusted(pack);
      return errors.length ? { ok: false, errors } : { ok: true, package: pack, contentHash: hash(raw) };
    } catch (error) { return { ok: false, errors: [`AKP JSON 无法解析：${error.message}`] }; }
  }
  function validatePackage(pack, project) {
    const errors = [], warnings = [], add = (path, message) => errors.push(`${path}: ${message}`);
    if (!pack || typeof pack !== 'object' || Array.isArray(pack)) return { ok: false, errors: ['$: 根节点必须是对象'], warnings };
    const manifest = pack.manifest;
    if (!manifest) add('manifest', '缺少清单');
    else {
      if (manifest.formatVersion !== 'AKP/0.1') add('manifest.formatVersion', '仅支持 AKP/0.1');
      ['packageId', 'sourceProjectId', 'projectName', 'generatedAt', 'sourceRevision', 'importPurpose'].forEach(key => { if (!text(manifest[key], 500)) add(`manifest.${key}`, '不能为空且必须是文本'); });
      if (!/^AKP-[A-Za-z0-9._-]+$/.test(manifest.packageId || '')) add('manifest.packageId', '需要 AKP- 前缀和稳定字符');
      if (Number.isNaN(Date.parse(manifest.generatedAt))) add('manifest.generatedAt', '必须是有效时间');
      if (!manifest.producer?.identity || !['external-agent', 'human', 'tool'].includes(manifest.producer.kind)) add('manifest.producer', '必须声明独立生产者，不绑定模型或 MCP Provider');
      if (!Array.isArray(manifest.declaredScope) || !manifest.declaredScope.length) add('manifest.declaredScope', '至少声明一个导入范围');
      if (project?.externalProjectId && project.externalProjectId !== manifest.sourceProjectId) add('manifest.sourceProjectId', `与目标项目归属 ${project.externalProjectId} 不一致`);
    }
    if (!Array.isArray(pack.entities) || pack.entities.length > MAX_ENTITIES) add('entities', `必须是数组且不超过 ${MAX_ENTITIES} 项`);
    if (!Array.isArray(pack.assertions) || pack.assertions.length > MAX_ASSERTIONS) add('assertions', `必须是数组且不超过 ${MAX_ASSERTIONS} 项`);
    if (!Array.isArray(pack.sources)) add('sources', '必须是数组');
    ['businessStructures', 'technicalStructures', 'importProvenance'].forEach(key => { if (!pack[key] || typeof pack[key] !== 'object') add(key, '必须存在且为对象'); });
    if (!Array.isArray(pack.unknownsAndConflicts)) add('unknownsAndConflicts', '必须是数组，未知不能省略或静默删除');
    const entityIds = new Set(), sourceIds = new Set(), assertionIds = new Set();
    (pack.sources || []).forEach((source, index) => {
      const path = `sources[${index}]`;
      if (!text(source.sourceId, 200)) add(path, '缺少 sourceId'); else if (sourceIds.has(source.sourceId)) add(path, `重复来源 ${source.sourceId}`); else sourceIds.add(source.sourceId);
      if (!text(source.sourceType, 100) || !text(source.path || source.uri, 2000) || !text(source.revision, 500) || !text(source.captureMethod, 500)) add(path, '来源类型、路径、版本和采集方式必填');
      if (source.excerpt && !text(source.excerpt, 20000)) add(`${path}.excerpt`, '摘录无效或过长');
      if (source.lineRange && (!Number.isInteger(source.lineRange.start) || !Number.isInteger(source.lineRange.end) || source.lineRange.start < 1 || source.lineRange.end < source.lineRange.start)) add(`${path}.lineRange`, '行范围无效');
      if (source.sensitive) warnings.push(`${path}: 声明含敏感信息，导入前需要确认权限与导出边界`);
    });
    (pack.entities || []).forEach((entity, index) => {
      const path = `entities[${index}]`;
      if (!text(entity.stableExternalId, 300)) add(path, '缺少稳定外部 ID'); else if (entityIds.has(entity.stableExternalId)) add(path, `重复外部 ID ${entity.stableExternalId}`); else entityIds.add(entity.stableExternalId);
      if (!/^[A-Za-z][A-Za-z0-9]+$/.test(entity.typeId || '')) add(path, 'typeId 无效');
      if (project && !K.schemaFor(project, entity.typeId)) add(path, `目标项目未注册类型 ${entity.typeId}`);
      if (!text(entity.name, 1000)) add(path, '名称必填');
      if (!entity.properties || typeof entity.properties !== 'object' || Array.isArray(entity.properties)) add(path, 'properties 必须为对象');
      if (!Array.isArray(entity.categoryAssignments) || !entity.applicableContext || typeof entity.applicableContext !== 'object') add(path, '分类和适用上下文必须为结构对象');
      if (!FACT_LEVELS.includes(entity.verificationStatus)) add(path, '事实等级必须是 observed / documented_intent / inferred / unknown / conflicting');
      (entity.sourceIds || []).forEach(id => { if (!sourceIds.has(id)) add(path, `引用了未声明来源 ${id}`); });
    });
    (pack.assertions || []).forEach((assertion, index) => {
      const path = `assertions[${index}]`;
      if (!text(assertion.stableExternalId, 300)) add(path, '缺少稳定外部 ID'); else if (assertionIds.has(assertion.stableExternalId)) add(path, `重复断言外部 ID ${assertion.stableExternalId}`); else assertionIds.add(assertion.stableExternalId);
      if (!entityIds.has(assertion.subject)) add(path, `主语外部引用无效 ${assertion.subject}`);
      if (assertion.object === undefined && assertion.value === undefined) add(path, '必须具有 object 或 value');
      if (assertion.object !== undefined && !entityIds.has(assertion.object)) add(path, `宾语外部引用无效 ${assertion.object}`);
      if (!G.PREDICATES[assertion.predicate]) add(path, `关系 ${assertion.predicate} 未注册`);
      if (!FACT_LEVELS.includes(assertion.verificationStatus)) add(path, '事实等级无效');
      if (!Array.isArray(assertion.sourceRefs)) add(path, 'sourceRefs 必须为数组');
      (assertion.sourceRefs || []).forEach(ref => { if (!sourceIds.has(ref.sourceId)) add(path, `来源引用无效 ${ref.sourceId}`); if (ref.locator === undefined || ref.locator === null || ref.locator === '') add(path, `来源 ${ref.sourceId} 缺少精确定位`); });
      if (assertion.verificationStatus === 'inferred') warnings.push(`${path}: 推断只形成待核验提案`);
    });
    const business = pack.businessStructures || {}, technical = pack.technicalStructures || {};
    ['actions', 'decisions', 'events', 'rules', 'constraints'].forEach(key => (business[key] || []).forEach(id => { if (!entityIds.has(id)) add(`businessStructures.${key}`, `外部引用 ${id} 不存在`); }));
    (business.flows || []).forEach((flow, index) => {
      const path = `businessStructures.flows[${index}]`;
      if (!entityIds.has(flow.scenarioId)) add(path, `场景引用 ${flow.scenarioId} 无效`);
      const nodeIds = new Set();
      (flow.nodes || []).forEach(node => { if (!node.id || nodeIds.has(node.id)) add(path, `节点 ID 重复或为空 ${node.id}`); nodeIds.add(node.id); ['actionId', 'ruleId', 'eventId'].filter(key => node[key]).forEach(key => { if (!entityIds.has(node[key])) add(path, `节点 ${node.id} 的 ${key} 引用无效`); }); });
      (flow.edges || []).forEach(edge => { if (!nodeIds.has(edge.from) || !nodeIds.has(edge.to)) add(path, `边 ${edge.id} 的端点无效`); });
    });
    (business.transitions || []).forEach((transition, index) => { if (!entityIds.has(transition.objectId)) add(`businessStructures.transitions[${index}]`, '业务对象引用无效'); if (!entityIds.has(transition.actionRef)) add(`businessStructures.transitions[${index}]`, '动作引用无效'); if (!transition.triggerEvent || !transition.guard) add(`businessStructures.transitions[${index}]`, '触发事件与守卫必填'); });
    ['systems', 'components', 'frameworks', 'dependencies', 'apis', 'dataEntities', 'configurations', 'environments', 'architectureDecisions'].forEach(key => (technical[key] || []).forEach(id => { if (!entityIds.has(id)) add(`technicalStructures.${key}`, `外部引用 ${id} 不存在`); }));
    (pack.unknownsAndConflicts || []).forEach((item, index) => {
      if (!item.id || !item.title || !['unknown', 'conflict', 'missing-source', 'unverified-inference', 'question'].includes(item.kind)) add(`unknownsAndConflicts[${index}]`, '未知或冲突记录缺少有效 ID、标题或类型');
      (item.relatedExternalIds || []).forEach(id => { if (!entityIds.has(id) && !assertionIds.has(id)) add(`unknownsAndConflicts[${index}]`, `关联外部 ID ${id} 不存在`); });
    });
    const provenance = pack.importProvenance || {};
    if (!provenance.agentRunId || !Array.isArray(provenance.toolExecutionRefs) || !provenance.extractedAt || !provenance.extractionStrategy) add('importProvenance', 'Agent Run、工具引用、提取时间和策略必填');
    return { ok: !errors.length, errors: [...new Set(errors)], warnings: [...new Set(warnings)], counts: { entities: pack.entities?.length || 0, assertions: pack.assertions?.length || 0, sources: pack.sources?.length || 0, unknownsAndConflicts: pack.unknownsAndConflicts?.length || 0 } };
  }
  function safeId(externalId) { return String(externalId).replace(/[^A-Za-z0-9._-]/g, '-').replace(/-+/g, '-').slice(0, 120); }
  function mappings(project, pack) {
    const sourceProject = pack.manifest.sourceProjectId, existing = project.externalIdMap?.[sourceProject] || {}, map = {};
    [...pack.sources, ...pack.entities].forEach(item => { const external = item.sourceId || item.stableExternalId; map[external] = existing[external] || safeId(external); });
    return map;
  }
  function previewImport(project, pack, validation) {
    const map = mappings(project, pack), current = C.allEntities(project, { draft: true, includeRetired: true }), byId = new Map(current.map(entity => [entity.id, entity]));
    const entities = pack.entities.map(entity => {
      const id = map[entity.stableExternalId], existing = byId.get(id), candidate = { ...clone(entity), id };
      const same = existing && JSON.stringify({ name: existing.name, description: existing.description, props: existing.props }) === JSON.stringify({ name: entity.name, description: entity.description || '', props: entity.properties });
      const duplicates = current.filter(item => item.id !== id && item.name.trim().toLocaleLowerCase() === entity.name.trim().toLocaleLowerCase()).map(item => item.id);
      return { externalId: entity.stableExternalId, id, action: !existing ? 'create' : same ? 'unchanged' : 'update-candidate', potentialDuplicates: duplicates, candidate, existing: existing ? clone(existing) : null };
    });
    const assertionExternal = project.externalAssertionMap?.[pack.manifest.sourceProjectId] || {};
    const assertions = pack.assertions.map(assertion => {
      const id = assertionExternal[assertion.stableExternalId] || `AST-${safeId(assertion.stableExternalId)}`, existing = assertionExternal[assertion.stableExternalId] ? C.getAssertion(project, id, { draft: true }) : null;
      const same = existing && existing.subject === map[assertion.subject] && existing.predicate === assertion.predicate && (existing.object || undefined) === (assertion.object ? map[assertion.object] : undefined) && JSON.stringify(existing.qualifiers || {}) === JSON.stringify({ ...clone(assertion.qualifiers), applicableContext: clone(assertion.qualifiers?.applicableContext || {}) }) && (assertion.sourceRefs || []).every(ref => (existing.sourceRefs || []).some(actual => actual.resourceId === map[ref.sourceId] && JSON.stringify(actual.locator) === JSON.stringify(ref.locator)));
      return { externalId: assertion.stableExternalId, id, action: !existing ? 'create' : same ? 'unchanged' : 'update-candidate', candidate: clone(assertion), existing: existing ? clone(existing) : null };
    });
    return { packageId: pack.manifest.packageId, map, entities, assertions, summary: { create: entities.filter(item => item.action === 'create').length + assertions.filter(item => item.action === 'create').length, updates: entities.filter(item => item.action === 'update-candidate').length + assertions.filter(item => item.action === 'update-candidate').length, unchanged: entities.filter(item => item.action === 'unchanged').length, duplicates: entities.filter(item => item.potentialDuplicates.length).length, conflicts: pack.unknownsAndConflicts.filter(item => item.kind === 'conflict').length, unknowns: pack.unknownsAndConflicts.filter(item => item.kind !== 'conflict').length, invalidReferences: validation.errors.length }, validation };
  }
  function stagePackage(project, input, options = {}) {
    W.assert(options.context, 'knowledge:draft');
    M.initProject(project);
    const parsed = parse(input); if (!parsed.ok) return parsed;
    const validation = validatePackage(parsed.package, project);
    const proposalId = `IMP-${project.meta.id}-${String((project.importProposals?.length || 0) + 1).padStart(4, '0')}`;
    const preview = previewImport(project, parsed.package, validation);
    const proposal = { proposalId, projectId: project.meta.id, packageId: parsed.package.manifest?.packageId || 'unknown', contentHash: parsed.contentHash, status: validation.ok ? 'previewed' : 'invalid', package: parsed.package, preview, validation, decisions: {}, createdAt: C.now(), updatedAt: C.now(), relatedKnowledgeIds: preview.entities.map(item => item.id) };
    project.importProposals.unshift(proposal); Access.touch(project, 'import.changed', { proposalId });
    return { ok: validation.ok, proposal, validation, preview };
  }
  function replaceProject(target, staging) { Object.keys(target).forEach(key => delete target[key]); Object.assign(target, staging); }
  function commitImport(project, proposalId, options = {}) {
    W.assert(options.context, 'knowledge:draft');
    const execute = (staging, command, input) => C.execute(staging, command, input, options.context);
    const original = (project.importProposals || []).find(item => item.proposalId === proposalId);
    if (!original) return { ok: false, errors: ['导入提案不存在'] };
    if (original.status === 'invalid') return { ok: false, errors: original.validation.errors };
    if (original.status === 'committed' && original.contentHash) {
      const currentPreview = previewImport(project, original.package, validatePackage(original.package, project));
      if (!currentPreview.summary.create && !currentPreview.summary.updates) return { ok: true, idempotent: true, proposal: original, summary: currentPreview.summary };
    }
    const staging = clone(project), proposal = staging.importProposals.find(item => item.proposalId === proposalId), pack = proposal.package;
    try {
      const validation = validatePackage(pack, staging); if (!validation.ok) throw new Error(validation.errors.join('；'));
      const preview = previewImport(staging, pack, validation), map = preview.map, sourceProject = pack.manifest.sourceProjectId;
      const startingOperationCount = staging.draftWorkspace?.operations.length || 0, startingUnknownCount = (staging.unknowns?.length || 0) + (staging.conflictRecords?.length || 0);
      staging.externalIdMap[sourceProject] ||= {}; staging.externalAssertionMap ||= {}; staging.externalAssertionMap[sourceProject] ||= {};
      const accepted = externalId => options.decisions?.[externalId] !== 'reject' && proposal.decisions?.[externalId] !== 'reject';
      pack.sources.forEach(source => {
        if (!accepted(source.sourceId)) return;
        const id = map[source.sourceId], existing = C.getEntity(staging, id, { draft: true });
        const props = { resourceType: source.sourceType, uri: source.uri || `file://${source.path}`, locator: { path: source.path, lineRange: source.lineRange || null, section: source.section || null, anchor: source.anchor || null }, sourceVersion: source.revision, contentHash: source.contentHash || null, summary: source.excerpt || '', captureMethod: source.captureMethod, sensitive: !!source.sensitive, parseStatus: 'structured', owner: pack.manifest.producer.identity, trust: source.sensitive ? 'unverified' : 'confirmed' };
        if (existing && JSON.stringify(existing.props || {}) === JSON.stringify(props)) { staging.externalIdMap[sourceProject][source.sourceId] = id; return; }
        if (!existing) execute(staging, 'CreateEntity', { id, typeId: 'Resource', name: `${source.path}${source.lineRange ? `:${source.lineRange.start}-${source.lineRange.end}` : ''}`, description: `AKP 来源 ${source.sourceId}`, props, sensitivity: source.sensitive ? 'sensitive' : 'internal' });
        else execute(staging, 'SetProperty', { entityId: id, path: 'props', value: props });
        staging.externalIdMap[sourceProject][source.sourceId] = id;
      });
      pack.entities.forEach(entity => {
        if (!accepted(entity.stableExternalId)) return;
        const id = map[entity.stableExternalId], existing = C.getEntity(staging, id, { draft: true }), change = preview.entities.find(item => item.externalId === entity.stableExternalId);
        if (change?.action === 'unchanged') { staging.externalIdMap[sourceProject][entity.stableExternalId] = id; return; }
        const sourceIds = (entity.sourceIds || []).filter(accepted).map(id => map[id]);
        const extra = { ...(entity.extra || {}), applicableContext: clone(entity.applicableContext), verificationStatus: entity.verificationStatus, factKind: entity.verificationStatus, stableExternalId: entity.stableExternalId, importPackageId: pack.manifest.packageId };
        if (!existing) execute(staging, 'CreateEntity', { id, typeId: entity.typeId, name: entity.name, description: entity.description || '', props: clone(entity.properties), extra, sourceIds, sensitivity: entity.sensitivity || 'internal', permissions: entity.permissions });
        else {
          execute(staging, 'SetProperty', { entityId: id, path: 'name', value: entity.name });
          execute(staging, 'SetProperty', { entityId: id, path: 'description', value: entity.description || '' });
          execute(staging, 'SetProperty', { entityId: id, path: 'props', value: clone(entity.properties) });
          execute(staging, 'SetProperty', { entityId: id, path: 'sourceIds', value: sourceIds });
          execute(staging, 'SetProperty', { entityId: id, path: 'applicableContext', value: clone(entity.applicableContext) });
          execute(staging, 'SetProperty', { entityId: id, path: 'factKind', value: entity.verificationStatus });
        }
        const draft = staging.draftWorkspace.entities[id]; if (draft) { draft.props = clone(entity.properties); draft.categoryAssignments = entity.categoryAssignments.map(categoryId => ({ categoryId, origin: 'akp', assignmentStatus: 'confirmed', sourceRef: proposalId })); }
        staging.externalIdMap[sourceProject][entity.stableExternalId] = id;
      });
      pack.assertions.forEach(assertion => {
        if (!accepted(assertion.stableExternalId) || !accepted(assertion.subject) || (assertion.object && !accepted(assertion.object))) return;
        const id = staging.externalAssertionMap[sourceProject][assertion.stableExternalId] || `AST-${safeId(assertion.stableExternalId)}`;
        if (C.getAssertion(staging, id, { draft: true })) return;
        const sourceRefs = assertion.sourceRefs.filter(ref => accepted(ref.sourceId)).map(ref => ({ resourceId: map[ref.sourceId], locator: clone(ref.locator), excerpt: ref.excerpt || '' }));
        const created = execute(staging, 'LinkKnowledge', { assertionId: id, subject: map[assertion.subject], predicate: assertion.predicate, ...(assertion.object ? { object: map[assertion.object] } : { value: clone(assertion.value) }), qualifiers: { ...clone(assertion.qualifiers), applicableContext: clone(assertion.qualifiers?.applicableContext || {}) }, sourceRefs, factKind: assertion.verificationStatus, validTime: clone(assertion.validTime || { from: null, to: null }), explanation: assertion.explanation || '' }).assertion;
        const draft = staging.draftWorkspace.assertions[created.assertionId]; draft.factKind = assertion.verificationStatus; draft.verificationStatus = assertion.verificationStatus === 'conflicting' ? 'conflicted' : 'unverified'; draft.stableExternalId = assertion.stableExternalId; draft.importPackageId = pack.manifest.packageId;
        staging.externalAssertionMap[sourceProject][assertion.stableExternalId] = created.assertionId;
      });
      const business = pack.businessStructures || {};
      (business.flows || []).forEach(imported => {
        const flow = { ...clone(imported), scenarioId: map[imported.scenarioId], nodes: (imported.nodes || []).map(node => ({ ...clone(node), actionId: node.actionId ? map[node.actionId] : null, ruleId: node.ruleId ? map[node.ruleId] : null, eventId: node.eventId ? map[node.eventId] : null })), edges: clone(imported.edges || []), sourceRefs: (imported.sourceRefs || []).map(ref => ({ resourceId: map[ref.sourceId], locator: clone(ref.locator) })), revision: 'r0', status: 'draft' };
        const existing = Object.values(staging.draftWorkspace?.flows || {}).find(item => item.id === flow.id) || C.getFlow(staging, flow.id, { baselineId: staging.versionLayers.baseline });
        if (existing && JSON.stringify({ nodes: existing.nodes, edges: existing.edges }) === JSON.stringify({ nodes: flow.nodes, edges: flow.edges })) return;
        execute(staging, 'DefineFlowStructure', { flow });
      });
      (business.transitions || []).forEach(imported => {
        const existingObject = C.getEntity(staging, map[imported.objectId], { draft: true });
        if (existingObject?.transitions?.some(item => item.id === imported.id && item.fromState === imported.fromState && item.toState === imported.toState)) return;
        execute(staging, 'DefineTransition', { id: imported.id, objectId: map[imported.objectId], fromState: imported.fromState, toState: imported.toState, triggerEvent: imported.triggerEvent, guard: imported.guard, actionRef: map[imported.actionRef], effect: imported.effect || '', sourceRefs: (imported.sourceRefs || []).map(ref => ({ resourceId: map[ref.sourceId], locator: clone(ref.locator) })) });
        const transition = staging.draftWorkspace.entities[map[imported.objectId]].transitions.find(item => item.id === imported.id); if (transition) transition.factKind = imported.verificationStatus || 'observed';
      });
      pack.unknownsAndConflicts.forEach(item => {
        const relatedIds = (item.relatedExternalIds || []).map(id => map[id] || staging.externalAssertionMap[sourceProject][id]).filter(Boolean);
        if (item.kind === 'conflict') {
          if (!staging.conflictRecords.some(record => record.id === item.id)) staging.conflictRecords.push({ id: item.id, title: item.title, description: item.description || '', status: 'open', assertionIds: relatedIds.filter(id => id.startsWith('AST-')), relatedKnowledgeIds: relatedIds, importProposalId: proposalId, resolution: null });
        } else if (!staging.unknowns.some(unknown => unknown.id === item.id)) staging.unknowns.push({ id: item.id, title: item.title, impact: item.description || '', relatedIds, blocksFormal: !!item.blocksFormal, status: 'open', owner: '待指定', kind: item.kind, importProposalId: proposalId });
      });
      const technical = pack.technicalStructures || {}, previousTechnicalSignature = JSON.stringify(staging.versionLayers || {});
      staging.versionLayers ||= {};
      if (technical.architectureRevision) staging.versionLayers.architectureRevision = technical.architectureRevision;
      if (technical.codeRevision) staging.versionLayers.codeRevision = technical.codeRevision;
      if (technical.dependencyVersions) staging.versionLayers.dependencyVersions = clone(technical.dependencyVersions);
      if (Object.prototype.hasOwnProperty.call(technical, 'deploymentVersion')) staging.versionLayers.deploymentVersion = clone(technical.deploymentVersion);
      if (Object.prototype.hasOwnProperty.call(technical, 'runtimeConfiguration')) staging.versionLayers.runtimeConfiguration = clone(technical.runtimeConfiguration);
      const technicalChanged = JSON.stringify(staging.versionLayers) !== previousTechnicalSignature;
      const resultingOperationCount = staging.draftWorkspace?.operations.length || 0, resultingUnknownCount = (staging.unknowns?.length || 0) + (staging.conflictRecords?.length || 0);
      if (resultingOperationCount === startingOperationCount && resultingUnknownCount === startingUnknownCount && !technicalChanged) {
        proposal.status = 'committed'; proposal.committedAt = C.now(); proposal.updatedAt = C.now(); proposal.idempotent = true; proposal.commitSummary = preview.summary;
        Access.touch(staging, 'import.changed', { proposalId, packageId: pack.manifest.packageId, idempotent: true }); replaceProject(project, staging);
        return { ok: true, idempotent: true, proposal: project.importProposals.find(item => item.proposalId === proposalId), summary: preview.summary };
      }
      staging.knowledgePackages.unshift({ packageId: pack.manifest.packageId, contentHash: proposal.contentHash, manifest: clone(pack.manifest), provenance: clone(pack.importProvenance), importedAt: C.now(), proposalId, status: 'draft' });
      if (!staging.draftWorkspace) {
        proposal.status = 'committed'; proposal.committedAt = C.now(); proposal.updatedAt = C.now(); proposal.technicalRevisionOnly = true; proposal.commitSummary = preview.summary;
        Access.touch(staging, 'import.changed', { proposalId, packageId: pack.manifest.packageId, technicalRevisionOnly: true }); replaceProject(project, staging);
        return { ok: true, technicalRevisionOnly: true, proposal: project.importProposals.find(item => item.proposalId === proposalId), summary: preview.summary };
      }
      proposal.status = 'committed'; proposal.committedAt = C.now(); proposal.updatedAt = C.now(); proposal.commitSummary = preview.summary; proposal.draftWorkspaceId = staging.draftWorkspace.id;
      staging.draftWorkspace.title ||= `导入 ${pack.manifest.projectName}`; staging.draftWorkspace.reason ||= `人工确认 AKP ${pack.manifest.packageId}，仅写入待核验草稿`;
      staging.draftWorkspace.operations.push({ id: `OP-${proposalId}-COMMIT`, command: 'ImportKnowledgePackage', input: { packageId: pack.manifest.packageId, contentHash: proposal.contentHash }, at: C.now(), by: options.by || '知识负责人', summary: `原子导入 ${pack.manifest.packageId}；事实等级保留，未直接批准` });
      Access.touch(staging, 'import.changed', { proposalId, packageId: pack.manifest.packageId });
      replaceProject(project, staging);
      return { ok: true, proposal: project.importProposals.find(item => item.proposalId === proposalId), draftWorkspace: project.draftWorkspace, summary: preview.summary };
    } catch (error) { return { ok: false, errors: [error.message], atomicRollback: true, proposal: original }; }
  }
  function discardImport(project, proposalId, options = {}) {
    W.assert(options.context, 'knowledge:draft');
    const proposal = (project.importProposals || []).find(item => item.proposalId === proposalId);
    if (!proposal || proposal.status !== 'committed') return { ok: false, errors: ['只有已写入且未发布的导入可以撤销'] };
    if (project.draftWorkspace?.id !== proposal.draftWorkspaceId) return { ok: false, errors: ['导入所在草稿已发布或已切换，不能覆盖后续工作'] };
    const staging = clone(project), ws = staging.draftWorkspace, pack = staging.importProposals.find(item => item.proposalId === proposalId).package, map = mappings(staging, pack);
    pack.entities.forEach(entity => { const id = map[entity.stableExternalId]; if (ws.entities[id]?.importPackageId === pack.manifest.packageId) delete ws.entities[id]; });
    Object.values(ws.assertions).forEach(assertion => { if (assertion.importPackageId === pack.manifest.packageId) delete ws.assertions[assertion.assertionId]; });
    ws.operations.push({ id: `OP-${proposalId}-DISCARD`, command: 'DiscardKnowledgePackageImport', at: C.now(), summary: `撤销未发布导入 ${pack.manifest.packageId}` });
    const target = staging.importProposals.find(item => item.proposalId === proposalId); target.status = 'discarded'; target.updatedAt = C.now();
    Access.touch(staging, 'import.changed', { proposalId, discarded: true }); replaceProject(project, staging); return { ok: true, proposal: project.importProposals.find(item => item.proposalId === proposalId) };
  }
  return { FACT_LEVELS, MAX_BYTES, parse, validatePackage, previewImport, stagePackage, commitImport, discardImport, hash, safeId };
});
