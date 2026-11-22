/* Business Atlas V5 — semantic assertions, immutable governance, context contracts and activity capabilities. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const core = commonjs ? require('../v4-core.js') : root.AtlasCore;
  const api = factory(core);
  if (commonjs) module.exports = api;
  root.AtlasV5 = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (K) {
  'use strict';
  const clone = K.clone;
  const now = () => new Date().toISOString();
  const FACT_KINDS = ['approved_specification', 'observed_implementation', 'historical_intent', 'observed', 'documented_intent', 'inferred', 'unknown', 'conflicting'];
  const VERIFICATION = ['confirmed', 'unverified', 'conflicted', 'stale'];
  const PREDICATES = {
    belongsTo: { label: '属于', from: '*', to: ['Project', 'Domain', 'Capability', 'Scenario', 'System', 'Subsystem'] },
    contains: { label: '包含', from: ['Project', 'Domain', 'Capability', 'Feature', 'Scenario', 'Stage', 'System', 'Subsystem', 'Module'], to: '*' },
    implements: { label: '实现', from: ['Feature', 'Module', 'API'], to: ['Capability', 'Feature', 'Action', 'Rule'] },
    triggers: { label: '触发', from: ['Surface', 'API', 'Event', 'Action'], to: ['Action', 'Feature'] },
    constrains: { label: '约束', from: ['Rule'], to: ['Action', 'Feature', 'API'] },
    blocks: { label: '阻止', from: ['Rule'], to: ['Action'] },
    reads: { label: '读取', from: ['Action', 'API', 'Module'], to: ['DataEntity', 'Configuration'] },
    writes: { label: '写入', from: ['Action', 'API', 'Module'], to: ['DataEntity', 'Configuration'] },
    dependsOn: { label: '依赖', from: '*', to: '*' },
    affects: { label: '影响', from: ['Configuration', 'KnownIssue'], to: ['Rule', 'Feature', 'Action', 'Configuration'] },
    produces: { label: '产生', from: ['Action', 'API'], to: ['Event', 'DataEntity'] },
    transitions: { label: '转移', from: ['Action'], to: ['BusinessObject'] },
    appliesTo: { label: '适用', from: ['Rule', 'Configuration', 'Assertion'], to: '*' },
    supports: { label: '证实', from: ['Evidence', 'Resource'], to: '*' },
    cites: { label: '引用依据', from: '*', to: ['Resource', 'Evidence'] },
    conflictsWith: { label: '与规范冲突', from: '*', to: '*' },
    changes: { label: '改变', from: ['Action'], to: ['BusinessObject'] },
    references: { label: '引用', from: '*', to: '*' },
    explains: { label: '解释', from: ['Term', 'Rule', 'Resource'], to: '*' },
    derivedFrom: { label: '提炼自', from: '*', to: ['Resource', 'Evidence'] },
    uses: { label: '使用', from: '*', to: '*' },
    requires: { label: '要求', from: '*', to: '*' },
    subjectOf: { label: '是场景主体', from: ['BusinessObject', 'ManufacturingBatch', 'AdministrativeMatter'], to: ['Scenario'] }
  };
  const CAPABILITIES = {
    fill: { label: '填写信息', input: '内容' }, linkKnowledge: { label: '添加关联知识', input: '知识对象 ID' },
    submitEvidence: { label: '提交检测证据', input: '证据定位与说明', artifact: '检测证据' },
    askQuestion: { label: '提出澄清问题', input: '问题' }, review: { label: '记录审核意见', input: '审核意见' },
    approve: { label: '批准', input: '批准意见' }, reject: { label: '驳回', input: '驳回原因' },
    assignTask: { label: '指派复检任务', input: '责任人与要求' }, requestVerification: { label: '请求验证', input: '验证要求' },
    generateDelivery: { label: '生成任务交接包', input: null }, delegateAgent: { label: '委托 Agent', input: '执行说明' },
    linkImplementation: { label: '关联实现结果', input: '文件或提交' }, requestTest: { label: '请求测试', input: '测试范围' },
    submitAcceptance: { label: '提交验收', input: '验收结果' }, recordBusinessEvent: { label: '记录业务事件', input: '事件内容' },
    recordInvestigation: { label: '记录调查结论', input: '调查结论', artifact: '调查结论' },
    proposeDisposition: { label: '提出处置建议', input: '处置建议' }, countersign: { label: '提交会签意见', input: '部门意见' },
    requestCorrection: { label: '退回补正', input: '补正材料与期限' }, decideDepartment: { label: '作出部门决定', input: '决定与依据' },
    viewRegulations: { label: '查看法规与材料', input: null }, complete: { label: '完成当前活动', input: null }
  };

  function deepFreeze(value) {
    if (!value || typeof value !== 'object' || Object.isFrozen(value)) return value;
    Object.freeze(value); Object.values(value).forEach(deepFreeze); return value;
  }
  function typeOf(project, id) { return getEntity(project, id)?.typeId || null; }
  function typeMatches(actual, expected) { return expected === '*' || (Array.isArray(expected) ? expected : [expected]).includes(actual); }
  function validateAssertion(project, input) {
    const errors = [], subject = getEntity(project, input.subject), object = input.object ? getEntity(project, input.object) : null;
    if (!input.assertionId && !input.id) errors.push('断言需要稳定 ID');
    if (!subject) errors.push(`断言主语 ${input.subject || '空'} 不存在`);
    if (input.object && !object) errors.push(`断言宾语 ${input.object} 不存在`);
    if (!input.object && input.value === undefined) errors.push('断言需要 object 或 value');
    const predicate = PREDICATES[input.predicate];
    if (!predicate) errors.push(`未注册语义关系 ${input.predicate || '空'}`);
    if (predicate && subject && !typeMatches(subject.typeId, predicate.from)) errors.push(`${subject.typeId} 不能作为 ${input.predicate} 的主语`);
    if (predicate && object && !typeMatches(object.typeId, predicate.to)) errors.push(`${object.typeId} 不能作为 ${input.predicate} 的宾语`);
    const subjectSchema = subject && K.schemaFor(project, subject.typeId), objectSchema = object && K.schemaFor(project, object.typeId);
    if (subjectSchema?.custom && !subjectSchema.relations?.includes(input.predicate)) errors.push(`自定义类型 ${subject.typeId} 未声明允许关系 ${input.predicate}`);
    if (objectSchema?.custom && !objectSchema.relations?.includes(input.predicate)) errors.push(`自定义类型 ${object.typeId} 未声明允许关系 ${input.predicate}`);
    if (input.predicate === 'affects' && subject?.typeId === 'Configuration') {
      const q = input.qualifiers || {};
      if (!(q.environment || q.environments?.length) || !(q.organization || q.region || q.tenant || q.businessScope) || !(q.configVersion || q.version)) errors.push('配置影响必须声明环境、业务范围和配置版本');
    }
    if (input.factKind && !FACT_KINDS.includes(input.factKind)) errors.push('事实类别无效');
    if (input.verificationStatus && !VERIFICATION.includes(input.verificationStatus)) errors.push('核验状态无效');
    return errors;
  }
  function createAssertion(project, input, options = {}) {
    const sourceRefs = clone(input.sourceRefs || (input.sourceId ? [{ resourceId: input.sourceId, locator: input.locator || null }] : []));
    const requested = input.verificationStatus || (input.verified === false || input.trust === 'inferred' ? 'unverified' : 'confirmed');
    const verificationStatus = !sourceRefs.length && !options.legacyImport ? 'unverified' : requested;
    const item = {
      assertionId: input.assertionId || input.id || `AST-${project.meta.id}-${String((project.assertions?.length || 0) + 1).padStart(5, '0')}`,
      projectId: project.meta.id, subject: input.subject || input.from, predicate: input.predicate || input.type,
      ...(input.object || input.to ? { object: input.object || input.to } : { value: clone(input.value) }),
      qualifiers: clone(input.qualifiers || { projectId: project.meta.id, environment: input.environment, businessScope: input.scope, condition: input.condition, version: input.version, configVersion: input.configVersion }),
      sourceRefs, factKind: input.factKind || 'approved_specification', verificationStatus,
      validTime: clone(input.validTime || { from: '2026-11-14', to: null }), revision: input.revision || 'a1',
      owner: input.owner || '未指定确认人', explanation: input.explanation || '', conflictIds: clone(input.conflictIds || []), createdAt: input.createdAt || now()
    };
    Object.keys(item.qualifiers).forEach(key => item.qualifiers[key] === undefined && delete item.qualifiers[key]);
    const errors = validateAssertion(project, item); if (errors.length) throw new Error(errors.join('；'));
    project.assertions ||= [];
    const duplicate = project.assertions.find(a => a.subject === item.subject && a.predicate === item.predicate && a.object === item.object && JSON.stringify(a.qualifiers) === JSON.stringify(item.qualifiers));
    if (duplicate) return duplicate;
    if (project.assertions.some(a => a.assertionId === item.assertionId)) throw new Error(`断言 ID ${item.assertionId} 已存在`);
    project.assertions.push(item); return item;
  }
  function importLegacyAssertions(project) {
    project.assertions ||= [];
    (project.relations || []).forEach(relation => {
      if (!PREDICATES[relation.type]) return;
      try {
        createAssertion(project, {
          assertionId: `AST-${relation.id}`, subject: relation.from, predicate: relation.type, object: relation.to,
          qualifiers: { projectId: project.meta.id, version: relation.version, condition: relation.condition, businessScope: relation.scope },
          sourceRefs: relation.sourceId ? [{ resourceId: relation.sourceId, locator: relation.condition ? '关系条件' : '原始映射' }] : [],
          verificationStatus: relation.verified === false || relation.trust === 'inferred' ? 'unverified' : 'confirmed',
          factKind: 'approved_specification', owner: 'V4 已批准关系迁移', explanation: relation.explanation || ''
        }, { legacyImport: true });
      } catch (_) { /* Invalid legacy combinations remain readable as relations, never promoted to assertions. */ }
    });
    return project.assertions;
  }
  function currentRevision(item) { return item?.revision || item?.currentVersion || item?.version || 'r1'; }
  function baselineById(project, id) { return (project.baselines || []).find(b => b.id === id) || null; }
  function assertionAsEntity(assertion) {
    return assertion ? { ...assertion, id: assertion.assertionId, typeId: 'Assertion', name: `${assertion.subject} ${assertion.predicate} ${assertion.object || ''}`, description: assertion.explanation || '', status: assertion.verificationStatus === 'confirmed' ? 'published' : 'draft' } : null;
  }
  function getEntity(project, id, options = {}) {
    if (!id) return null;
    const bare = String(id).split('@')[0];
    let revision = typeof options === 'string' ? options : options.revision;
    if (typeof options === 'object' && options.baselineId) {
      const baseline = baselineById(project, options.baselineId); revision = baseline?.entries?.[bare] || revision;
      if (baseline && !baseline.entries?.[bare]) return null;
    }
    if (revision && project.knowledgeRevisions?.[`${bare}@${revision}`]) return project.knowledgeRevisions[`${bare}@${revision}`];
    if (!revision && project.publishedEntities?.[bare]) return project.publishedEntities[bare];
    const legacy = K.getEntity(project, bare, revision || undefined); if (legacy) return legacy;
    const assertion = (project.assertions || []).find(a => a.assertionId === bare && (!revision || a.revision === revision));
    return assertionAsEntity(assertion);
  }
  function permissionAllows(item, permission = { role: 'reader', clearance: 'restricted' }) {
    if (!item) return false;
    const levels = { public: 0, internal: 1, sensitive: 2, confidential: 2, restricted: 3 };
    const roleAllowed = !item.permissions?.read || item.permissions.read.includes('*') || item.permissions.read.includes(permission.role) || permission.role === 'admin';
    return roleAllowed && (levels[item.sensitivity || 'internal'] ?? 1) <= (levels[permission.clearance] ?? 1);
  }
  function allEntities(project, options = {}) {
    const map = new Map();
    K.allEntities(project).forEach(item => { const current = getEntity(project, item.id) || item; map.set(item.id, current); });
    Object.values(project.publishedEntities || {}).forEach(item => map.set(item.id, item));
    return [...map.values()].filter(item => !options.permission || permissionAllows(item, options.permission));
  }
  function initializeProject(project) {
    project.assertions ||= []; importLegacyAssertions(project);
    project.publishedEntities ||= {}; project.knowledgeRevisions ||= {}; project.knowledgeChanges ||= []; project.impactReviews ||= []; project.baselines ||= [];
    Object.values(project.publishedEntities).forEach(deepFreeze); Object.values(project.knowledgeRevisions).forEach(deepFreeze);
    allEntities(project).forEach(item => {
      if (!item?.id) return;
      const revision = currentRevision(item), snapshot = deepFreeze(clone({ ...item, revision, status: item.status || 'published' }));
      project.publishedEntities[item.id] ||= snapshot;
      project.knowledgeRevisions[`${item.id}@${revision}`] ||= snapshot;
    });
    if (!project.baselines.length) {
      const entries = {}, assertionRevisions = {};
      Object.values(project.publishedEntities).forEach(item => { entries[item.id] = currentRevision(item); });
      project.assertions.forEach(a => { assertionRevisions[a.assertionId] = a.revision; });
      const id = project.versionLayers?.baseline || `BL-${project.meta.id}-2026-11-14`;
      project.baselines.push({ id, projectId: project.meta.id, createdAt: '2026-11-14T09:00:00.000Z', approvalStatus: 'approved', entries, assertionRevisions, source: 'V4 已发布知识与 V5 显式语义迁移', confirmations: [{ by: '项目知识负责人', decision: 'approved', at: '2026-11-14T09:00:00.000Z' }] });
      project.versionLayers ||= {}; project.versionLayers.baseline = id;
    }
    project.unknowns ||= []; project.conflictRecords ||= []; project.contextPackages ||= []; project.contractFeedback ||= [];
    return project;
  }
  function assertionsFor(project, id, options = {}) {
    const baseline = options.baselineId ? baselineById(project, options.baselineId) : null;
    return (project.assertions || []).filter(a => {
      if (a.subject !== id && a.object !== id) return false;
      if (options.permission && (!permissionAllows(getEntity(project, a.subject), options.permission) || (a.object && !permissionAllows(getEntity(project, a.object), options.permission)))) return false;
      if (baseline && (!baseline.assertionRevisions[a.assertionId] || baseline.assertionRevisions[a.assertionId] !== a.revision)) return false;
      if (options.approvedOnly && (a.verificationStatus !== 'confirmed' || a.factKind !== 'approved_specification')) return false;
      if (options.predicate && a.predicate !== options.predicate) return false;
      return true;
    });
  }
  function nextRevision(revision = 'r1') {
    if (/^[ra]\d+$/.test(revision)) return `${revision[0]}${Number(revision.slice(1)) + 1}`;
    const match = String(revision).match(/^(v?)(\d+)(?:\.(\d+))?$/); return match ? `${match[1]}${match[2]}.${Number(match[3] || 0) + 1}` : `${revision}.1`;
  }
  function flatten(value, prefix = '', out = {}) {
    if (value && typeof value === 'object' && !Array.isArray(value)) Object.entries(value).forEach(([key, child]) => flatten(child, prefix ? `${prefix}.${key}` : key, out));
    else out[prefix] = value;
    return out;
  }
  function diffEntities(before, after) {
    const a = flatten(before || {}), b = flatten(after || {}), keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    return [...keys].filter(key => !['revision', 'status', 'createdAt', 'updatedAt', 'publishedAt'].includes(key) && JSON.stringify(a[key]) !== JSON.stringify(b[key])).map(key => ({ path: key, before: a[key], after: b[key] }));
  }
  function createChange(project, input) {
    initializeProject(project);
    const entity = getEntity(project, input.entityId); if (!entity) throw new Error('需要选择已存在的知识对象');
    const existing = project.knowledgeChanges.find(c => c.entityId === input.entityId && ['draft', 'pending'].includes(c.status)); if (existing) return existing;
    const baseRevision = entity.typeId === 'Project' && String(entity.revision).startsWith('BL-') ? 'r1' : currentRevision(entity), id = input.id || `KC-${project.meta.id}-${String(project.knowledgeChanges.length + 1).padStart(4, '0')}`;
    if (entity.typeId === 'Assertion') project.knowledgeRevisions[`${entity.id}@${baseRevision}`] ||= deepFreeze(clone(entity));
    const draft = clone(entity); draft.revision = nextRevision(baseRevision); draft.status = 'draft'; delete draft.revisions; delete draft.selectedVersion;
    const change = { id, projectId: project.meta.id, entityId: entity.id, ...(entity.typeId === 'Assertion' ? { assertionId: entity.id } : {}), entityType: entity.typeId, title: input.title || `修改${entity.name}`, reason: input.reason || '', baseRevision, targetRevision: draft.revision, baseSnapshot: deepFreeze(clone(entity)), draft, status: 'draft', revision: 'c1', impactAcknowledged: false, impacts: [], reviewDecisions: [], history: [{ at: now(), by: input.owner || '当前用户', event: 'draft-created' }], createdAt: now(), updatedAt: now() };
    project.knowledgeChanges.unshift(change); return change;
  }
  function updateDraft(project, changeId, patch, expectedRevision) {
    const change = project.knowledgeChanges.find(c => c.id === changeId); if (!change || change.status !== 'draft') throw new Error('只有草稿变更可以编辑');
    if (expectedRevision && change.revision !== expectedRevision) throw new Error('变更草稿已被更新，请重新载入');
    const next = { ...change.draft, ...clone(patch), id: change.entityId, typeId: change.entityType, revision: change.targetRevision, status: 'draft' };
    if (patch.props) next.props = { ...change.draft.props, ...clone(patch.props) };
    if (!next.name) throw new Error('名称不能为空');
    if (project.knowledge.some(item => item.id === change.entityId)) {
      const errors = K.validateEntity(project, next); if (errors.length) throw new Error(errors.join('；'));
    }
    change.draft = next; change.revision = `c${Number(change.revision.slice(1)) + 1}`; change.updatedAt = now(); change.history.push({ at: now(), by: '当前用户', event: 'draft-updated' }); return change;
  }
  function impactFor(project, change) {
    const ids = new Set();
    (project.assertions || []).forEach(a => {
      if (a.subject === change.entityId && a.object) ids.add(a.object);
      if (a.object === change.entityId) ids.add(a.subject);
    });
    return [...ids].filter(id => id !== change.entityId).map(id => {
      const entity = getEntity(project, id), existing = project.impactReviews.find(r => r.changeId === change.id && r.entityId === id);
      return existing || { id: `IMP-${change.id}-${id}`, changeId: change.id, entityId: id, entityName: entity?.name || id, revision: currentRevision(entity), status: 'requires-review', reason: '关联语义可能受变更影响；不自动升级关联对象修订。' };
    });
  }
  function submitChange(project, changeId, input = {}) {
    const change = project.knowledgeChanges.find(c => c.id === changeId); if (!change || change.status !== 'draft') return { ok: false, errors: ['变更不是可提交草稿'] };
    const errors = []; if (!(input.reason || change.reason).trim()) errors.push('请填写变更原因');
    if (!diffEntities(change.baseSnapshot, change.draft).length) errors.push('草稿与已发布修订没有差异');
    change.impacts = impactFor(project, change);
    if (!change.impactAcknowledged && change.impacts.length) errors.push('请完成影响复核确认');
    if (errors.length) return { ok: false, errors };
    change.reason = input.reason || change.reason; change.status = 'pending'; change.draft.status = 'pending'; change.impacts.forEach(i => { if (!project.impactReviews.some(r => r.id === i.id)) project.impactReviews.push(i); });
    change.history.push({ at: now(), by: input.by || '当前用户', event: 'submitted', impacts: change.impacts.length }); change.updatedAt = now(); return { ok: true, change };
  }
  function createBaseline(project, input = {}) {
    const entries = {}, assertionRevisions = {};
    Object.values(project.publishedEntities).forEach(item => { entries[item.id] = currentRevision(item); });
    project.assertions.forEach(a => { assertionRevisions[a.assertionId] = a.revision; });
    const baseline = { id: input.id || `BL-${project.meta.id}-${String(project.baselines.length + 1).padStart(3, '0')}`, projectId: project.meta.id, createdAt: now(), approvalStatus: 'approved', entries, assertionRevisions, source: input.source || '知识变更批准后的完整对象与断言修订快照', confirmations: [{ by: input.by || '项目批准人', decision: 'approved', at: now() }] };
    project.baselines.push(baseline); project.versionLayers.baseline = baseline.id; return baseline;
  }
  function approveChange(project, changeId, input = {}) {
    const change = project.knowledgeChanges.find(c => c.id === changeId); if (!change || change.status !== 'pending') return { ok: false, errors: ['变更尚未提交或已处理'] };
    if (change.assertionId) {
      const nextAssertion = clone(change.draft);
      ['id', 'typeId', 'name', 'description', 'status', 'publishedAt', 'governance', 'revisions'].forEach(key => delete nextAssertion[key]);
      nextAssertion.assertionId = change.assertionId; nextAssertion.revision = change.targetRevision;
      if (nextAssertion.verificationStatus === 'confirmed' && !nextAssertion.sourceRefs?.length) return { ok: false, errors: ['没有来源的断言不能发布为已确认事实'] };
      const assertionErrors = validateAssertion(project, nextAssertion); if (assertionErrors.length) return { ok: false, errors: assertionErrors };
      const index = project.assertions.findIndex(a => a.assertionId === change.assertionId); if (index < 0) return { ok: false, errors: ['原断言不存在'] };
      const publishedAssertion = deepFreeze(assertionAsEntity(nextAssertion)); project.assertions[index] = deepFreeze(nextAssertion); project.knowledgeRevisions[`${change.assertionId}@${change.targetRevision}`] = publishedAssertion;
      change.status = 'published'; change.reviewDecisions.push({ id: `RD-${change.id}-01`, by: input.by || '项目批准人', decision: 'approved', at: now(), comment: input.comment || '断言来源、范围和语义已复核' });
      change.history.push({ at: now(), by: input.by || '项目批准人', event: 'published', revision: change.targetRevision });
      const assertionBaseline = createBaseline(project, { by: input.by, source: `批准 ${change.id} 后形成的新知识基线` });
      return { ok: true, change, published: publishedAssertion, baseline: assertionBaseline };
    }
    const published = deepFreeze(clone({ ...change.draft, status: 'published', publishedAt: now(), governance: { ...(change.draft.governance || {}), state: 'confirmed', owner: input.by || '项目批准人' } }));
    project.knowledgeRevisions[`${change.entityId}@${change.baseRevision}`] ||= deepFreeze(clone(change.baseSnapshot));
    project.knowledgeRevisions[`${change.entityId}@${change.targetRevision}`] = published;
    project.publishedEntities[change.entityId] = published;
    change.status = 'published'; change.reviewDecisions.push({ id: `RD-${change.id}-01`, by: input.by || '项目批准人', decision: 'approved', at: now(), comment: input.comment || '业务与技术影响已复核' });
    change.history.push({ at: now(), by: input.by || '项目批准人', event: 'published', revision: change.targetRevision });
    change.impacts.forEach(impact => { const stored = project.impactReviews.find(r => r.id === impact.id); if (stored) stored.status = 'requires-review'; });
    const baseline = createBaseline(project, { by: input.by, source: `批准 ${change.id} 后形成的新知识基线` });
    return { ok: true, change, published, baseline };
  }
  function resolvePanorama(project, entityId) {
    const profiles = project.panoramas || [];
    return profiles.find(p => p.id === entityId || p.rootId === entityId || p.entityIds?.includes(entityId) || p.steps?.some(s => s.entityId === entityId)) || profiles.find(p => p.default) || null;
  }
  function panorama(project, entityId) {
    const profile = resolvePanorama(project, entityId); if (!profile) return null;
    return { ...clone(profile), steps: profile.steps.map(step => ({ ...step, entity: getEntity(project, step.entityId), assertions: assertionsFor(project, step.entityId) })) };
  }
  function validateSemantics(project) {
    const errors = [], warnings = [];
    (project.assertions || []).forEach(a => validateAssertion(project, a).forEach(e => errors.push(`${a.assertionId}: ${e}`)));
    allEntities(project).filter(item => item.typeId === 'BusinessObject').forEach(object => {
      const states = new Set(object.states || []), transitions = object.transitions || [];
      if (states.length && !transitions.length) warnings.push(`${object.id}: 只有状态目录，尚未建立生命周期`);
      transitions.forEach(t => {
        if (!states.has(t.fromState) || !states.has(t.toState)) errors.push(`${object.id}: 转移 ${t.id} 引用未定义状态`);
        if (!t.triggerEvent || !t.guard || !getEntity(project, t.actionRef)) errors.push(`${object.id}: 转移 ${t.id} 缺少触发、守卫或有效动作`);
        if (t.verificationStatus === 'confirmed' && !t.sourceRefs?.length) errors.push(`${object.id}: 已确认转移 ${t.id} 缺少来源`);
      });
    });
    (project.flows || []).forEach(flow => {
      const nodes = new Map(flow.nodes.map(n => [n.id, n])), outgoing = new Map();
      (flow.edges || []).forEach(edge => {
        if (!nodes.has(edge.from) || !nodes.has(edge.to)) errors.push(`${flow.id}: 边 ${edge.id} 引用无效节点`);
        (outgoing.get(edge.from) || outgoing.set(edge.from, []).get(edge.from)).push(edge);
        if (['return', 'error'].includes(edge.type) && !nodes.has(edge.to)) errors.push(`${flow.id}: 异常或回流没有有效恢复节点`);
      });
      flow.nodes.filter(n => n.type === 'decision').forEach(node => {
        const branches = outgoing.get(node.id) || [];
        if (!branches.length) errors.push(`${flow.id}: 判断 ${node.id} 未定义结果`);
        branches.filter(e => e.type === 'when').forEach(edge => { if (!edge.when?.outcomeId) errors.push(`${flow.id}: 条件边 ${edge.id} 未定义决策结果`); });
      });
      const hasReturn = flow.edges.some(e => e.type === 'return'); if (hasReturn) warnings.push(`${flow.id}: 存在显式恢复回边；仅允许定义中的受控循环`);
      flow.nodes.filter(n => n.type === 'parallel').forEach(n => { const explicitJoin = n.joinId && nodes.has(n.joinId); const edgeJoin = flow.edges.some(e => e.type === 'join' && nodes.has(e.to) && nodes.get(e.to).type === 'join'); if (!explicitJoin && !edgeJoin) errors.push(`${flow.id}: 并行节点 ${n.id} 未定义有效汇合与退出`); });
    });
    return { ok: !errors.length, errors, warnings };
  }
  function collectContext(project, rootId, options = {}) {
    const depth = Math.max(0, Math.min(4, options.depth ?? 2)), permission = options.permission || { role: 'reader', clearance: 'restricted' };
    const included = new Map(), excluded = [], edges = [], unknowns = new Set(); let frontier = [rootId];
    const root = getEntity(project, rootId, { baselineId: options.baselineId });
    if (!root) return { included, excluded, edges, unknowns, root: null };
    if (!permissionAllows(root, permission)) excluded.push({ id: rootId, reason: '权限不足' }); else included.set(rootId, root);
    for (let level = 0; level < depth; level++) {
      const next = [];
      frontier.forEach(id => assertionsFor(project, id, { baselineId: options.baselineId }).forEach(a => {
        const other = a.subject === id ? a.object : a.subject; if (!other) return;
        const target = getEntity(project, other, { baselineId: options.baselineId });
        if (!target || !permissionAllows(target, permission)) { excluded.push({ id: other, assertionId: a.assertionId, reason: '权限或基线范围排除' }); return; }
        if (options.approvedOnly && (a.verificationStatus !== 'confirmed' || a.factKind !== 'approved_specification')) { unknowns.add(a.assertionId); return; }
        edges.push(a); if (!included.has(other)) { included.set(other, target); next.push(other); }
      }));
      frontier = next;
    }
    return { included, excluded, edges: [...new Map(edges.map(a => [a.assertionId, a])).values()], unknowns, root };
  }
  function generateContextPackage(project, input) {
    initializeProject(project);
    const baseline = baselineById(project, input.baselineId || project.versionLayers.baseline), root = getEntity(project, input.requirementId || input.rootId, { baselineId: baseline?.id });
    if (!root) return { ok: false, code: 'NOT_FOUND', errors: ['需求或知识对象不在指定基线中'] };
    const formal = input.mode !== 'exploration', graph = collectContext(project, root.id, { baselineId: baseline?.id, depth: input.depth ?? 3, permission: input.permission, approvedOnly: formal });
    const nodes = [...graph.included.values()], relatedIds = new Set([root.id, ...nodes.map(n => n.id), ...graph.edges.flatMap(a => [a.subject, a.object])]);
    const applicableUnknowns = (project.unknowns || []).filter(u => u.status !== 'resolved' && (!u.relatedIds?.length || u.relatedIds.some(id => relatedIds.has(id))));
    const critical = applicableUnknowns.filter(u => u.blocksFormal), conflicts = (project.conflictRecords || []).filter(c => c.status !== 'resolved' && c.assertionIds?.some(id => graph.edges.some(a => a.assertionId === id)));
    const errors = []; if (formal && baseline?.approvalStatus !== 'approved') errors.push('正式契约必须绑定已批准基线');
    if (formal && !['published', 'approved'].includes(root.status)) errors.push('根需求尚未批准');
    if (formal && critical.length) errors.push(`存在 ${critical.length} 个阻止安全实施的关键未知`);
    if (errors.length) return { ok: false, code: critical.length ? 'CRITICAL_UNKNOWNS' : 'UNAPPROVED_CONTEXT', errors, criticalUnknowns: clone(critical), explorationAvailable: true };
    const byType = type => nodes.filter(n => n.typeId === type).map(clone);
    const packageId = `KCP-${project.meta.id}-${String(project.contextPackages.length + 1).padStart(4, '0')}`;
    const result = {
      ok: true, packageId, projectId: project.meta.id, mode: formal ? 'formal' : 'exploration', purpose: input.purpose || 'development', generatedAt: now(),
      taskIntent: input.taskIntent || `围绕 ${root.name} 完成受控${({ development: '开发实现', testing: '测试验证', review: '业务审核', agent: 'Agent 执行', impact: '技术影响分析' })[input.purpose] || '任务'}，发现偏差必须反馈。`,
      businessContext: nodes.filter(n => ['Domain', 'Capability', 'Feature', 'Scenario', 'Actor', 'BusinessObject', 'ManufacturingBatch'].includes(n.typeId)).map(clone),
      approvedRequirements: [clone(root), ...byType('Requirement').filter(n => n.id !== root.id)], constraints: byType('Rule'),
      behavior: { actions: byType('Action'), transitions: nodes.filter(n => n.typeId === 'BusinessObject').flatMap(n => clone(n.transitions || [])), assertions: clone(graph.edges) },
      implementationContext: nodes.filter(n => ['Surface', 'System', 'Module', 'API', 'DataEntity', 'Configuration', 'Event'].includes(n.typeId)).map(clone),
      unknowns: clone(applicableUnknowns), unresolvedAssertionIds: [...graph.unknowns], acceptance: clone(root.acceptance || root.props?.acceptance || project.acceptance?.filter(a => a.requirementId === root.id) || []),
      evidence: [...new Set(graph.edges.flatMap(a => (a.sourceRefs || []).map(s => s.resourceId)))].map(id => getEntity(project, id, { baselineId: baseline?.id })).filter(Boolean).map(clone),
      versionBinding: { baselineId: baseline.id, baselineApproved: baseline.approvalStatus === 'approved', entries: Object.fromEntries(nodes.map(n => [n.id, currentRevision(n)])), assertions: Object.fromEntries(graph.edges.map(a => [a.assertionId, a.revision])) },
      feedbackContract: { required: ['完成工作与产物定位', '测试结果', '新发现冲突', '待澄清问题', '与契约的偏差'], destination: 'pending_verification', mayModifyApprovedBaseline: false },
      includedFacts: nodes.map(n => ({ id: n.id, revision: currentRevision(n), typeId: n.typeId })), includedAssertions: graph.edges.map(a => a.assertionId), excluded: graph.excluded,
      conflicts: clone(conflicts), limitations: formal ? [] : ['探索上下文包含未核验信息，不可作为正式实施依据。']
    };
    project.contextPackages.unshift(result); return result;
  }
  function recordFeedback(project, packageId, input) {
    const context = project.contextPackages.find(p => p.packageId === packageId); if (!context) throw new Error('交付契约不存在');
    const feedback = { id: `FB-${project.meta.id}-${String(project.contractFeedback.length + 1).padStart(4, '0')}`, packageId, baselineId: context.versionBinding.baselineId, completedWork: input.completedWork || '', artifacts: clone(input.artifacts || []), testResults: input.testResults || '', conflicts: input.conflicts || '', questions: input.questions || '', deviations: input.deviations || '', status: 'pending_verification', createdAt: now(), by: input.by || '模拟执行者' };
    project.contractFeedback.unshift(feedback);
    if (feedback.questions || feedback.conflicts || feedback.deviations) project.issues.unshift({ id: `Q-${feedback.id}`, title: feedback.questions || feedback.conflicts || feedback.deviations, objectId: context.approvedRequirements[0]?.id || context.businessContext[0]?.id || project.meta.id, status: 'waiting', decision: null, replies: [], sourceFeedbackId: feedback.id, why: '执行反馈只能进入待确认队列，不自动修改批准基线。' });
    return feedback;
  }
  function allowedCapabilities(stage) { return (stage?.capabilities || []).filter(id => CAPABILITIES[id]); }
  function executeActivity(project, instanceId, capabilityId, input = {}) {
    const instance = project.workflowInstances.find(i => i.id === instanceId), stage = instance && K.currentStage(instance);
    if (!instance || instance.status !== 'active' || !stage) return { ok: false, errors: ['当前没有可执行活动'] };
    if (!allowedCapabilities(stage).includes(capabilityId)) return { ok: false, errors: [`活动 ${stage.name} 未声明操作 ${capabilityId}`] };
    const cap = CAPABILITIES[capabilityId]; if (cap.input && !String(input.value || '').trim() && !['generateDelivery'].includes(capabilityId)) return { ok: false, errors: [`请填写${cap.input}`] };
    if (capabilityId === 'complete') return K.advanceWorkflow(project, instanceId, { by: input.by || '当前用户', context: instance.context || {} });
    if (capabilityId === 'generateDelivery') {
      const delivery = { id: `DLV-${instance.id}-${instance.records.length + 1}`, requirementId: instance.subjectId, packageVersion: `${instance.definitionId}@${instance.definitionVersion}`, snapshot: deepFreeze(clone({ workflow: instance, at: now() })) };
      project.deliveries.push(delivery); instance.artifacts.handoff = { id: delivery.id, at: now() };
    }
    if (cap.artifact) instance.artifacts[cap.artifact] = { id: `ART-${instance.id}-${Object.keys(instance.artifacts).length + 1}`, value: input.value, stageId: stage.id, at: now(), status: 'recorded' };
    instance.records.push({ id: `REC-${instance.id}-${instance.records.length + 1}`, at: now(), by: input.by || '当前用户', type: 'activity', capabilityId, stageId: stage.id, note: input.value || cap.label });
    instance.updatedAt = now(); return { ok: true, instance, capability: cap };
  }
  return {
    clone, now, deepFreeze, FACT_KINDS, VERIFICATION, PREDICATES, CAPABILITIES,
    validateAssertion, createAssertion, importLegacyAssertions, assertionsFor, getEntity, allEntities, permissionAllows, initializeProject, baselineById, currentRevision,
    nextRevision, diffEntities, createChange, updateDraft, impactFor, submitChange, approveChange, createBaseline,
    resolvePanorama, panorama, validateSemantics, collectContext, generateContextPackage, recordFeedback, allowedCapabilities, executeActivity
  };
});
