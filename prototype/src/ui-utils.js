/* Small shared presentation helpers for the current text-first prototype. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  root.AtlasUI = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const esc = value => String(value ?? '').replace(/[&<>'"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
  const badge = (text, tone = 'blue') => `<span class="badge ${esc(tone)}">${esc(text)}</span>`;
  const empty = (title, copy = '') => `<div class="empty"><b>${esc(title)}</b>${copy ? `<p>${esc(copy)}</p>` : ''}</div>`;
  const status = value => ({ published: '已发布', approved: '已批准', draft: '草稿', pending: '待审核', active: '进行中', completed: '已完成', confirmed: '已确认', unverified: '待核验', proposed: '候选' })[value] || value || '未声明';
  const tone = value => ['published', 'approved', 'completed', 'confirmed', 'consistent', 'passed'].includes(value) ? 'green' : ['conflicted', 'inconsistent', 'failed'].includes(value) ? 'red' : ['draft', 'pending', 'proposed', 'unverified', 'unknown'].includes(value) ? 'amber' : 'blue';
  const typeName = (project, type) => project?.schemaRegistry?.[type]?.name || ({ Event: '业务事件', Exception: '异常处理', Assertion: '语义断言', BusinessCase: '业务案例', ObservedBehavior: '当前观察' })[type] || type;
  return { esc, badge, empty, status, tone, typeName };
});
