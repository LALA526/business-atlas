/* Unified prototype write authorization. This is a model check, not production security. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  root.AtlasWriteAccess = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const CAPABILITIES = {
    'project:create': ['editor', 'owner', 'admin'],
    'knowledge:draft': ['developer', 'editor', 'owner', 'admin'],
    'changes:submit': ['editor', 'owner', 'admin'],
    'baseline:publish': ['owner', 'admin'],
    'task:create': ['developer', 'editor', 'owner', 'admin'],
    'decision:propose': ['developer', 'editor', 'owner', 'admin'],
    'decision:approve': ['owner', 'admin'],
    'impact:write': ['developer', 'editor', 'owner', 'admin'],
    'delivery:record': ['developer', 'editor', 'owner', 'admin'],
    'contract:generate': ['developer', 'editor', 'owner', 'admin'],
    'issue:write': ['developer', 'editor', 'owner', 'admin'],
    'task:accept': ['owner', 'admin'],
    'delivery:export': ['developer', 'editor', 'owner', 'admin'],
    'backup:full': ['admin']
  };
  const SYSTEM_CONTEXT = Object.freeze({ projectId: '*', baselineId: 'system', versionMode: 'system', permissionContext: Object.freeze({ role: 'admin', clearance: 'restricted', identity: 'controlled-fixture-migration' }) });
  function role(ctx) { return ctx?.permissionContext?.role || 'reader'; }
  function allows(ctx, capability) { return (CAPABILITIES[capability] || []).includes(role(ctx)); }
  function assert(ctx, capability) {
    if (!allows(ctx, capability)) {
      const error = new Error(`当前角色 ${role(ctx)} 无权执行 ${capability}；需要 ${(CAPABILITIES[capability] || []).join(' / ')}。`);
      error.code = 'WRITE_FORBIDDEN'; error.capability = capability; throw error;
    }
    return true;
  }
  return { CAPABILITIES, SYSTEM_CONTEXT, allows, assert, role };
});
