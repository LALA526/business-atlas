/* Business Atlas V9 — deterministic, project-extensible applicability evaluation. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  root.AtlasV9Applicability = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const CORE_DIMENSIONS = ['organization', 'tenant', 'region', 'environment', 'businessScope', 'systemVersion', 'configurationVersion', 'effectiveFrom', 'effectiveTo'];
  const clone = value => value == null ? value : JSON.parse(JSON.stringify(value));
  const own = (object, key) => Object.prototype.hasOwnProperty.call(object || {}, key);
  const values = value => Array.isArray(value) ? value : [value];
  const present = value => value !== undefined && value !== null && value !== '' && !(Array.isArray(value) && !value.length);
  function dimensions(project) {
    const custom = project?.applicabilitySchema?.dimensions || project?.schemaRegistry?.Applicability?.dimensions || [];
    return [...new Set([...CORE_DIMENSIONS, ...custom.map(item => typeof item === 'string' ? item : item.id).filter(Boolean)])];
  }
  function conditionOf(item) {
    const qualifiers = item?.qualifiers || {};
    const explicit = item?.applicableContext || qualifiers.applicableContext || {};
    const condition = { ...explicit };
    dimensions({ applicabilitySchema: { dimensions: Object.keys({ ...qualifiers, ...explicit }) } }).forEach(key => {
      if (!own(condition, key) && own(qualifiers, key)) condition[key] = qualifiers[key];
    });
    if (!own(condition, 'configurationVersion') && own(qualifiers, 'configVersion')) condition.configurationVersion = qualifiers.configVersion;
    if (!own(condition, 'systemVersion') && own(qualifiers, 'version')) condition.systemVersion = qualifiers.version;
    if (item?.validTime) {
      if (!own(condition, 'effectiveFrom') && present(item.validTime.from)) condition.effectiveFrom = item.validTime.from;
      if (!own(condition, 'effectiveTo') && present(item.validTime.to)) condition.effectiveTo = item.validTime.to;
    }
    return condition;
  }
  function matches(expected, actual) {
    if (values(expected).some(value => value === '*' || value === 'any')) return true;
    const actualValues = values(actual).map(String);
    return values(expected).some(value => actualValues.includes(String(value)));
  }
  function evaluate(item, context = {}, project = null) {
    const condition = conditionOf(item), missing = [], mismatches = [], compared = [];
    if (item?.verificationStatus === 'conflicted' || item?.applicabilityStatus === 'conflict' || condition.conflict === true || item?.qualifiers?.applicabilityConflict === true) {
      return { status: 'conflict', reason: '来源或适用条件存在冲突，需要人工判断。', condition: clone(condition), missing, mismatches, compared };
    }
    dimensions(project).filter(key => !['effectiveFrom', 'effectiveTo'].includes(key)).forEach(key => {
      if (!present(condition[key])) return;
      if (!present(context[key])) { missing.push(key); return; }
      compared.push(key);
      if (!matches(condition[key], context[key])) mismatches.push({ dimension: key, expected: clone(condition[key]), actual: clone(context[key]) });
    });
    const from = condition.effectiveFrom, to = condition.effectiveTo;
    if (present(from) || present(to)) {
      if (!present(context.asOf)) missing.push('asOf');
      else {
        const asOf = Date.parse(context.asOf), start = present(from) ? Date.parse(from) : -Infinity, end = present(to) ? Date.parse(to) : Infinity;
        if (Number.isNaN(asOf) || (present(from) && Number.isNaN(start)) || (present(to) && Number.isNaN(end))) missing.push('validEffectiveDate');
        else {
          compared.push('effectiveTime');
          if (asOf < start || asOf > end) mismatches.push({ dimension: 'effectiveTime', expected: { from: from || null, to: to || null }, actual: context.asOf });
        }
      }
    }
    if (mismatches.length) return { status: 'not_applicable', reason: `不适用：${mismatches.map(item => item.dimension).join('、')} 与当前条件不同。`, condition: clone(condition), missing, mismatches, compared };
    if (missing.length) return { status: 'unknown', reason: `无法判断：缺少 ${[...new Set(missing)].join('、')}。未知不默认适用。`, condition: clone(condition), missing: [...new Set(missing)], mismatches, compared };
    return { status: 'applicable', reason: compared.length ? '所有已声明适用维度均与当前条件一致。' : '未声明额外适用限制。', condition: clone(condition), missing, mismatches, compared };
  }
  function annotate(items, context, project) {
    return (items || []).map(item => ({ ...clone(item), applicability: evaluate(item, context, project) }));
  }
  function filter(items, context, mode = 'explore', project = null) {
    const annotated = annotate(items, context, project);
    if (mode === 'explore') return annotated;
    return annotated.filter(item => item.applicability.status === 'applicable');
  }
  function summarize(items) {
    const counts = { applicable: 0, not_applicable: 0, unknown: 0, conflict: 0 };
    (items || []).forEach(item => { const status = item.applicability?.status || evaluate(item).status; counts[status] = (counts[status] || 0) + 1; });
    return counts;
  }
  function currentConclusion(items, context, project) {
    const annotated = annotate(items, context, project), counts = summarize(annotated);
    return { facts: annotated.filter(item => item.applicability.status === 'applicable'), comparisons: annotated.filter(item => item.applicability.status !== 'applicable'), counts, status: counts.conflict ? 'conflict' : counts.unknown ? 'unknown' : counts.applicable ? 'applicable' : 'not_applicable' };
  }
  return { CORE_DIMENSIONS, dimensions, conditionOf, evaluate, annotate, filter, summarize, currentConclusion };
});
