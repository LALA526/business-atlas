/* Business Atlas V4 — Knowledge Core, Schema Registry, Semantic Services and Workflow Engine. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  root.AtlasCore = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const clone = value => value == null ? value : JSON.parse(JSON.stringify(value));
  const now = () => new Date().toISOString();
  const PROPERTY_TYPES = ['text', 'longtext', 'number', 'boolean', 'date', 'enum', 'ref', 'refs', 'json', 'resource', 'money', 'duration', 'unit'];
  const TYPE_LABELS = {
    Project: '项目', Domain: '业务领域', Capability: '业务能力', Feature: '产品功能', Scenario: '业务场景', Action: '业务动作', Actor: '参与者',
    Rule: '业务规则', BusinessObject: '业务对象', State: '状态', Event: '事件', Surface: '界面', System: '系统', Subsystem: '子系统', Module: '模块',
    API: '接口', Message: '消息', DataEntity: '数据实体', DataField: '数据字段', Integration: '外部集成', Configuration: '配置', Environment: '环境',
    Deployment: '部署', Term: '术语与概念', Resource: '原始资料', Convention: '项目约定', KnownIssue: '已知问题', HistoricalDecision: '历史决策',
    Requirement: '需求', Evidence: '证据', Workflow: '工作流', Change: '变更', Task: '任务', Delivery: '交付产物', Stage: '业务阶段', Step: '业务步骤', Exception: '异常与恢复'
  };
  const LEGACY_COLLECTIONS = {
    domains: 'Domain', capabilities: 'Capability', actors: 'Actor', objects: 'BusinessObject', rules: 'Rule', scenarios: 'Scenario',
    actions: 'Action', surfaces: 'Surface', facts: 'Evidence', changes: 'Change', issues: 'KnownIssue', deliveries: 'Delivery',
    stages: 'Stage', steps: 'Step', exceptions: 'Exception'
  };

  function field(id, label, dataType = 'text', extra = {}) { return { id, label, dataType, required: false, ...extra }; }
  function coreSchemas() {
    const schema = (id, name, dimension, fields = [], extra = {}) => ({
      id, name, dimension, version: 1, fields: [
        field('name', '名称', 'text', { required: true }), field('description', '定义', 'longtext'), ...fields
      ], relations: ['contains', 'belongsTo', 'uses', 'supports', 'implements', 'references', 'dependsOn', 'affects', 'reads', 'writes', 'cites', 'explains'], defaultView: 'summary', editable: true, permissions: { create: ['editor', 'admin'], edit: ['editor', 'admin'] }, ...extra
    });
    return {
      Project: schema('Project', '项目', '项目认知', [field('positioning', '项目定位', 'longtext'), field('audience', '服务对象', 'refs')]),
      Domain: schema('Domain', '业务领域', '业务认知'), Capability: schema('Capability', '业务能力', '业务认知'),
      Feature: schema('Feature', '产品功能', '业务认知', [field('capabilityId', '关联能力', 'ref'), field('entryPoints', '功能入口', 'refs')]),
      Scenario: schema('Scenario', '业务场景', '业务认知', [field('expressionMode', '表达模式', 'enum', { options: ['flow', 'case', 'state', 'decision', 'knowledge'] })], { defaultView: 'flow' }),
      Action: schema('Action', '业务动作', '业务认知'), Actor: schema('Actor', '参与者', '业务认知'),
      Rule: schema('Rule', '业务规则', '业务认知', [field('ruleKind', '规则形式', 'enum', { options: ['decision', 'constraint'] })], { defaultView: 'decision' }),
      BusinessObject: schema('BusinessObject', '业务对象', '业务认知', [field('states', '生命周期状态', 'json')], { defaultView: 'state' }),
      Surface: schema('Surface', '界面', '系统认知'), System: schema('System', '系统', '系统认知', [], { defaultView: 'system' }),
      Subsystem: schema('Subsystem', '子系统', '系统认知', [], { defaultView: 'system' }), Module: schema('Module', '模块', '系统认知', [field('responsibility', '职责', 'longtext')], { defaultView: 'system' }),
      API: schema('API', '接口', '系统认知', [field('method', '方法', 'text'), field('path', '路径', 'text'), field('providerId', '提供方', 'ref'), field('consumers', '调用方', 'refs'), field('input', '主要输入', 'json'), field('output', '主要输出', 'json'), field('failure', '失败与重试', 'longtext'), field('contractVersion', '契约版本', 'text')]),
      Message: schema('Message', '消息', '系统认知', [field('topic', '主题', 'text'), field('payload', '载荷', 'json')]),
      DataEntity: schema('DataEntity', '数据实体 / 表', '系统认知', [field('tableName', '物理表名', 'text'), field('fields', '主要字段', 'json'), field('lifecycle', '数据生命周期', 'longtext'), field('readOwners', '读取责任', 'refs'), field('writeOwners', '写入责任', 'refs'), field('implementationVersion', '适用技术版本', 'text')], { defaultView: 'data' }),
      DataField: schema('DataField', '数据字段', '系统认知', [field('dataType', '数据类型', 'text'), field('required', '必填', 'boolean')]),
      Integration: schema('Integration', '外部集成', '系统认知', [field('protocol', '协议', 'text'), field('owner', '维护责任', 'text')]),
      Configuration: schema('Configuration', '配置', '系统认知', [field('source', '文件或配置系统', 'resource'), field('moduleId', '所属模块', 'ref'), field('dataType', '值类型', 'enum', { options: ['text', 'number', 'boolean', 'json'] }), field('allowedValues', '可用取值', 'json'), field('defaultValue', '默认值', 'json'), field('effectiveWhen', '生效条件', 'longtext'), field('environments', '适用环境', 'refs'), field('regions', '地区或租户', 'json'), field('risk', '修改风险', 'longtext'), field('configVersion', '配置版本', 'text'), field('verified', '已核验', 'boolean')], { defaultView: 'config' }),
      Environment: schema('Environment', '部署环境', '系统认知', [field('region', '区域', 'text'), field('release', '运行版本', 'text'), field('configVersion', '配置版本', 'text')]),
      Deployment: schema('Deployment', '部署', '系统认知', [field('release', '软件版本', 'text'), field('environmentId', '环境', 'ref')]),
      Term: schema('Term', '术语与概念', '知识资产', [field('shortDefinition', '简短定义', 'text', { required: true }), field('context', '所属上下文', 'text'), field('synonyms', '同义名称', 'json'), field('historicalNames', '历史名称', 'json'), field('abbreviations', '缩写', 'json'), field('confusedWith', '易混淆概念', 'refs'), field('nameSources', '名称来源', 'json'), field('applicableVersion', '适用版本', 'text')]),
      Resource: schema('Resource', '原始知识资料', '知识资产', [field('resourceType', '资料类型', 'text'), field('uri', '文件或外部定位', 'resource'), field('locator', '精确定位', 'json'), field('sourceVersion', '原始版本', 'text'), field('summary', '摘要（不替代原文）', 'longtext'), field('parseStatus', '解析状态', 'enum', { options: ['unparsed', 'partial', 'structured'] }), field('owner', '维护责任', 'text'), field('trust', '来源可信状态', 'enum', { options: ['confirmed', 'unverified', 'historical', 'conflicted'] })], { defaultView: 'resource' }),
      Convention: schema('Convention', '项目约定', '知识资产'), KnownIssue: schema('KnownIssue', '已知问题 / 技术债', '知识资产'), HistoricalDecision: schema('HistoricalDecision', '历史决策', '知识资产'),
      Requirement: schema('Requirement', '需求', '协作治理'), Evidence: schema('Evidence', '证据', '协作治理'), Workflow: schema('Workflow', '工作流', '协作治理'),
      Change: schema('Change', '变更', '协作治理'), Task: schema('Task', '任务', '协作治理'), Delivery: schema('Delivery', '交付产物', '协作治理')
    };
  }

  function entity(typeId, data = {}) {
    return {
      id: data.id, typeId, name: data.name || data.id, description: data.description || data.desc || '',
      props: clone(data.props || {}), revision: data.revision || 'r1', status: data.status || 'draft', scope: data.scope || 'project',
      projectId: data.projectId || null, sourceIds: clone(data.sourceIds || []), sensitivity: data.sensitivity || 'internal',
      permissions: clone(data.permissions || { read: ['*'], edit: ['editor', 'admin'], approve: ['owner'] }),
      governance: clone(data.governance || { state: data.status === 'published' ? 'confirmed' : 'draft', owner: data.owner || '未指定' }),
      ...Object.fromEntries(Object.entries(data).filter(([key]) => !['id', 'typeId', 'name', 'description', 'desc', 'props', 'revision', 'status', 'scope', 'projectId', 'sourceIds', 'sensitivity', 'permissions', 'governance', 'owner'].includes(key)))
    };
  }

  function workflowDefinition(data = {}) {
    return {
      id: data.id, projectId: data.projectId || null, name: data.name || '未命名工作流', purpose: data.purpose || 'delivery',
      version: data.version || 'v1', status: data.status || 'published', scope: data.scope || 'project',
      stages: clone(data.stages || []).map((stage, index) => ({ ...stage, required: stage.required !== false, order: index, artifacts: clone(stage.artifacts || []), confirmation: !!stage.confirmation })),
      allowReopen: data.allowReopen !== false, createdAt: data.createdAt || now(), revision: data.revision || 'r1'
    };
  }
  function workflowInstance(definition, data = {}) {
    if (!definition?.id || !definition.version) throw new Error('工作流实例必须引用固定定义版本');
    return {
      id: data.id, projectId: data.projectId || definition.projectId, subjectId: data.subjectId || null,
      definitionId: definition.id, definitionVersion: definition.version, definitionSnapshot: clone(definition),
      currentStageId: data.currentStageId !== undefined ? data.currentStageId : (data.status === 'completed' ? null : definition.stages[0]?.id || null), status: data.status || 'active',
      participants: clone(data.participants || {}), records: clone(data.records || []), completed: clone(data.completed || []),
      artifacts: clone(data.artifacts || {}), confirmations: clone(data.confirmations || {}), blockers: clone(data.blockers || []), context: clone(data.context || {}),
      createdAt: data.createdAt || now(), updatedAt: data.updatedAt || now()
    };
  }

  function legacyType(project, id) {
    for (const [collection, typeId] of Object.entries(LEGACY_COLLECTIONS)) if ((project[collection] || []).some(item => item.id === id)) return typeId;
    if ((project.stages || []).some(item => item.id === id)) return 'Stage';
    if ((project.steps || []).some(item => item.id === id)) return 'Step';
    if ((project.knowledge || []).some(item => item.id === id)) return project.knowledge.find(item => item.id === id).typeId;
    return null;
  }
  function getEntity(project, id, version) {
    if (!id) return null;
    const bareId = String(id).split('@')[0];
    if (project.meta?.id === bareId) return { ...project.meta, typeId: 'Project', description: project.profile?.positioning || project.meta.goal || project.meta.desc, revision: project.versionLayers?.baseline || project.meta.version, props: project.profile || {} };
    const custom = (project.knowledge || []).find(item => item.id === bareId);
    if (custom) {
      if (version && custom.revision !== version) {
        if (custom.revisions?.[version]) return { ...clone(custom.revisions[version]), typeId: custom.typeId, revisions: custom.revisions };
        return null;
      }
      return custom;
    }
    for (const [collection, typeId] of Object.entries(LEGACY_COLLECTIONS)) {
      const item = (project[collection] || []).find(entry => entry.id === bareId);
      if (!item) continue;
      if (typeId === 'Rule') {
        const selected = version ? item.versions?.[version] || null : item.versions?.[item.currentVersion] || item.versions?.draft;
        return { ...item, typeId, description: selected?.scope || '', revision: selected?.version || version || item.currentVersion || 'draft', status: selected?.status || 'draft', selectedVersion: selected };
      }
      if (version && (item.version || item.revision || item.currentVersion) !== version) {
        const archiveType = { Action: 'action', Scenario: 'scenario', BusinessObject: 'object' }[typeId];
        const archived = archiveType && project.archives?.[`${archiveType}:${bareId}@${version}`];
        if (!archived) return null;
        return { ...archived, typeId, description: archived.description || archived.desc || archived.goal || archived.purpose || '', revision: version };
      }
      return { ...item, typeId, name: item.name || item.title || item.source || item.id, description: item.description || item.desc || item.goal || item.purpose || item.title || item.conclusion || '', revision: item.version || item.revision || item.status || 'r1' };
    }
    return null;
  }
  function allEntities(project, options = {}) {
    const legacy = [];
    for (const [collection, typeId] of Object.entries(LEGACY_COLLECTIONS)) {
      (project[collection] || []).forEach(item => legacy.push({ ...item, typeId, name: item.name || item.title || item.source || item.id, description: item.description || item.desc || item.goal || item.purpose || item.title || item.conclusion || '', revision: item.version || item.currentVersion || item.revision || 'r1' }));
    }
    const all = [...legacy, ...(project.knowledge || [])];
    const permission = options.permission || { role: 'admin', clearance: 'restricted' };
    const levels = { public: 0, internal: 1, sensitive: 2, restricted: 3 };
    return all.filter(item => {
      const allowed = !item.permissions || item.permissions.read?.includes('*') || item.permissions.read?.includes(permission.role);
      return allowed && (levels[item.sensitivity || 'internal'] || 0) <= (levels[permission.clearance] ?? 3);
    });
  }
  function schemaFor(project, typeId) { return project.schemaRegistry?.[typeId] || coreSchemas()[typeId] || null; }
  function registerType(project, definition) {
    if (!definition?.id || !/^[A-Za-z][A-Za-z0-9]+$/.test(definition.id)) throw new Error('类型 ID 需使用英文字母开头的稳定标识');
    if (!definition.name) throw new Error('类型名称不能为空');
    project.schemaRegistry ||= coreSchemas();
    if (project.schemaRegistry[definition.id]) throw new Error(`知识类型 ${definition.id} 已存在`);
    const fields = [field('name', '名称', 'text', { required: true }), field('description', '定义', 'longtext'), ...(definition.fields || []).map(item => ({ ...item }))];
    const fieldIds = new Set();
    fields.forEach(item => {
      if (!item.id || !item.label || fieldIds.has(item.id)) throw new Error('属性字段需要唯一的字段 ID 与显示名');
      fieldIds.add(item.id);
      if (!PROPERTY_TYPES.includes(item.dataType)) throw new Error(`不支持的属性类型 ${item.dataType}`);
    });
    project.schemaRegistry[definition.id] = {
      id: definition.id, name: definition.name, dimension: definition.dimension || '项目扩展', version: 1, fields,
      relations: definition.relations || ['references', 'contains', 'uses', 'supports'], defaultView: definition.defaultView || 'summary', editable: true,
      permissions: definition.permissions || { create: ['editor', 'admin'], edit: ['editor', 'admin'] }, custom: true
    };
    return project.schemaRegistry[definition.id];
  }
  function validateValue(definition, value, project) {
    if (definition.required && (value === undefined || value === null || value === '' || (Array.isArray(value) && !value.length))) return '不能为空';
    if (value === undefined || value === null || value === '') return null;
    switch (definition.dataType) {
      case 'number': case 'money': case 'duration': case 'unit': return typeof value !== 'number' || !Number.isFinite(value) ? '必须是有效数值' : null;
      case 'boolean': return typeof value !== 'boolean' ? '必须是布尔值' : null;
      case 'date': return !/^\d{4}-\d{2}-\d{2}/.test(value) || Number.isNaN(Date.parse(value)) ? '必须是有效日期' : null;
      case 'enum': return definition.options?.length && !definition.options.includes(value) ? '不在允许枚举值中' : null;
      case 'ref': case 'resource': return typeof value !== 'string' ? '必须是对象引用 ID 或资源定位' : (definition.dataType === 'ref' && project && !getEntity(project, value) ? '引用的知识对象不存在' : null);
      case 'refs': return !Array.isArray(value) || value.some(id => typeof id !== 'string') ? '必须是对象引用 ID 数组' : (project ? value.filter(id => !getEntity(project, id)).map(id => `引用 ${id} 不存在`).join('；') || null : null);
      case 'json': return typeof value !== 'object' ? '必须是结构化内容' : null;
      default: return typeof value !== 'string' ? '必须是文本' : null;
    }
  }
  function validateEntity(project, item) {
    const errors = [], schema = schemaFor(project, item.typeId);
    if (!schema) errors.push(`未注册知识类型 ${item.typeId}`);
    if (!item.id || !item.name) errors.push('稳定 ID 与名称不能为空');
    (schema?.fields || []).forEach(definition => {
      const key = definition.id === 'description' ? 'description' : definition.id === 'name' ? 'name' : definition.id;
      const value = ['name', 'description'].includes(key) ? item[key] : item.props?.[key];
      const error = validateValue(definition, value, project); if (error) errors.push(`${definition.label}${error}`);
    });
    return errors;
  }
  function createEntity(project, typeId, data) {
    if (!schemaFor(project, typeId)) throw new Error(`请先注册知识类型 ${typeId}`);
    if (getEntity(project, data.id)) throw new Error(`稳定 ID ${data.id} 已存在`);
    const item = entity(typeId, { ...data, projectId: project.meta.id });
    const errors = validateEntity(project, item); if (errors.length) throw new Error(errors.join('；'));
    project.knowledge ||= []; project.knowledge.push(item); return item;
  }
  function updateEntity(project, id, patch, expectedRevision) {
    const item = (project.knowledge || []).find(entry => entry.id === id);
    if (!item) throw new Error('通用知识编辑器仅修改扩展知识； legacy 对象使用变更工作台');
    if (expectedRevision && item.revision !== expectedRevision) throw new Error('对象修订已变化，请重新载入');
    item.revisions ||= {}; item.revisions[item.revision] ||= clone(item);
    const next = { ...item, ...clone(patch), id: item.id, typeId: item.typeId, revision: `r${Number(String(item.revision).replace(/\D/g, '')) + 1 || 2}` };
    const errors = validateEntity(project, next); if (errors.length) throw new Error(errors.join('；'));
    Object.keys(item).forEach(key => { if (!(key in next)) delete item[key]; }); Object.assign(item, next); return item;
  }
  function addRelation(project, relation) {
    if (!relation.from || !relation.to || !relation.type) throw new Error('关系需要 from / type / to');
    if (!getEntity(project, relation.from) || !getEntity(project, relation.to)) throw new Error('关系端点不存在');
    project.relations ||= [];
    if (project.relations.some(item => item.from === relation.from && item.to === relation.to && item.type === relation.type && item.scope === relation.scope)) return project.relations.find(item => item.from === relation.from && item.to === relation.to && item.type === relation.type);
    const item = {
      id: relation.id || `REL-${project.meta.id}-${String(project.relations.length + 1).padStart(5, '0')}`,
      from: relation.from, type: relation.type, to: relation.to, kind: relation.kind || 'knowledge', scope: relation.scope || 'project',
      version: relation.version || null, sourceId: relation.sourceId || null, trust: relation.trust || 'confirmed', verified: relation.verified !== false,
      direction: relation.direction || 'forward', explanation: relation.explanation || '', condition: relation.condition || null
    };
    project.relations.push(item); return item;
  }
  function relationsFor(project, id, options = {}) {
    const depth = Math.max(0, options.depth || 1), visited = new Set([id]), nodes = new Map(), edges = [];
    let frontier = [id];
    const root = getEntity(project, id); if (root) nodes.set(id, root);
    for (let level = 0; level < depth; level++) {
      const next = [];
      frontier.forEach(entityId => {
        (project.relations || []).forEach(relation => {
          const fromId = String(relation.from).split('@')[0], toId = String(relation.to).split('@')[0];
          if (fromId !== entityId && toId !== entityId) return;
          if (options.verifiedOnly && relation.verified === false && relation.trust !== 'confirmed') return;
          if (options.verifiedOnly && relation.trust === 'inferred') return;
          edges.push(relation); const other = fromId === entityId ? toId : fromId;
          const item = getEntity(project, other); if (item && !nodes.has(other)) nodes.set(other, item);
          if (!visited.has(other)) { visited.add(other); next.push(other); }
        });
      });
      frontier = next;
    }
    return { root, nodes: [...nodes.values()], edges: [...new Map(edges.map(item => [item.id, item])).values()] };
  }
  function incoming(project, id, type) { return (project.relations || []).filter(item => String(item.to).split('@')[0] === id && (!type || item.type === type)); }
  function outgoing(project, id, type) { return (project.relations || []).filter(item => String(item.from).split('@')[0] === id && (!type || item.type === type)); }
  function queryKnowledge(project, request = {}) {
    const root = getEntity(project, request.id, request.version);
    if (!root) return { ok: false, code: request.version ? 'VERSION_UNAVAILABLE' : 'NOT_FOUND', error: `${request.id}@${request.version || 'current'} 不可用` };
    if (root.typeId === 'Rule' && request.version && !root.selectedVersion) return { ok: false, code: 'VERSION_UNAVAILABLE', error: `${request.id}@${request.version} 不可用` };
    const permission = request.permission || { role: 'reader', clearance: 'internal' };
    if (!allEntities(project, { permission }).some(item => item.id === root.id)) return { ok: false, code: 'FORBIDDEN', error: '当前权限上下文不可读取此对象' };
    const graph = relationsFor(project, root.id, { depth: request.depth ?? 1, verifiedOnly: request.view === 'approved' });
    const visible = new Set(allEntities(project, { permission }).map(item => item.id));
    return { ok: true, projectId: project.meta.id, baseline: project.versionLayers?.baseline || project.meta.version, root: clone(root), nodes: graph.nodes.filter(item => visible.has(item.id)).map(clone), relations: graph.edges.filter(item => visible.has(item.from) && visible.has(item.to)).map(clone), query: clone(request) };
  }
  function health(project) {
    const items = allEntities(project), resources = project.knowledge?.filter(item => item.typeId === 'Resource') || [];
    const confirmedRelations = (project.relations || []).filter(item => item.verified !== false && item.trust !== 'inferred').length;
    const structured = items.filter(item => !['Evidence', 'Resource'].includes(item.typeId));
    return {
      modeled: structured.length, resources: resources.length, unparsedResources: resources.filter(item => ['unparsed', 'partial'].includes(item.props?.parseStatus)).length,
      verified: items.filter(item => item.governance?.state === 'confirmed' || item.status === 'published' || item.status === '已核验').length,
      missingSources: structured.filter(item => !item.sourceIds?.length && !incoming(project, item.id, 'supports').length).map(item => item.id),
      inferredRelations: (project.relations || []).filter(item => item.trust === 'inferred' || item.verified === false).map(item => item.id),
      confirmedRelations, totalRelations: (project.relations || []).length,
      note: '建模覆盖与事实可信度分别计算；覆盖率高不等于真实。'
    };
  }
  function paginate(items, page = 1, size = 20) {
    const pages = Math.max(1, Math.ceil(items.length / size)), current = Math.min(pages, Math.max(1, page));
    return { items: items.slice((current - 1) * size, current * size), page: current, pages, total: items.length };
  }

  function createWorkflow(project, data) {
    const def = workflowDefinition({ ...data, projectId: project.meta.id });
    if (!def.stages.length) throw new Error('工作流至少需要一个活动');
    const ids = new Set();
    def.stages.forEach((stage, index) => {
      if (!stage.id || ids.has(stage.id) || !stage.name || !stage.role) throw new Error('每个活动需要唯一 ID、名称和责任角色');
      ids.add(stage.id); stage.required = stage.required !== false; stage.order = index;
      if (stage.returnTo && !def.stages.some(candidate => candidate.id === stage.returnTo)) throw new Error(`退回目标 ${stage.returnTo} 不存在`);
    });
    project.workflowDefinitions ||= [];
    if (project.workflowDefinitions.some(item => item.id === def.id && item.version === def.version)) throw new Error(`工作流定义 ${def.id}@${def.version} 已存在`);
    project.workflowDefinitions.push(def); return def;
  }
  function instantiateWorkflow(project, definitionId, data = {}) {
    const def = (project.workflowDefinitions || []).find(item => item.id === definitionId && (!data.definitionVersion || item.version === data.definitionVersion));
    if (!def) throw new Error('指定工作流定义版本不存在');
    const instance = workflowInstance(def, { ...data, id: data.id || `WFI-${project.meta.id}-${String((project.workflowInstances?.length || 0) + 1).padStart(3, '0')}` });
    project.workflowInstances ||= [];
    if (project.workflowInstances.some(item => item.id === instance.id)) throw new Error(`工作流实例 ${instance.id} 已存在`);
    project.workflowInstances.push(instance); return instance;
  }
  function currentStage(instance) { return instance.definitionSnapshot.stages.find(stage => stage.id === instance.currentStageId) || null; }
  function workflowBlockers(instance, stageId = instance.currentStageId) {
    const stage = instance.definitionSnapshot.stages.find(item => item.id === stageId), blockers = [];
    if (!stage) return ['当前活动不存在'];
    if (stage.completionWhen?.allConfirmed) (stage.completionWhen.allConfirmed || []).forEach(id => { if (!instance.confirmations[id]) blockers.push(`需要确认：${id}`); });
    (stage.artifacts || []).forEach(id => { if (!instance.artifacts[id]) blockers.push(`需要产物：${id}`); });
    if (stage.confirmation && !instance.confirmations[stage.id]) blockers.push(`需要活动确认：${stage.name}`);
    return blockers;
  }
  function advanceWorkflow(project, instanceId, data = {}) {
    const instance = (project.workflowInstances || []).find(item => item.id === instanceId);
    if (!instance || instance.status !== 'active') return { ok: false, errors: ['工作流实例不可推进'] };
    const stage = currentStage(instance); if (!stage) return { ok: false, errors: ['当前活动不存在'] };
    if (stage.entryWhen?.equals) {
      const { key, value } = stage.entryWhen.equals; if (data.context?.[key] !== value) return { ok: false, errors: [`进入条件未满足：${key} = ${value}`] };
    }
    const blockers = workflowBlockers(instance); if (blockers.length && !data.force) return { ok: false, errors: blockers };
    const stages = instance.definitionSnapshot.stages, index = stages.findIndex(item => item.id === stage.id);
    instance.completed = instance.completed.filter(item => item.stageId !== stage.id);
    instance.completed.push({ stageId: stage.id, at: now(), by: data.by || '当前用户', note: data.note || '完成', optional: !stage.required });
    instance.records.push({ at: now(), by: data.by || '当前用户', type: 'complete', stageId: stage.id, note: data.note || '' });
    let next = stages.slice(index + 1).find(candidate => {
      if (!candidate.entryWhen?.equals) return true;
      const { key, value } = candidate.entryWhen.equals; return data.context?.[key] === value;
    });
    instance.currentStageId = next?.id || null; instance.status = next ? 'active' : 'completed'; instance.updatedAt = now();
    return { ok: true, instance, completed: !next };
  }
  function skipWorkflowStage(project, instanceId, data = {}) {
    const instance = (project.workflowInstances || []).find(item => item.id === instanceId), stage = instance && currentStage(instance);
    if (!instance || !stage || stage.required) return { ok: false, errors: ['只有当前可选活动可以跳过'] };
    instance.records.push({ at: now(), by: data.by || '当前用户', type: 'skip', stageId: stage.id, note: data.note || '按项目约定跳过' });
    instance.completed.push({ stageId: stage.id, at: now(), by: data.by || '当前用户', skipped: true });
    const stages = instance.definitionSnapshot.stages, index = stages.findIndex(item => item.id === stage.id), next = stages[index + 1];
    instance.currentStageId = next?.id || null; instance.status = next ? 'active' : 'completed'; instance.updatedAt = now(); return { ok: true, instance };
  }
  function returnWorkflow(project, instanceId, data = {}) {
    const instance = (project.workflowInstances || []).find(item => item.id === instanceId), stage = instance && currentStage(instance);
    if (!instance || !stage) return { ok: false, errors: ['实例或活动不存在'] };
    const stages = instance.definitionSnapshot.stages, index = stages.findIndex(item => item.id === stage.id);
    const targetId = data.targetStageId || stage.returnTo || stages[index - 1]?.id;
    if (!targetId || !stages.some(item => item.id === targetId)) return { ok: false, errors: ['此活动没有合法退回目标'] };
    if (stage.returnWhen?.equals) {
      const { key, value } = stage.returnWhen.equals; if (data.context?.[key] !== value) return { ok: false, errors: [`退回条件未满足：${key} = ${value}`] };
    }
    instance.records.push({ at: now(), by: data.by || '当前用户', type: 'return', fromStageId: stage.id, stageId: targetId, note: data.note || '退回重新处理' });
    instance.currentStageId = targetId; instance.status = 'active'; instance.completed = instance.completed.filter(item => item.stageId !== targetId); instance.updatedAt = now();
    return { ok: true, instance };
  }
  function reopenWorkflow(project, instanceId, data = {}) {
    const instance = (project.workflowInstances || []).find(item => item.id === instanceId);
    if (!instance || instance.status !== 'completed' || !instance.definitionSnapshot.allowReopen) return { ok: false, errors: ['此实例不能重新打开'] };
    const target = data.stageId || instance.definitionSnapshot.stages.at(-1)?.id;
    instance.status = 'active'; instance.currentStageId = target; instance.completed = instance.completed.filter(item => item.stageId !== target);
    instance.records.push({ at: now(), by: data.by || '当前用户', type: 'reopen', stageId: target }); instance.updatedAt = now(); return { ok: true, instance };
  }

  return {
    clone, PROPERTY_TYPES, TYPE_LABELS, coreSchemas, field, entity, registerType, schemaFor, validateValue, validateEntity,
    createEntity, updateEntity, getEntity, allEntities, legacyType, addRelation, relationsFor, incoming, outgoing, queryKnowledge, health, paginate,
    workflowDefinition, workflowInstance, createWorkflow, instantiateWorkflow, currentStage, workflowBlockers, advanceWorkflow, skipWorkflowStage, returnWorkflow, reopenWorkflow
  };
});
