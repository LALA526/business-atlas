/* Business Atlas V7 — Project Knowledge Framework, coverage criteria and scalable catalog queries. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const K = commonjs ? require('../v4-core.js') : root.AtlasCore;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const P = commonjs ? require('./v6-projection.js') : root.AtlasV6Projection;
  const api = factory(K, G, C, P);
  if (commonjs) module.exports = api;
  root.AtlasV7Framework = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (K, G, C, P) {
  'use strict';
  const DIMENSIONS = [
    { dimension: 'project', name: '项目认知', description: '项目是什么、为什么存在、边界与整体全貌', order: 10 },
    { dimension: 'business', name: '业务认知', description: '业务怎样发生，软件如何支撑业务', order: 20 },
    { dimension: 'system', name: '系统认知', description: '系统怎样实现业务，技术如何组合', order: 30 },
    { dimension: 'assets', name: '知识资产', description: '项目概念、原始资料、约定与历史证据', order: 40 },
    { dimension: 'governance', name: '协作治理', description: '知识如何演进、交付、验证和持续维护', order: 50 }
  ];
  const category = (categoryId, dimension, name, applicableTypes, description, actions = [], extra = {}) => ({ categoryId, parentId: dimension, dimension, name, description, applicableTypes, defaultViews: extra.defaultViews || ['collection', 'inspector'], suggestedCreationActions: actions, completenessCriteria: extra.criteria || [], order: extra.order || 100, visibilityPolicy: extra.visibilityPolicy || 'always', match: extra.match || null });
  const CATEGORIES = [
    category('project.introduction', 'project', '项目定位与背景', ['Project'], '项目定位、背景和存在原因', ['edit-project']),
    category('project.goals', 'project', '业务目标与价值', ['Capability', 'Requirement'], '业务目标、价值与可验证结果', ['CreateEntity:Capability', 'CreateEntity:Requirement']),
    category('project.scope', 'project', '项目范围与边界', ['Project'], '负责与不负责的范围边界', ['edit-project']),
    category('project.stakeholders', 'project', '服务对象与参与方', ['Actor'], '项目服务对象、组织和参与方', ['CreateEntity:Actor']),
    category('project.roles', 'project', '组织与角色', ['Actor'], '项目角色及其职责', ['CreateEntity:Actor']),
    category('project.business-overview', 'project', '整体业务概况', ['Domain', 'Scenario'], '项目主要业务活动与能力', ['CreateEntity:Domain', 'CreateEntity:Scenario']),
    category('project.system-overview', 'project', '系统全貌', ['System', 'Subsystem'], '系统组成、外部依赖与技术全貌', ['CreateEntity:System']),
    category('project.constraints', 'project', '项目约束与关键决定', ['Convention', 'HistoricalDecision', 'KnownIssue'], '关键约束、项目约定与历史决定', ['CreateEntity:Convention', 'CreateEntity:HistoricalDecision']),
    category('business.domains', 'business', '业务领域', ['Domain'], '企业实际业务职责领域，不是标准目录维度', ['CreateEntity:Domain']),
    category('business.capabilities', 'business', '业务能力', ['Capability'], '组织需要具备的业务能力', ['CreateEntity:Capability']),
    category('business.features', 'business', '产品功能', ['Feature'], '软件为用户提供的功能及其业务价值', ['CreateEntity:Feature']),
    category('business.scenarios', 'business', '业务场景', ['Scenario'], '参与者在特定条件下完成业务目标的过程', ['CreateEntity:Scenario']),
    category('business.actions', 'business', '业务动作', ['Action'], '谁在什么条件下执行什么动作', ['CreateEntity:Action']),
    category('business.actors', 'business', '业务角色', ['Actor'], '业务参与者及其职责和关注', ['CreateEntity:Actor']),
    category('business.objects', 'business', '业务对象', ['BusinessObject', 'ManufacturingBatch', 'AdministrativeMatter', 'LegalMaterial'], '业务中的关键事物、属性及身份', ['CreateEntity:BusinessObject', 'schema-driven']),
    category('business.rules', 'business', '业务规则', ['Rule'], '业务约束、适用范围、条件与例外', ['CreateEntity:Rule']),
    category('business.decisions', 'business', '业务决策', ['Rule'], '结构化判断条件与结果', ['CreateEntity:Rule'], { match: (p, e) => C.ruleVersion(e)?.supportsEvaluation }),
    category('business.events', 'business', '业务事件', ['Event'], '已经发生的业务事实及后续影响', ['CreateEntity:Event']),
    category('business.lifecycles', 'business', '状态与生命周期', ['BusinessObject', 'ManufacturingBatch'], '合法状态、真实状态转移及守卫', ['DefineTransition']),
    category('business.exceptions', 'business', '异常与恢复', ['Exception'], '业务异常、处理、补偿与恢复路径', ['CreateEntity:Exception']),
    category('system.systems', 'system', '系统与子系统', ['System', 'Subsystem'], '系统边界、组成与职责', ['CreateEntity:System']),
    category('system.modules', 'system', '软件模块', ['Module'], '软件模块及其处理职责', ['CreateEntity:Module']),
    category('system.surfaces', 'system', '用户界面', ['Surface'], '用户操作入口与页面结构', ['CreateEntity:Surface']),
    category('system.operations', 'system', '页面操作', ['Surface'], '页面操作与业务动作触发关系', ['CreateEntity:Surface'], { defaultViews: ['operations', 'relations'] }),
    category('system.apis', 'system', 'API 与消息', ['API', 'Message'], '接口、消息契约、提供方与调用方', ['CreateEntity:API', 'CreateEntity:Message']),
    category('system.data', 'system', '数据实体与字段', ['DataEntity', 'DataField'], '数据含义、归属、字段与约束', ['CreateEntity:DataEntity']),
    category('system.data-flow', 'system', '数据关系与流转', ['DataEntity'], '数据读取、写入、传递和一致性', ['LinkKnowledge'], { defaultViews: ['data-flow', 'relations'] }),
    category('system.config', 'system', '业务配置', ['Configuration'], '配置取值、生效范围和业务影响', ['CreateEntity:Configuration']),
    category('system.flags', 'system', '功能开关', ['Configuration'], '功能开关与发布控制', ['CreateEntity:Configuration'], { match: (p, e) => /flag|开关/i.test(`${e.name}${e.description}`) }),
    category('system.environments', 'system', '部署环境', ['Environment', 'Deployment'], '环境、部署、软件版本与配置差异', ['CreateEntity:Environment']),
    category('system.integrations', 'system', '外部系统与集成', ['Integration', 'System'], '第三方系统、协议与集成责任', ['CreateEntity:Integration']),
    category('system.constraints', 'system', '实现约束与技术依赖', ['Convention', 'KnownIssue', 'Module'], '事务、一致性、技术依赖和实现限制', ['CreateEntity:KnownIssue']),
    category('assets.terms', 'assets', '术语与概念', ['Term'], '项目术语、特殊概念和统一定义', ['CreateEntity:Term']),
    category('assets.names', 'assets', '同义词、缩写与历史名称', ['Term'], '名称演变、别名与易混淆概念', ['CreateEntity:Term'], { defaultViews: ['names', 'relations'] }),
    category('assets.requirements', 'assets', '原始需求资料', ['Resource'], '原始需求、工作项和业务意图', ['CreateEntity:Resource'], { match: (p, e) => /requirement|需求|work-item/i.test(e.props?.resourceType || '') }),
    category('assets.specifications', 'assets', '业务规范', ['Resource'], '批准业务规范与作业指导', ['CreateEntity:Resource'], { match: (p, e) => /document|controlled|规范/i.test(e.props?.resourceType || '') }),
    category('assets.technical', 'assets', '技术文档', ['Resource'], '系统、接口、数据与实现文档', ['CreateEntity:Resource'], { match: (p, e) => /repository|implementation|technical/i.test(e.props?.resourceType || '') }),
    category('assets.manuals', 'assets', '操作手册', ['Resource'], '操作说明与业务作业手册', ['CreateEntity:Resource'], { match: (p, e) => /manual|手册|作业/i.test(`${e.props?.resourceType || ''}${e.name}`) }),
    category('assets.regulations', 'assets', '法规和标准', ['Resource'], '法律、法规、标准与政策条款', ['CreateEntity:Resource'], { match: (p, e) => /legislation|regulation|法|标准/i.test(`${e.props?.resourceType || ''}${e.name}`) }),
    category('assets.conventions', 'assets', '项目约定', ['Convention'], '项目特有约定与共同工作方式', ['CreateEntity:Convention']),
    category('assets.decisions', 'assets', '历史决策', ['HistoricalDecision'], '历史设计、旧规范与历史意图', ['CreateEntity:HistoricalDecision']),
    category('assets.evidence', 'assets', '原始证据与参考来源', ['Resource', 'Evidence'], '代码、配置、测试、文档及人工确认来源', ['CreateEntity:Resource']),
    category('governance.requirements', 'governance', '业务需求', ['Requirement'], '批准需求、业务要求与验收条件', ['CreateEntity:Requirement']),
    category('governance.issues', 'governance', '待澄清问题', ['KnownIssue'], '问题、冲突、影响与确认责任', ['CreateIssue']),
    category('governance.changes', 'governance', '变更', ['Change'], '对象与关系的变更集及差异', []),
    category('governance.reviews', 'governance', '审核与批准', ['Change'], '审核决定、人工确认和批准记录', []),
    category('governance.baselines', 'governance', '知识基线', ['Baseline'], '对象、断言与流程的批准版本快照', []),
    category('governance.tasks', 'governance', '开发任务', ['Task'], '开发、测试与 Agent 任务及其状态', ['create-task']),
    category('governance.workflows', 'governance', '工作流', ['Workflow'], '工作流定义与执行实例', []),
    category('governance.deliveries', 'governance', '交付产物', ['Delivery'], '结构化契约、快照和交付产物', []),
    category('governance.acceptance', 'governance', '测试与验收', ['Evidence', 'Requirement'], '测试证据、验收条件与结果', ['CreateEntity:Resource']),
    category('governance.health', 'governance', '知识冲突与过期信息', ['KnownIssue'], '冲突、过期、待复核与缺失', [])
  ];
  function schemaFor(p, typeId) {
    const schema = K.schemaFor(p, typeId);
    if (schema) return { instantiable: schema.instantiable !== false, editable: schema.editable !== false, ...schema };
    const generic = K.coreSchemas();
    if (generic[typeId]) return generic[typeId];
    // A small set of semantic types used by V5/V6 is registered here, not accepted through a hardcoded industry whitelist.
    const semanticNames = { Event: '业务事件', Exception: '异常与恢复', Message: '消息', Evidence: '证据', Change: '变更', Task: '任务', Baseline: '知识基线', Workflow: '工作流', Delivery: '交付产物', Integration: '外部集成', HistoricalDecision: '历史决策', KnownIssue: '已知问题', Convention: '项目约定', Requirement: '需求', Capability: '业务能力', Environment: '部署环境', Deployment: '部署', DataField: '数据字段', Subsystem: '子系统', Message: '消息' };
    if (!semanticNames[typeId]) return null;
    return { id: typeId, name: semanticNames[typeId], dimension: '项目知识', fields: [K.field('name', '名称', 'text', { required: true }), K.field('description', '定义', 'longtext')], relations: ['references', 'contains', 'dependsOn'], editable: true, instantiable: true, permissions: { create: ['editor', 'admin'], edit: ['editor', 'admin'] } };
  }
  function categoriesForEntity(p, entity) {
    if (!entity) return [];
    return CATEGORIES.filter(cat => cat.applicableTypes.includes(entity.typeId) && (!cat.match || cat.match(p, entity))).map(cat => cat.categoryId);
  }
  function registerCategory(p, definition) {
    if (!definition.categoryId || !DIMENSIONS.some(d => d.dimension === definition.dimension)) throw new Error('自定义分类需要稳定 ID 和有效一级维度');
    if (CATEGORIES.some(c => c.categoryId === definition.categoryId) || (p.frameworkOverrides?.customCategories || []).some(c => c.categoryId === definition.categoryId)) throw new Error('分类 ID 已存在');
    p.frameworkOverrides ||= { customCategories: [], hidden: [], renames: {} };
    const custom = category(definition.categoryId, definition.dimension, definition.name, definition.applicableTypes || [], definition.description || '', definition.suggestedCreationActions || [], definition);
    p.frameworkOverrides.customCategories.push(custom); return custom;
  }
  function allCategories(p) {
    const custom = p.frameworkOverrides?.customCategories || [], hidden = new Set(p.frameworkOverrides?.hidden || []), renames = p.frameworkOverrides?.renames || {};
    return [...CATEGORIES, ...custom].filter(c => !hidden.has(c.categoryId)).map(c => ({ ...c, name: renames[c.categoryId] || c.name }));
  }
  function categoryById(p, id) { return allCategories(p).find(c => c.categoryId === id) || null; }
  function getEntitiesForCategory(p, cat, options = {}) {
    if (!cat) return [];
    return C.allEntities(p, { draft: options.draft !== false, includeRetired: true, permission: options.permission }).filter(e => cat.applicableTypes.includes(e.typeId) && (!cat.match || cat.match(p, e)));
  }
  function trustState(p, entity, options = {}) {
    const assertions = options.relations || C.getAssertions(p, entity.id, { draft: true });
    if ((options.conflictRecords || p.conflictRecords)?.some(c => c.status !== 'resolved' && c.assertionIds?.some(id => assertions.some(a => a.assertionId === id)))) return 'conflicted';
    if ((options.impactReviews || p.impactReviews)?.some(r => r.entityId === entity.id && r.status === 'requires-review')) return 'stale';
    if (assertions.some(a => a.verificationStatus === 'unverified' || a.verificationStatus === 'conflicted')) return 'unverified';
    return entity.governance?.state === 'confirmed' ? 'confirmed' : 'unknown';
  }
  function entityState(p, entity, options = {}) {
    if (entity.status === 'retired') return 'retired';
    if (entity.status === 'draft') return 'draft';
    const trust = trustState(p, entity, options); if (trust === 'conflicted' || trust === 'stale') return trust;
    if (trust === 'unverified') return 'unverified';
    const hasDescription = !!(entity.description || entity.goal || entity.purpose);
    if (!hasDescription) return 'basic';
    const related = (options.relations || C.getAssertions(p, entity.id, { draft: true })).length;
    return related ? trust : 'partial';
  }
  function relatedIds(p, id, predicates) { return C.getAssertions(p, id, { draft: true }).filter(a => !predicates || predicates.includes(a.predicate)).map(a => a.subject === id ? a.object : a.subject).filter(Boolean); }
  function coverage(p, entity, options = {}) {
    const readEntity = id => options.getEntity ? options.getEntity(id) : C.getEntity(p, id, { draft: true }), relations = options.relations || C.getAssertions(p, entity.id, { draft: true }), linked = predicates => relations.filter(a => predicates.includes(a.predicate)).map(a => a.subject === entity.id ? a.object : a.subject).filter(Boolean);
    const checks = [], add = (id, label, status, reason, action) => checks.push({ id, label, status, reason, action: action || null });
    const definition = entity.description || entity.goal || entity.purpose;
    add('definition', '业务定义', definition ? 'met' : 'missing', definition ? '已记录业务含义' : '尚未说明业务含义', 'set-property');
    if (entity.typeId === 'Feature') {
      const scenario = entity.scenarioId || entity.props?.scenarioId || linked(['references', 'belongsTo']).some(id => readEntity(id)?.typeId === 'Scenario');
      const actions = linked(['implements', 'triggers']).filter(id => readEntity(id)?.typeId === 'Action');
      const actionRelations = actions.flatMap(actionId => (options.getRelations ? options.getRelations(actionId) : C.getAssertions(p, actionId, { draft: true })).map(a => ({ actionId, assertion: a })));
      const relatedThroughActions = [...new Set(actionRelations.map(({ actionId, assertion }) => assertion.subject === actionId ? assertion.object : assertion.subject).filter(Boolean))];
      const rules = [...new Set([...linked(['constrains', 'blocks', 'uses']), ...relatedThroughActions])].filter(id => readEntity(id)?.typeId === 'Rule');
      const tech = [...new Set([...linked(['implements', 'triggers', 'reads', 'writes']), ...relatedThroughActions])].filter(id => ['Surface', 'API', 'DataEntity'].includes(readEntity(id)?.typeId));
      const acceptance = entity.acceptance || entity.props?.acceptance || [];
      add('scenario', '所属业务场景', scenario ? 'met' : 'missing', scenario ? '已建立场景归属' : '尚未关联业务场景', 'link-scenario');
      add('actions', '业务动作', actions.length ? 'met' : 'missing', actions.length ? `${actions.length} 个明确关联` : '尚未关联业务动作', 'bind-action');
      add('rules', '关键业务规则', rules.length ? 'met' : 'unknown', rules.length ? `${rules.length} 条关联规则` : '可能不需要规则，也可能尚未关联，需要业务判断', 'link-rule');
      add('implementation', '实现映射', tech.length ? 'met' : 'missing', tech.length ? `${tech.length} 项技术映射` : '尚无 UI、API 或数据映射', 'bind-api');
      add('acceptance', '验收条件', acceptance.length ? 'met' : 'missing', acceptance.length ? `${acceptance.length} 条验收条件` : '尚未定义验收条件', 'set-acceptance');
      const sources = entity.sourceIds?.length || relations.some(a => a.predicate === 'cites');
      add('evidence', '依据与验收证据', sources ? 'met' : 'unverified', sources ? '已关联来源' : '尚未关联可信来源或验收证据', 'attach-evidence');
    } else if (entity.typeId === 'Rule') {
      const version = C.ruleVersion(entity);
      add('scope', '适用范围', version?.scope ? 'met' : 'missing', version?.scope || '规则不能默认全项目生效', 'edit-rule');
      add('decision', '判断或约束定义', version?.supportsEvaluation ? (version.rows?.length ? 'met' : 'missing') : version?.constraint ? 'met' : 'missing', version?.supportsEvaluation ? (version.rows?.length ? `${version.rows.length} 条判断行` : '没有判断行') : (version?.constraint ? '保留人工约束' : '约束未定义'), 'edit-rule');
      const sources = entity.sourceIds?.length || relations.some(a => a.predicate === 'cites');
      add('evidence', '规则来源', sources ? 'met' : 'unverified', sources ? '已关联来源' : '规则缺少可信来源', 'attach-evidence');
    } else if (entity.typeId === 'API') {
      add('endpoint', '接口契约', entity.props?.path ? 'met' : 'missing', entity.props?.path ? `${entity.props.method || ''} ${entity.props.path}` : '接口路径未定义', 'set-api');
      const actions = linked(['implements']);
      add('business', '业务映射', actions.length ? 'met' : 'missing', actions.length ? `${actions.length} 个实现关系` : '尚未关联业务动作', 'bind-action');
      const data = linked(['reads', 'writes']);
      add('data', '数据读写', data.length ? 'met' : 'unknown', data.length ? `${data.length} 项读写关系` : '尚未建立数据读写；如不需要请人工标记不适用', 'bind-data');
    } else if (entity.typeId === 'Configuration') {
      add('scope', '生效范围', entity.props?.effectiveWhen ? 'met' : 'missing', entity.props?.effectiveWhen || '尚未说明环境、组织与业务范围', 'set-config');
      const impacts = linked(['affects']);
      add('impacts', '业务影响', impacts.length ? 'met' : 'missing', impacts.length ? `${impacts.length} 条影响断言` : '尚未关联受影响规则或功能', 'link-knowledge');
    } else if (entity.typeId === 'Term') {
      add('term-definition', '标准定义', entity.props?.shortDefinition || definition ? 'met' : 'missing', entity.props?.shortDefinition || definition || '尚未建立标准定义', 'set-property');
      add('context', '适用上下文', entity.props?.context ? 'met' : 'unknown', entity.props?.context || '尚未说明术语适用上下文', 'set-property');
    } else if (entity.typeId === 'BusinessObject') {
      add('states', '合法状态', entity.states?.length ? 'met' : 'not_applicable', entity.states?.length ? `${entity.states.length} 个合法状态` : '当前对象未使用状态模型', 'set-states');
      if (entity.states?.length) add('transitions', '状态转移', entity.transitions?.length ? 'met' : 'unknown', entity.transitions?.length ? `${entity.transitions.length} 条明确转移` : '只有状态目录，尚未建立生命周期', 'add-transition');
    }
    const trust = trustState(p, entity, options), state = entityState(p, entity, options);
    const structural = { met: checks.filter(c => c.status === 'met').length, missing: checks.filter(c => c.status === 'missing').length, unknown: checks.filter(c => c.status === 'unknown').length, unverified: checks.filter(c => c.status === 'unverified').length, notApplicable: checks.filter(c => c.status === 'not_applicable').length };
    return { entityId: entity.id, state, trust, structural, checks, complete: structural.missing === 0 && structural.unverified === 0, note: '结构覆盖与事实可信分别计算；检查齐全不代表内容真实。' };
  }
  function catalog(p, input = {}) {
    const cat = categoryById(p, input.categoryId), query = String(input.query || '').toLowerCase(), state = input.state || 'all';
    let items = getEntitiesForCategory(p, cat, input).map(entity => ({ entity, state: entityState(p, entity) })).filter(row => (state === 'all' || row.state === state) && `${row.entity.id}${row.entity.name}${row.entity.description || ''}${row.entity.aliases || ''}`.toLowerCase().includes(query));
    const sort = input.sort || 'name';
    items.sort((a, b) => sort === 'state' ? a.state.localeCompare(b.state) || a.entity.name.localeCompare(b.entity.name, 'zh-CN') : sort === 'id' ? a.entity.id.localeCompare(b.entity.id) : a.entity.name.localeCompare(b.entity.name, 'zh-CN') || a.entity.id.localeCompare(b.entity.id));
    const pageSize = Math.min(100, Math.max(1, input.pageSize || 20)), pages = Math.max(1, Math.ceil(items.length / pageSize)), page = Math.min(pages, Math.max(1, input.page || 1));
    const allCategoryItems = getEntitiesForCategory(p, cat, input);
    return { category: cat, items: items.slice((page - 1) * pageSize, page * pageSize), total: items.length, page, pages, pageSize, counts: { total: allCategoryItems.length, draft: allCategoryItems.filter(e => e.status === 'draft').length, unverified: allCategoryItems.filter(e => entityState(p, e) === 'unverified').length, retired: allCategoryItems.filter(e => e.status === 'retired').length } };
  }
  function repairCoreFeatureLinks(p) {
    if (!p._v7Seed) return;
    const mappings = {
      'PRJ-00001': { feature: 'FEAT-A01', scenario: 'SCN-00126', actions: ['ACT-00424'], source: 'SRC-A01' },
      'PRJ-00002': { feature: 'FEAT-B01', scenario: 'SCN-B001', actions: ['ACT-B0005', 'ACT-B0008'], source: 'SRC-B01' },
      'PRJ-00003': { feature: 'FEAT-M01', scenario: 'SCN-M001', actions: ['ACT-M005', 'ACT-M009'], source: 'SRC-M01' }
    };
    const config = mappings[p.meta.id]; p._v7Seed = false; if (!config) return;
    const add = (id, subject, predicate, object, explanation) => {
      if ((p.assertions || []).some(a => a.subject === subject && a.predicate === predicate && a.object === object)) return;
      const a = { assertionId: id, projectId: p.meta.id, subject, predicate, object, qualifiers: { source: 'V5 已建立功能映射' }, sourceRefs: [{ resourceId: config.source, locator: explanation }], factKind: 'approved_specification', verificationStatus: 'confirmed', validTime: { from: '2026-11-14', to: null }, revision: 'a1', owner: 'V5 项目知识迁移', explanation, status: 'published' };
      p.assertions.push(a); p.assertionRevisionStore ||= {}; p.assertionRevisionStore[`${id}@a1`] = G.deepFreeze(C.clone(a)); p.knowledgeRevisions[`${id}@a1`] = G.deepFreeze({ ...C.clone(a), id, typeId: 'Assertion', name: `${subject} ${predicate} ${object}` });
      (p.baselines || []).forEach(b => { if (b.id === p.versionLayers.baseline) b.assertionRevisions[id] = 'a1'; });
    };
    add(`AST-V7-${config.feature}-SCENARIO`, config.feature, 'references', config.scenario, '既有功能与场景映射');
    config.actions.forEach(actionId => add(`AST-V7-${config.feature}-${actionId}`, config.feature, 'implements', actionId, '既有功能与动作实现映射'));
  }
  function initProject(p) { p.frameworkOverrides ||= { customCategories: [], hidden: [], renames: {} }; repairCoreFeatureLinks(p); return p; }
  function initProjects(projects) { Object.values(projects).forEach(initProject); return projects; }
  function generateIndexFixtures(p, count = 2000) {
    if (!K.schemaFor(p, 'IndexRecord')) { K.registerType(p, { id: 'IndexRecord', name: '轻量索引记录', dimension: '系统认知', fields: [{ id: 'sequence', label: '序号', dataType: 'number', required: true }, { id: 'group', label: '索引组', dataType: 'enum', options: ['alpha', 'beta', 'gamma', 'delta'] }] }); registerCategory(p, { categoryId: 'custom.IndexRecord', dimension: 'system', name: '轻量索引记录', description: '用于验证大规模导航的轻量对象，不代表复杂业务知识', applicableTypes: ['IndexRecord'], suggestedCreationActions: ['CreateEntity:IndexRecord'] }); }
    const existing = C.allEntities(p, { draft: true, includeRetired: true }).filter(e => e.typeId === 'IndexRecord').length;
    for (let i = 0; i < count; i++) {
      const sequence = existing + i + 1, id = `IDX-${p.meta.id}-${String(sequence).padStart(6, '0')}`;
      if (C.getEntity(p, id, { draft: true })) continue;
      p.knowledge.push(K.entity('IndexRecord', { id, name: `索引记录 ${String(sequence).padStart(6, '0')}`, description: '', revision: 'r1', status: i % 7 === 0 ? 'draft' : 'published', props: { sequence, group: ['alpha', 'beta', 'gamma', 'delta'][sequence % 4] } }));
    }
    return C.allEntities(p, { draft: true }).filter(e => e.typeId === 'IndexRecord').length;
  }
  return { DIMENSIONS, CATEGORIES, schemaFor, categoriesForEntity, registerCategory, allCategories, categoryById, getEntitiesForCategory, trustState, entityState, coverage, catalog, initProject, initProjects, generateIndexFixtures };
});
