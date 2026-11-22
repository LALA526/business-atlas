/* Minimal project factory shared by the current runtime. No historical demo corpus. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const G = commonjs ? require('./v5-core.js') : root.AtlasV5;
  const api = factory(G);
  if (commonjs) module.exports = api;
  root.AtlasProjectFactory = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (G) {
  'use strict';
  function createBlankProject(id, name, goal) {
    if (!id || !name) throw new Error('空白项目必须提供 id 和 name');
    const project = {
      meta: { id, name, version: 'draft', status: 'draft', desc: goal || '从业务目标开始建立项目知识。', goal: goal || '从业务目标开始建立项目知识。', created: '2026-11-16', counters: {}, prototypeOnly: true },
      actors: [], objects: [], domains: [], capabilities: [], rules: [], scenarios: [], stages: [], steps: [], actions: [], exceptions: [], surfaces: [], facts: [], relations: [], flows: [], changes: [], issues: [], deliveries: [], knowledge: [], resources: [], workflowDefinitions: [], workflowInstances: [],
      archives: {}, schemaRegistry: {}, profile: { positioning: goal || '', background: goal || '', audience: '', inScope: [], outScope: [] },
      versionLayers: { baseline: `BL-${id}-DRAFT`, release: null, config: null, deployments: [] },
      scale: { actions: 0, surfaces: 0, knowledge: 0, note: '仅展示已建立的业务知识。' }, workspace: { homeSections: ['intro', 'concerns'], readingPaths: [] }, presentationProfiles: {}
    };
    return G.initializeProject(project);
  }
  function createProjectSet() { return {}; }
  return { createBlankProject, createProjectSet };
});
