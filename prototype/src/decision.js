/* Business Atlas V9.3 — deterministic, authorized business decision evaluation. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const R = commonjs ? require('./v8-read.js') : root.AtlasV8Read;
  const Access = commonjs ? require('./v9-access.js') : root.AtlasV9Access;
  const api = factory(C, R, Access);
  if (commonjs) module.exports = api;
  root.AtlasV93Decision = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (C, R, Access) {
  'use strict';
  const clone = C.clone;
  const own = (object, key) => Object.prototype.hasOwnProperty.call(object || {}, key);
  const SUPPORTED_OPERATORS = Object.freeze(['equals', 'notEquals', 'contains', 'startsWith', 'exists', 'notExists', 'greaterThan', 'lessThan', 'in', 'all', 'any']);
  const OUTCOME_TYPES = Object.freeze({ execute: '执行业务动作', reject: '拒绝或阻止', no_action: '不执行', update_state: '更新业务状态', emit_event: '产生事件', manual: '要求人工处理', unknown: '无法判断' });
  const OPERATOR_LABELS = { equals: '精确等于', notEquals: '不等于', contains: '包含', startsWith: '以前缀开始', exists: '已提供且非空值', notExists: '未提供或为空值', greaterThan: '大于', lessThan: '小于', in: '属于集合', all: '全部满足', any: '至少一项满足' };
  const result = (code, status, extra = {}) => ({ code, status, outcome: null, matchedRow: null, evaluatedRows: [], preconditions: [], missingFacts: [], unsupportedConditions: [], conflicts: [], sources: [], ...extra });
  const present = (values, inputId) => own(values, inputId) && values[inputId] !== undefined && values[inputId] !== null;

  function conditionFromRow(row) {
    if (row?.condition) return clone(row.condition);
    if (row?.conditions) return clone(row.conditions);
    const parts = Object.entries(row?.when || {}).filter(([, expected]) => expected !== 'any').map(([inputId, expected]) => {
      if (expected && typeof expected === 'object' && !Array.isArray(expected) && expected.op) return { inputId, ...clone(expected) };
      return { op: 'equals', inputId, value: expected };
    });
    return parts.length === 1 ? parts[0] : { op: 'all', conditions: parts };
  }

  function inputFor(version, inputId) { return (version?.inputs || []).find(input => input.id === inputId) || null; }
  function displayValue(input, value) { return input?.options?.find(option => option.value === value)?.label ?? (value === undefined ? '未提供' : value === null ? '空值' : Array.isArray(value) ? value.join('、') : String(value)); }

  function describeCondition(condition, version) {
    if (!condition || !condition.op) return '条件表达缺失，无法解释';
    if (['all', 'any'].includes(condition.op)) return `(${(condition.conditions || []).map(item => describeCondition(item, version)).join(condition.op === 'all' ? ' 且 ' : ' 或 ')})`;
    const input = inputFor(version, condition.inputId), label = input?.label || input?.businessMeaning || condition.inputId || '未指定业务输入';
    if (['exists', 'notExists'].includes(condition.op)) return `${label}${OPERATOR_LABELS[condition.op]}`;
    const value = condition.op === 'in' ? (condition.values || []).map(item => displayValue(input, item)).join('、') : displayValue(input, condition.value);
    const comparison = OPERATOR_LABELS[condition.op] || `未支持的比较 ${condition.op}`;
    const stringSemantics = ['contains', 'startsWith'].includes(condition.op) ? `（${condition.caseSensitive ? '区分' : '不区分'}大小写${condition.nullHandling ? `，空值${condition.nullHandling}` : ''}）` : '';
    return `${label} ${comparison} ${value}${stringSemantics}`;
  }

  function unknownCondition(condition, reason, missingFacts = []) { return { status: 'unknown', reason, missingFacts, condition: clone(condition) }; }
  function evaluateCondition(condition, values, version) {
    if (!condition || !SUPPORTED_OPERATORS.includes(condition.op)) return { status: 'unsupported', reason: `条件 ${condition?.op || '未定义'} 暂不支持模拟，不能默认成立。`, missingFacts: [], condition: clone(condition) };
    if (['all', 'any'].includes(condition.op)) {
      const children = (condition.conditions || []).map(item => evaluateCondition(item, values, version));
      if (!children.length) return { status: 'unsupported', reason: `${condition.op} 没有子条件。`, missingFacts: [], condition: clone(condition) };
      const decisive = condition.op === 'all' ? children.find(item => item.status === 'false') : children.find(item => item.status === 'true');
      if (decisive) return { status: condition.op === 'all' ? 'false' : 'true', reason: decisive.reason, missingFacts: [...new Set(children.flatMap(item => item.missingFacts || []))], children, condition: clone(condition) };
      const unsupported = children.find(item => item.status === 'unsupported');
      if (unsupported) return { status: 'unsupported', reason: unsupported.reason, missingFacts: [...new Set(children.flatMap(item => item.missingFacts || []))], children, condition: clone(condition) };
      const unknown = children.some(item => item.status === 'unknown');
      if (unknown) return { status: 'unknown', reason: '部分子条件缺少必要事实。', missingFacts: [...new Set(children.flatMap(item => item.missingFacts || []))], children, condition: clone(condition) };
      return { status: condition.op === 'all' ? 'true' : 'false', reason: condition.op === 'all' ? '全部子条件成立。' : '没有子条件成立。', missingFacts: [], children, condition: clone(condition) };
    }
    const inputId = condition.inputId;
    if (!inputId) return { status: 'unsupported', reason: '条件未指定业务输入。', missingFacts: [], condition: clone(condition) };
    const input = inputFor(version, inputId), label = input?.label || inputId, has = present(values, inputId), actual = values[inputId];
    if (condition.op === 'exists') return { status: has ? 'true' : 'false', reason: `${label}${has ? '已提供且非 null/undefined' : '未提供或为 null/undefined'}。`, missingFacts: [], condition: clone(condition) };
    if (condition.op === 'notExists') return { status: !has ? 'true' : 'false', reason: `${label}${!has ? '未提供或为 null/undefined' : '已提供'}。`, missingFacts: [], condition: clone(condition) };
    if (!has) return unknownCondition(condition, `缺少必要输入：${label}。`, [inputId]);
    if (['contains', 'startsWith'].includes(condition.op) && typeof condition.caseSensitive !== 'boolean') return { status: 'unsupported', reason: `${label} 的字符串比较尚未声明是否区分大小写。`, missingFacts: [], condition: clone(condition) };
    if (['contains', 'startsWith'].includes(condition.op) && actual == null && condition.nullHandling !== 'not-match') return unknownCondition(condition, `${label} 为空值时的比较语义尚未确认。`, [inputId]);
    const sensitive = condition.caseSensitive !== false, left = typeof actual === 'string' && !sensitive ? actual.toLocaleLowerCase() : actual, expectedRaw = condition.value, right = typeof expectedRaw === 'string' && !sensitive ? expectedRaw.toLocaleLowerCase() : expectedRaw;
    let matched;
    switch (condition.op) {
      case 'equals': matched = actual === condition.value; break;
      case 'notEquals': matched = actual !== condition.value; break;
      case 'contains': matched = Array.isArray(left) ? left.includes(right) : typeof left === 'string' && typeof right === 'string' ? left.includes(right) : false; break;
      case 'startsWith': matched = typeof left === 'string' && typeof right === 'string' ? left.startsWith(right) : false; break;
      case 'greaterThan': matched = typeof actual === 'number' && typeof condition.value === 'number' && Number.isFinite(actual) && Number.isFinite(condition.value) ? actual > condition.value : false; break;
      case 'lessThan': matched = typeof actual === 'number' && typeof condition.value === 'number' && Number.isFinite(actual) && Number.isFinite(condition.value) ? actual < condition.value : false; break;
      case 'in': matched = Array.isArray(condition.values) && condition.values.includes(actual); break;
      default: return { status: 'unsupported', reason: `条件 ${condition.op} 暂不支持模拟。`, missingFacts: [], condition: clone(condition) };
    }
    return { status: matched ? 'true' : 'false', reason: `${describeCondition(condition, version)}：${matched ? '成立' : '不成立'}。`, missingFacts: [], actual: clone(actual), condition: clone(condition) };
  }

  function isApprovedTarget(rule, version, ctx) {
    const factKind = version?.factKind || rule.factKind || 'target_specification';
    return ctx.versionMode !== 'draft' && ['published', 'approved'].includes(rule.status) && ['published', 'approved'].includes(version?.status) && factKind === 'target_specification';
  }

  function outcomeFor(version, id) { const outcome = (version?.outcomes || []).find(outcome => outcome.id === id); return outcome ? { type: 'unknown', ...outcome, ...(version?.outcomeSemantics?.[id] || {}) } : null; }
  function priorityFor(version, rows) {
    if (version?.priorityPolicy) return { confirmed: version.priorityPolicy.confirmed === true, strategy: version.priorityPolicy.strategy || 'unconfirmed', source: version.priorityPolicy.source || null };
    const priorities = rows.map(row => row.priority);
    const explicit = priorities.every(priority => Number.isFinite(priority)) && new Set(priorities).size === priorities.length;
    return { confirmed: explicit && ['published', 'approved'].includes(version?.status), strategy: explicit ? 'approved-unique-row-priority' : 'unconfirmed', source: explicit ? '规则版本中的唯一显式行优先级' : null };
  }

  function evaluateRule(project, ctx, ruleOrId, values = {}, options = {}) {
    const id = typeof ruleOrId === 'string' ? ruleOrId : ruleOrId?.id;
    const read = Access.readEntity(project, ctx, id);
    if (!read.ok) return result(read.code, 'unknown', { ruleRef: { ruleId: id }, explanation: '规则在当前权限、版本或适用上下文中不可读。' });
    const rule = read.data, version = C.ruleVersion(rule, { version: options.version, strict: !!options.version });
    const ruleRef = { ruleId: rule.id, version: version?.version || rule.currentVersion, revision: rule.revision, baselineId: ctx.baselineId };
    const sources = R.getSources(project, ctx, rule.id).map(ref => ({ resourceId: ref.resourceId, name: ref.entity?.name || ref.confirmedBy || '人工确认', locator: ref.locator, sourceVersion: ref.entity?.props?.sourceVersion || null }));
    if (!version) return result('VERSION_UNAVAILABLE', 'unknown', { ruleRef, sources, explanation: '指定规则版本不存在。' });
    const formal = options.formal !== false;
    if (formal && !isApprovedTarget(rule, version, ctx)) return result('UNAPPROVED_TARGET', 'unapproved', { ruleRef: { ...ruleRef, factKind: version.factKind || rule.factKind || 'target_specification', approvalStatus: version.status || rule.status }, sources, explanation: '该规则不是当前批准基线中的目标业务规范，不能用于正式业务判断。' });
    if (!version.supportsEvaluation) return result('MANUAL_RULE', 'manual', { ruleRef, sources, explanation: version.constraint || version.summary || '此规则需要人工核验，不能自动求值。' });
    const applicability = rule.applicability || null;
    if (formal && applicability?.status === 'not_applicable') return result('NOT_APPLICABLE', 'unknown', { ruleRef, sources, applicability, explanation: applicability.reason });
    if (formal && ['unknown', 'conflict'].includes(applicability?.status)) return result(applicability.status === 'conflict' ? 'APPLICABILITY_CONFLICT' : 'APPLICABILITY_UNKNOWN', applicability.status === 'conflict' ? 'conflict' : 'unknown', { ruleRef, sources, applicability, explanation: applicability.reason });
    const preconditions = (version.preconditions || []).map(condition => ({ ...evaluateCondition(condition, values, version), description: describeCondition(condition, version) }));
    if (preconditions.some(item => item.status === 'false')) return result('PRECONDITION_NOT_MET', 'decided', { ruleRef, sources, preconditions, outcome: clone(version.preconditionOutcome || { id: 'NOT_APPLICABLE', type: 'no_action', label: '前提不成立，不执行该判断' }), explanation: '判断前提不成立，不进入规则行匹配。' });
    if (preconditions.some(item => ['unknown', 'unsupported'].includes(item.status))) return result('PRECONDITION_UNKNOWN', 'unknown', { ruleRef, sources, preconditions, missingFacts: [...new Set(preconditions.flatMap(item => item.missingFacts || []))], unsupportedConditions: preconditions.filter(item => item.status === 'unsupported'), explanation: '判断前提缺少事实或包含暂不支持的条件。' });
    const rows = [...(version.exceptions || []).map(row => ({ ...row, exception: true })), ...(version.rows || []).map(row => ({ ...row, exception: false }))];
    const evaluatedRows = rows.map((row, index) => {
      const condition = conditionFromRow(row), evaluation = evaluateCondition(condition, values, version);
      return { rowId: row.id || `ROW-${index + 1}`, priority: row.priority, outcomeId: row.outcomeId, exception: !!row.exception, description: describeCondition(condition, version), ...evaluation };
    });
    const matched = evaluatedRows.filter(row => row.status === 'true'), unknown = evaluatedRows.filter(row => row.status === 'unknown'), unsupported = evaluatedRows.filter(row => row.status === 'unsupported');
    const missingFacts = [...new Set(evaluatedRows.flatMap(row => row.missingFacts || []))];
    const priority = priorityFor(version, rows);
    const conflicts = [];
    const selected = matched.slice().sort((a, b) => (a.priority ?? Number.MAX_SAFE_INTEGER) - (b.priority ?? Number.MAX_SAFE_INTEGER));
    if (matched.length > 1) {
      const outcomes = new Set(matched.map(row => row.outcomeId));
      if (!priority.confirmed && outcomes.size > 1) conflicts.push({ code: 'UNCONFIRMED_PRIORITY', reason: '多条规则行命中不同结果，但项目未确认优先级策略。', rowIds: matched.map(row => row.rowId) });
      else {
        const bestPriority = selected[0]?.priority, same = selected.filter(row => row.priority === bestPriority);
        if (new Set(same.map(row => row.outcomeId)).size > 1) conflicts.push({ code: 'RULE_CONFLICT', reason: '相同优先级的规则行产生不同业务结果。', rowIds: same.map(row => row.rowId) });
      }
    }
    if (conflicts.length) return result(conflicts[0].code, 'conflict', { ruleRef, sources, evaluatedRows, preconditions, missingFacts, unsupportedConditions: unsupported, conflicts, priority, explanation: conflicts[0].reason });
    if (selected.length) {
      const best = selected[0], unknownBefore = [...unknown, ...unsupported].filter(row => (row.priority ?? Number.MAX_SAFE_INTEGER) < (best.priority ?? Number.MAX_SAFE_INTEGER));
      if (unknownBefore.length) return result('HIGHER_PRIORITY_UNKNOWN', 'unknown', { ruleRef, sources, evaluatedRows, preconditions, missingFacts, unsupportedConditions: unsupported, priority, explanation: '更高优先级的条件无法判断，不能跳过后直接采用较低优先级结果。' });
      const outcome = outcomeFor(version, best.outcomeId);
      if (!outcome) return result('OUTCOME_MISSING', 'unknown', { ruleRef, sources, evaluatedRows, preconditions, missingFacts, priority, explanation: `决策行 ${best.rowId} 引用了不存在的结果。` });
      return result(outcome.id, 'decided', { ruleRef, sources, evaluatedRows, preconditions, matchedRow: best, outcome: clone({ type: 'unknown', ...outcome }), missingFacts, unsupportedConditions: unsupported, priority, explanation: `${best.description}，命中“${outcome.label}”。` });
    }
    if (unsupported.length) return result('UNSUPPORTED_CONDITION', 'unknown', { ruleRef, sources, evaluatedRows, preconditions, missingFacts, unsupportedConditions: unsupported, priority, explanation: '存在暂不支持模拟的条件，未将未知当作不成立，也未套用默认结果。' });
    if (unknown.length) return result('MISSING_INPUT', 'unknown', { ruleRef, sources, evaluatedRows, preconditions, missingFacts, priority, explanation: `缺少必要业务事实：${missingFacts.map(inputId => inputFor(version, inputId)?.label || inputId).join('、')}。无法确认规则是否命中。` });
    const defaultRef = version.defaultOutcome || version.default || null;
    if (defaultRef) {
      const defaultOutcome = typeof defaultRef === 'string' ? outcomeFor(version, defaultRef) : defaultRef.outcomeId ? outcomeFor(version, defaultRef.outcomeId) : defaultRef;
      if (defaultOutcome) return result(defaultOutcome.id || 'DEFAULT', 'decided', { ruleRef, sources, evaluatedRows, preconditions, outcome: clone({ type: 'unknown', ...defaultOutcome, ...(version?.outcomeSemantics?.[defaultOutcome.id] || {}) }), priority, defaultUsed: true, explanation: `所有已定义条件均不成立，执行已批准默认分支“${defaultOutcome.label}”。` });
    }
    return result('NO_MATCH_NO_DEFAULT', 'unknown', { ruleRef, sources, evaluatedRows, preconditions, priority, explanation: '没有规则行命中，且未定义已批准默认结果；不能自行假定结果。' });
  }

  function evaluateRuleSet(project, ctx, ruleIds, values = {}, options = {}) {
    const evaluations = ruleIds.map(id => evaluateRule(project, ctx, id, values, options));
    const decided = evaluations.filter(item => item.status === 'decided' && item.outcome && !['PRECONDITION_NOT_MET'].includes(item.code));
    if (!decided.length) return { ...result('NO_DECISION', evaluations.some(item => item.status === 'conflict') ? 'conflict' : 'unknown', { evaluations }), explanation: '没有可用规则产生确定结果。' };
    const outcomes = new Set(decided.map(item => item.outcome.id));
    if (outcomes.size > 1) {
      const policy = options.crossRulePriority;
      if (!policy?.confirmed) return { ...result('UNCONFIRMED_RULE_PRIORITY', 'conflict', { evaluations, decided }), explanation: '多条规则产生不同结果，未确认跨规则优先级，不能默认第一条、最新或最窄规则优先。' };
      const ordered = decided.slice().sort((a, b) => (policy.order || []).indexOf(a.ruleRef.ruleId) - (policy.order || []).indexOf(b.ruleRef.ruleId));
      return { ...clone(ordered[0]), evaluations, crossRulePriority: clone(policy) };
    }
    return { ...clone(decided[0]), evaluations, combinedOutcomeCount: decided.length };
  }

  function verifyCase(project, ctx, caseOrId, options = {}) {
    const id = typeof caseOrId === 'string' ? caseOrId : caseOrId?.id;
    const read = Access.readEntity(project, ctx, id);
    if (!read.ok) return { ok: false, code: read.code, status: 'unknown', explanation: '案例在当前上下文中不可读。' };
    const businessCase = read.data, props = businessCase.props || {}, decisionRef = props.decisionRef;
    if (!decisionRef?.ruleId) return { ok: false, code: 'DECISION_REF_MISSING', status: 'unknown', caseId: id, explanation: '案例没有引用明确业务规则。' };
    const evaluation = evaluateRule(project, ctx, decisionRef.ruleId, props.inputs || {}, { formal: options.formal !== false, version: decisionRef.version });
    const expected = props.expected || {}, expectsUnknown = expected.status === 'unknown' || expected.outcomeId === 'UNKNOWN';
    const matches = expectsUnknown ? evaluation.status === 'unknown' : evaluation.status === 'decided' && evaluation.outcome?.id === expected.outcomeId;
    return { ok: matches, code: matches ? 'CASE_MATCH' : 'CASE_CONFLICT', status: matches ? 'verified' : 'conflict', caseId: id, caseKind: props.caseKind, expected: clone(expected), evaluation, confirmationStatus: props.confirmationStatus || businessCase.verificationStatus || 'unverified', explanation: matches ? '案例结果与所引用规则版本一致；示例没有替代或修改规则。' : '案例与规则不一致，已形成冲突信号；平台没有修改规则以适配示例。' };
  }

  function semanticCompleteness(rule) {
    const version = C.ruleVersion(rule), checks = [];
    const add = (id, label, met, reason) => checks.push({ id, label, status: met ? 'met' : 'unknown', reason });
    add('purpose', '业务目的', !!(version?.businessPurpose || rule.description || version?.summary), version?.businessPurpose || rule.description || version?.summary || '缺少业务目的');
    add('context', 'Context / 适用范围', !!version?.scope, version?.scope || '缺少适用范围');
    add('inputs', 'Inputs / 业务输入', !!(version?.inputs || []).length && (version.inputs || []).every(input => input.label || input.businessMeaning), (version?.inputs || []).length ? '输入需要具有业务名称和含义' : '尚未定义输入');
    add('preconditions', 'Preconditions / 前提', !!version?.scope || !!(version?.preconditions || []).length, version?.scope || '前提未单独形式化，仅按适用范围阅读');
    add('evaluation', 'Evaluation / 比较语义', !version?.supportsEvaluation || !!(version?.rows || []).length, version?.supportsEvaluation ? '每行可展开查看自然语言和技术输入' : '人工约束，不伪装可执行');
    add('priority', 'Priority / 优先级', !version?.supportsEvaluation || priorityFor(version, [...(version?.exceptions || []), ...(version?.rows || [])]).confirmed, priorityFor(version, [...(version?.exceptions || []), ...(version?.rows || [])]).strategy);
    add('branch', 'Branch & Outcome / 分支结果', !version?.supportsEvaluation || ((version?.rows || []).length > 0 && (version?.outcomes || []).length > 0), `${(version?.rows || []).length} 条判断行 · ${(version?.outcomes || []).length} 种业务结果`);
    add('default', 'Default / 默认分支', !!(version?.defaultOutcome || version?.default), version?.defaultOutcome || version?.default ? '已定义默认分支' : '未定义默认结果；未命中时返回无法判断');
    add('exception', 'Exception / 异常', !!(version?.exceptions || []).length || !!version?.exceptionHandling || version?.supportsEvaluation === false, version?.exceptionHandling || ((version?.exceptions || []).length ? '已定义例外规则' : '未记录专门异常；不能推断没有异常'));
    add('evidence', 'Evidence / 来源', !!(rule.sourceIds || []).length || !!version?.source, version?.source || '需要读取对象与断言来源');
    const unknownCount = checks.filter(check => check.status !== 'met').length;
    return { status: unknownCount ? 'partial' : 'complete', checks, unknownCount, note: '完整性提示不是新的对象总状态，不替代 Governance、Verification、Applicability 或 Coverage。' };
  }

  return { SUPPORTED_OPERATORS, OUTCOME_TYPES, OPERATOR_LABELS, conditionFromRow, describeCondition, evaluateCondition, evaluateRule, evaluateRuleSet, verifyCase, semanticCompleteness, isApprovedTarget, priorityFor };
});
