/* Business Atlas V9 — engineering knowledge schemas and same-framework collaboration records. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const K = commonjs ? require('../v4-core.js') : root.AtlasCore;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const F = commonjs ? require('./v7-framework.js') : root.AtlasV7Framework;
  const api = factory(K, G, C, F);
  if (commonjs) module.exports = api;
  root.AtlasV9Model = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (K, G, C, F) {
  'use strict';
  const TYPES = [
    ['ArchitectureDecision', '架构决策（ADR）', '系统认知', 'ADR', [['status', '决策状态', 'enum', ['proposed', 'accepted', 'superseded', 'deprecated']], ['decision', '决策内容', 'longtext'], ['rationale', '选型依据', 'longtext'], ['consequences', '影响与代价', 'longtext']]],
    ['Technology', '编程语言与技术', '系统认知', 'TECH', [['version', '技术版本', 'text']]],
    ['Framework', '框架与运行时', '系统认知', 'FW', [['version', '框架版本', 'text'], ['runtime', '运行环境', 'text']]],
    ['Dependency', '第三方依赖', '系统认知', 'DEP', [['packageName', '包名', 'text'], ['version', '依赖版本', 'text'], ['compatibility', '兼容约束', 'longtext']]],
    ['Runtime', '运行时', '系统认知', 'RUN', [['version', '运行时版本', 'text']]],
    ['Component', '软件组件', '系统认知', 'COMP', [['responsibility', '组件职责', 'longtext'], ['codePaths', '代码位置', 'json']]],
    ['BuildPipeline', '构建流程', '系统认知', 'BUILD', [['entry', '构建入口', 'text'], ['output', '构建产物', 'text']]],
    ['DeploymentUnit', '部署单元', '系统认知', 'DUNIT', [['release', '部署版本', 'text'], ['topology', '部署结构', 'longtext']]],
    ['EngineeringConvention', '工程约定', '系统认知', 'CONV', [['scope', '约定适用范围', 'longtext'], ['verification', '验证方式', 'longtext']]],
    ['Test', '测试与验证', '协作治理', 'TEST', [['command', '执行命令', 'text'], ['coverageScope', '验证范围', 'longtext'], ['expected', '预期结果', 'longtext']]],
    ['UIOperation', '界面操作', '系统认知', 'UIOP', [['operation', '操作名称', 'text'], ['hotspot', '图像热点', 'json']]],
    ['SourceAsset', '原始视觉资料', '知识资产', 'ASSET', [['assetKind', '资料类型', 'text'], ['mediaType', '媒体类型', 'text'], ['sourceVersion', '界面或资料版本', 'text'], ['capturedAt', '采集时间', 'text']]],
    ['ExplanationAsset', '人工说明图', '知识资产', 'EXPLAIN', [['expression', '表达形式', 'enum', ['ascii', 'mermaid', 'svg-description']], ['content', '说明内容', 'longtext'], ['verification', '核验状态', 'text']]]
  ];
  const CATEGORIES = [
    ['system.architecture', '架构与系统边界', ['System', 'Subsystem', 'Component', 'Module'], '系统、子系统、组件、分层、边界与服务依赖'],
    ['system.technology', '技术栈与框架', ['Technology', 'Framework', 'Runtime', 'Dependency'], '语言、框架、运行时、SDK、包版本与兼容约束'],
    ['system.deployment', '部署与运行', ['DeploymentUnit', 'Environment', 'Deployment', 'Runtime'], '构建、部署结构、环境差异、基础设施与运行约束'],
    ['system.decisions', '架构决策与工程约定', ['ArchitectureDecision', 'EngineeringConvention', 'KnownIssue'], 'ADR、技术选型、设计约束、技术债、测试与迁移决策'],
    ['system.build', '构建与交付流水线', ['BuildPipeline'], '构建入口、产物、可重复构建和验证约束'],
    ['system.visual-assets', '界面与视觉资料', ['SourceAsset', 'ExplanationAsset'], '原始截图、说明图和界面版本；不替代结构化事实'],
    ['governance.feedback', '使用反馈', ['Feedback'], '体验、知识纠错、缺失、缺陷与建议的处理闭环'],
    ['governance.agent-runs', 'Agent 执行会话', ['AgentRun'], '任务、工具调用、观察、推断、提案与人工复核'],
    ['governance.imports', '知识包导入', ['ImportProposal'], 'AKP 校验、预览、人工确认、草稿与来源'],
    ['governance.visual-views', '语义图形视图', ['VisualView'], '由统一知识投影生成的流程、状态、架构、数据和时序视图'],
    ['governance.questions', '问题与回答', ['QuestionAnswer'], '不同角色基于同一知识的可追溯解释']
  ];
  function extendPredicates() {
    const wildcardEngineering = ['Component', 'Module', 'System', 'Subsystem', 'Framework', 'Dependency', 'DeploymentUnit', 'BuildPipeline', 'ArchitectureDecision', 'Technology', 'Runtime'];
    Object.assign(G.PREDICATES, {
      exposes: { label: '对外暴露', from: ['Component', 'Module', 'System'], to: ['API', 'Message'] },
      hosts: { label: '部署承载', from: ['DeploymentUnit', 'Environment'], to: ['Component', 'Module', 'System'] },
      verifies: { label: '验证', from: ['Test', 'Evidence', 'Resource'], to: '*' },
      impacts: { label: '影响', from: '*', to: '*' }
    });
    G.PREDICATES.implements.from = [...new Set([...G.PREDICATES.implements.from, 'Component'])];
    G.PREDICATES.constrains.from = [...new Set([...G.PREDICATES.constrains.from, 'ArchitectureDecision', 'EngineeringConvention'])];
    G.PREDICATES.constrains.to = [...new Set([...G.PREDICATES.constrains.to, ...wildcardEngineering])];
    G.PREDICATES.affects.to = [...new Set([...G.PREDICATES.affects.to, ...wildcardEngineering])];
    G.PREDICATES.reads.from = [...new Set([...G.PREDICATES.reads.from, 'Component'])];
    G.PREDICATES.writes.from = [...new Set([...G.PREDICATES.writes.from, 'Component'])];
    G.PREDICATES.contains.from = [...new Set([...G.PREDICATES.contains.from, ...wildcardEngineering, 'BuildPipeline'])];
  }
  function initProject(p) {
    extendPredicates();
    TYPES.forEach(([id, name, dimension, idPrefix, fields]) => {
      if (!K.schemaFor(p, id)) K.registerType(p, { id, name, dimension, idPrefix, fields: fields.map(([fieldId, label, dataType, options]) => K.field(fieldId, label, dataType, dataType === 'enum' ? { options } : {})), relations: ['contains', 'belongsTo', 'implements', 'references', 'dependsOn', 'affects', 'reads', 'writes', 'cites', 'explains', 'uses', 'requires', 'constrains', 'exposes', 'hosts', 'verifies', 'impacts'] });
      p.schemaRegistry[id].idPrefix ||= idPrefix;
    });
    p.frameworkOverrides ||= { customCategories: [], hidden: [], renames: {} };
    p.frameworkOverrides.customCategories ||= [];
    CATEGORIES.forEach(([categoryId, name, applicableTypes, description]) => {
      if (!p.frameworkOverrides.customCategories.some(cat => cat.categoryId === categoryId)) p.frameworkOverrides.customCategories.push({ categoryId, parentId: categoryId.split('.')[0], dimension: categoryId.split('.')[0], name, description, applicableTypes, defaultViews: ['collection', 'inspector'], suggestedCreationActions: applicableTypes.filter(id => K.schemaFor(p, id)).map(id => `CreateEntity:${id}`), completenessCriteria: [], order: 100, visibilityPolicy: 'always', match: null });
    });
    p.feedbackRecords ||= []; p.agentRuns ||= []; p.importProposals ||= []; p.visualViews ||= []; p.questionAnswers ||= []; p.externalIdMap ||= {}; p.knowledgePackages ||= [];
    p.applicabilitySchema ||= { dimensions: ['organization', 'tenant', 'region', 'environment', 'businessScope', 'systemVersion', 'configurationVersion', 'effectiveFrom', 'effectiveTo'], combination: 'all-declared-dimensions; missing=unknown; mismatch=not_applicable; explicit-conflict=conflict' };
    p.versionLayers ||= {};
    p.versionLayers.architectureRevision ||= p.versionLayers.baseline || 'untracked';
    p.versionLayers.codeRevision ||= 'untracked'; p.versionLayers.dependencyVersions ||= {}; p.versionLayers.deploymentVersion ||= null; p.versionLayers.runtimeConfiguration ||= null;
    p.meta.prototypeOnly = p.meta.prototypeOnly !== false;
    // Historical visualViews remain readable as inert backup data; no active visual feature.
    return p;
  }
  function initProjects(projects) { Object.values(projects || {}).forEach(initProject); return projects; }
  return { TYPES, CATEGORIES, extendPredicates, initProject, initProjects };
});
