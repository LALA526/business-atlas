/* Business Atlas V9 — role-aware explanations, feedback loop, agent evidence and semantic visual projections. */
(function (root, factory) {
  const commonjs = typeof module === 'object' && module.exports;
  const C = commonjs ? require('./v6-core.js') : root.AtlasV6;
  const Access = commonjs ? require('./v9-access.js') : root.AtlasV9Access;
  const W = commonjs ? require('./write-access.js') : root.AtlasWriteAccess;
  const api = factory(C, Access, W);
  if (commonjs) module.exports = api;
  root.AtlasV9Services = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (C, Access, W) {
  'use strict';
  const clone = C.clone;
  const escText = value => String(value ?? '').trim();
  const INTENTS = ['WHY', 'HOW', 'IMPACT', 'DIFFERENCE', 'REQUIREMENT'];
  function inferIntent(text) {
    if (/影响|修改|变更|升级|替换/.test(text)) return 'IMPACT';
    if (/差异|区别|不同|对比|版本/.test(text)) return 'DIFFERENCE';
    if (/为什么|为何|原因|阻止|必须/.test(text)) return 'WHY';
    if (/如何|怎样|怎么|流程|运行/.test(text)) return 'HOW';
    if (/能否|是否|支持|能力|需求|可以|能不能/.test(text)) return 'REQUIREMENT';
    return 'HOW';
  }
  function bigrams(text) {
    const clean = String(text).replace(/[\s，。！？、；：,.!?;:'"“”‘’（）()\[\]【】]/g, '');
    const words = clean.match(/[A-Za-z][A-Za-z0-9_.-]{1,}|[\u4e00-\u9fff]{2,}/g) || [];
    const tokens = new Set(words.filter(word => /^[A-Za-z]/.test(word)));
    words.filter(word => /^[\u4e00-\u9fff]/.test(word)).forEach(word => {
      for (let i = 0; i < word.length - 1; i++) tokens.add(word.slice(i, i + 2));
      for (let i = 0; i < word.length - 2; i++) tokens.add(word.slice(i, i + 3));
      if (word.length <= 12) tokens.add(word);
    });
    const stop = new Set(['这个', '系统', '是否', '能否', '可以', '支持', '什么', '如何', '怎样', '一下', '我们', '你们', '当前', '项目', '能力', '知识', '管理', '业务', '实现', '功能', '人员', '不解', '理解', '技术', '全部', '前提', '回答']);
    return [...tokens].filter(token => !stop.has(token) && token.length >= 2);
  }
  function scoreEntity(entity, query) {
    const haystack = `${entity.id} ${entity.name} ${entity.description || ''} ${entity.goal || ''} ${entity.props?.responsibility || ''}`.toLowerCase(), needle = query.toLowerCase();
    let score = entity.id.toLowerCase() === needle ? 100 : entity.name.toLowerCase() === needle ? 80 : needle.includes(entity.name.toLowerCase()) && entity.name.length > 1 ? 40 : 0;
    bigrams(query).forEach(token => { if (haystack.includes(token.toLowerCase())) score += token.length >= 3 ? 4 : 1; });
    if (score > 0 && ['Feature', 'Capability', 'Component', 'Module', 'ArchitectureDecision', 'Rule', 'BuildPipeline', 'Test'].includes(entity.typeId)) score += .5;
    return score;
  }
  function citationsFor(project, ctx, entity, relations) {
    const citations = [{ knowledgeId: entity.id, revision: entity.revision, baselineId: ctx.baselineId, applicability: entity.applicability || null }];
    relations.filter(a => a.subject === entity.id || a.object === entity.id).forEach(assertion => {
      (assertion.sourceRefs || []).forEach(ref => {
        if (ref.kind === 'human_confirmation') citations.push({ knowledgeId: entity.id, assertionId: assertion.assertionId, humanConfirmation: ref.confirmedBy, locator: ref.locator, baselineId: ctx.baselineId });
        else {
          const source = Access.readEntity(project, ctx, ref.resourceId);
          if (source.ok) citations.push({ knowledgeId: entity.id, assertionId: assertion.assertionId, sourceId: ref.resourceId, locator: ref.locator, revision: source.data.revision, baselineId: ctx.baselineId });
        }
      });
    });
    (entity.sourceIds || []).forEach(id => { const source = Access.readEntity(project, ctx, id); if (source.ok) citations.push({ knowledgeId: entity.id, sourceId: id, revision: source.data.revision, locator: source.data.props?.locator || '对象级来源', baselineId: ctx.baselineId }); });
    return [...new Map(citations.map(item => [JSON.stringify(item), item])).values()];
  }
  function capabilityStatus(entity, project) {
    const prototype = project.meta.prototypeOnly || entity.props?.maturity === 'prototype' || entity.factKind && entity.factKind !== 'approved_specification';
    if (entity.status === 'retired') return 'explicitly_unsupported';
    if (prototype) return 'prototype_only';
    if (['published', 'approved'].includes(entity.status) && entity.governance?.state === 'confirmed') return 'supported';
    if (['draft', 'pending'].includes(entity.status)) return 'unconfirmed';
    return 'insufficient_evidence';
  }
  function ask(project, ctx, input = {}) {
    const query = escText(input.text), intent = input.intent || inferIntent(query), role = input.role || 'newcomer';
    if (!query) return { ok: false, code: 'CONTEXT_INSUFFICIENT', guidance: '请输入问题，或先选择问题意图和知识对象。', intent };
    const all = Access.listEntities(project, ctx).data;
    let focus = input.rootId ? all.filter(entity => entity.id === input.rootId) : [];
    if (focus.length && !input.followUpOf) {
      const ranked = all.filter(entity => entity.id !== input.rootId).map(entity => ({ entity, score: scoreEntity(entity, query) })).filter(item => item.score >= 4).sort((a, b) => b.score - a.score), related = [];
      ['Feature', 'Rule'].forEach(typeId => { const candidate = ranked.find(item => item.entity.typeId === typeId); if (candidate) related.push(candidate.entity); });
      ranked.forEach(item => { if (related.length < 2 && !related.includes(item.entity)) related.push(item.entity); });
      focus = [...focus, ...related.slice(0, 2)];
    }
    if (!focus.length && input.followUpOf) {
      const previous = (project.questionAnswers || []).find(answer => answer.answerId === input.followUpOf);
      const previousIds = previous?.citations?.map(citation => citation.knowledgeId) || [];
      focus = all.filter(entity => previousIds.includes(entity.id));
    }
    if (!focus.length) focus = all.map(entity => ({ entity, score: scoreEntity(entity, query) })).filter(item => item.score > 0).sort((a, b) => b.score - a.score).slice(0, 6).map(item => item.entity);
    if (!focus.length) {
      const candidates = all.filter(entity => ['Feature', 'Capability', 'Scenario', 'Component', 'Module', 'Rule'].includes(entity.typeId)).slice(0, 8);
      return { ok: false, code: 'CONTEXT_INSUFFICIENT', intent, query, guidance: '当前自由文本无法可靠映射到知识对象。请选择意图和对象后继续；系统不会假装理解。', candidates: candidates.map(entity => ({ id: entity.id, name: entity.name, typeId: entity.typeId })) };
    }
    const conclusions = [], citations = [], compared = [], limitations = new Set();
    focus.forEach(entity => {
      const relations = Access.readRelations(project, ctx, entity.id).data;
      const applicable = ctx.applicabilityMode === 'current' ? relations.filter(a => a.applicability?.status === 'applicable') : relations;
      const unknown = relations.filter(a => a.applicability?.status === 'unknown');
      const conflicts = relations.filter(a => a.applicability?.status === 'conflict');
      const ownCitations = citationsFor(project, ctx, entity, relations); citations.push(...ownCitations);
      if (intent === 'REQUIREMENT') {
        const status = capabilityStatus(entity, project);
        const label = { supported: '已明确支持', prototype_only: '特定条件支持（原型能力）', unconfirmed: '尚未确认', explicitly_unsupported: '已明确不支持', insufficient_evidence: '当前资料不足' }[status];
        conclusions.push({ knowledgeId: entity.id, title: entity.name, status, text: `${label}：${entity.description || '定义尚未补充。'}`, factKind: entity.factKind || 'approved_specification', applicability: entity.applicability, citations: ownCitations });
      } else if (intent === 'WHY') {
        const why = applicable.filter(a => ['constrains', 'blocks', 'requires', 'affects'].includes(a.predicate));
        conclusions.push({ knowledgeId: entity.id, title: entity.name, status: why.length ? 'explained' : 'insufficient_evidence', text: why.length ? why.map(a => `${a.predicate}：${a.explanation || a.qualifiers?.condition || '存在明确约束关系'}`).join('；') : `${entity.description || '当前对象'}；没有足够已授权约束关系解释原因。`, citations: ownCitations });
      } else if (intent === 'IMPACT') {
        conclusions.push({ knowledgeId: entity.id, title: entity.name, status: applicable.length ? 'impact_candidates' : 'insufficient_evidence', text: applicable.length ? `当前条件内有 ${applicable.length} 条明确关联需要复核：${applicable.map(a => `${a.predicate}(${a.subject} → ${a.object || a.value})`).join('；')}。技术依赖不自动等于业务因果。` : '尚未建立可评估的当前适用关系，不能推断没有影响。', citations: ownCitations });
      } else if (intent === 'DIFFERENCE') {
        const groups = relations.filter(a => ['affects', 'appliesTo', 'references', 'dependsOn'].includes(a.predicate));
        compared.push(...groups.filter(a => a.applicability?.status !== 'applicable'));
        conclusions.push({ knowledgeId: entity.id, title: entity.name, status: groups.length ? 'compared' : 'insufficient_evidence', text: groups.length ? `当前适用 ${groups.filter(a => a.applicability?.status === 'applicable').length} 条；不适用 ${groups.filter(a => a.applicability?.status === 'not_applicable').length} 条；未知 ${groups.filter(a => a.applicability?.status === 'unknown').length} 条。基线为 ${ctx.baselineId}，环境为 ${ctx.applicableContext.environment || '未指定'}。` : '没有可比较的版本、环境或配置关系。', citations: ownCitations });
      } else {
        const steps = applicable.filter(a => ['triggers', 'implements', 'contains', 'reads', 'writes', 'produces', 'transitions', 'uses', 'exposes', 'hosts'].includes(a.predicate));
        conclusions.push({ knowledgeId: entity.id, title: entity.name, status: steps.length ? 'explained' : 'partial', text: `${entity.description || entity.name}${steps.length ? ` 已建模的运行/组成关系：${steps.map(a => `${a.predicate} ${a.subject} → ${a.object || a.value}`).join('；')}` : ' 当前没有更多已确认运行关系。'}`, citations: ownCitations });
      }
      if (unknown.length) limitations.add(`${entity.name} 有 ${unknown.length} 条适用性未知关系，未作为当前结论。`);
      if (conflicts.length) limitations.add(`${entity.name} 有 ${conflicts.length} 条冲突关系，需要人工确认。`);
      (project.unknowns || []).filter(item => item.status !== 'resolved' && item.relatedIds?.includes(entity.id)).forEach(item => limitations.add(item.title));
    });
    (project.profile?.outScope || []).forEach(item => limitations.add(`范围边界：${item}`));
    if (project.meta.prototypeOnly) limitations.add('该项目资料描述前端原型，不代表生产部署、企业认证、真实 MCP/LLM 或自动逆向能力。');
    const uniqueCitations = [...new Map(citations.map(item => [JSON.stringify(item), item])).values()];
    const answer = { answerId: `ANS-${project.meta.id}-${String((project.questionAnswers?.length || 0) + 1).padStart(5, '0')}`, projectId: project.meta.id, query, intent, role, conversationId: input.conversationId || `CONV-${C.now().replace(/\D/g, '').slice(0, 14)}-${String((project.questionAnswers?.length || 0) + 1)}`, followUpOf: input.followUpOf || null, readContext: clone(ctx), focusIds: focus.map(entity => entity.id), conclusions, citations: uniqueCitations, limitations: [...limitations], compared, status: conclusions.some(item => item.status === 'insufficient_evidence') ? 'partial' : 'answered', createdAt: C.now(), deterministic: true, note: '由对象检索、适用性判断和确定性知识查询组合；未调用大语言模型。' };
    project.questionAnswers.push(answer); Access.touch(project, 'catalog.changed', { answerId: answer.answerId });
    return { ok: true, answer };
  }
  function answerText(answer, format = 'internal') {
    if (!answer) return '';
    const lines = answer.conclusions.map(item => `${item.title}：${item.text}`);
    if (format === 'customer') return `${lines.join('\n')}${answer.limitations.length ? `\n使用边界：${answer.limitations.filter(item => !/内部|受限/.test(item)).join('；')}` : ''}`;
    return `${lines.join('\n')}\n依据：${answer.citations.length} 项 · 基线 ${answer.readContext.baselineId}${answer.limitations.length ? `\n待确认与限制：${answer.limitations.join('；')}` : ''}`;
  }
  function createFeedback(project, input = {}) {
    W.assert(input.context, 'issue:write');
    if (!escText(input.title) || !escText(input.description)) throw new Error('反馈标题与描述不能为空');
    const categories = ['product-experience', 'business-correction', 'technical-correction', 'knowledge-gap', 'system-defect', 'agent-issue', 'capability-request'];
    if (!categories.includes(input.category)) throw new Error('请选择有效反馈类型');
    const feedback = { feedbackId: input.feedbackId || `FB-${project.meta.id}-${String((project.feedbackRecords?.length || 0) + 1).padStart(4, '0')}`, projectId: project.meta.id, reporter: input.reporter || '当前用户', reporterRole: input.reporterRole || 'reader', category: input.category, title: input.title.trim(), description: input.description.trim(), originContext: clone(input.originContext || {}), relatedKnowledgeIds: clone(input.relatedKnowledgeIds || []), relatedTaskIds: clone(input.relatedTaskIds || []), evidenceRefs: clone(input.evidenceRefs || []), severity: input.severity || 'normal', priority: input.priority || 'normal', status: 'triage', createdAt: C.now(), updatedAt: C.now(), owner: input.owner || '待分流', resolution: null, history: [{ at: C.now(), by: input.reporter || '当前用户', event: 'submitted' }] };
    const duplicates = (project.feedbackRecords || []).filter(item => item.feedbackId !== feedback.feedbackId && item.status !== 'closed' && (item.title === feedback.title || item.relatedKnowledgeIds.some(id => feedback.relatedKnowledgeIds.includes(id)))).map(item => item.feedbackId);
    feedback.possibleDuplicates = duplicates; project.feedbackRecords.unshift(feedback); Access.touch(project, 'feedback.changed', { feedbackId: feedback.feedbackId }); return { ok: true, feedback };
  }
  function triageFeedback(project, feedbackId, input = {}) {
    W.assert(input.context, 'issue:write');
    const feedback = project.feedbackRecords.find(item => item.feedbackId === feedbackId); if (!feedback) throw new Error('反馈不存在');
    feedback.status = input.duplicateOf ? 'duplicate' : 'analyzing'; feedback.owner = input.owner || '知识负责人'; feedback.possibleDuplicates = [...new Set([...(feedback.possibleDuplicates || []), ...(input.duplicateOf ? [input.duplicateOf] : [])])]; feedback.duplicateOf = input.duplicateOf || null; feedback.triageDecision = input.decision || ''; feedback.updatedAt = C.now(); feedback.history.push({ at: C.now(), by: input.by || feedback.owner, event: 'triaged', decision: feedback.triageDecision }); Access.touch(project, 'feedback.changed', { feedbackId }); return { ok: true, feedback };
  }
  function feedbackToWork(project, feedbackId, input = {}) {
    W.assert(input.context, 'issue:write');
    const feedback = project.feedbackRecords.find(item => item.feedbackId === feedbackId); if (!feedback) throw new Error('反馈不存在');
    const rootId = input.rootId || feedback.relatedKnowledgeIds[0] || project.meta.id;
    if (input.kind === 'task') {
      const task = C.createTask(project, { rootId, title: input.title || `处理反馈：${feedback.title}`, purpose: input.purpose || 'development', by: input.by }, input.context);
      feedback.relatedTaskIds = [...new Set([...feedback.relatedTaskIds, task.id])]; feedback.workItem = { kind: 'task', id: task.id };
    } else {
      const issue = C.createIssue(project, { objectId: rootId, title: feedback.title, description: feedback.description, impact: input.impact || '需要补充或纠正知识', assignee: input.owner || feedback.owner, sourceRefs: feedback.evidenceRefs, context: input.context }).issue;
      const ws = C.workspace(project); ws.reason = `处理反馈 ${feedback.feedbackId}：${feedback.title}`; ws.title = input.title || `知识补充：${feedback.title}`; ws.issueIds = [...new Set([...ws.issueIds, issue.id])]; issue.changeSetId = ws.id;
      feedback.workItem = { kind: 'change-set', id: ws.id, issueId: issue.id };
    }
    feedback.status = 'in-progress'; feedback.updatedAt = C.now(); feedback.history.push({ at: C.now(), by: input.by || '知识负责人', event: 'work-created', workItem: clone(feedback.workItem) }); Access.touch(project, 'feedback.changed', { feedbackId }); return { ok: true, feedback, workItem: feedback.workItem };
  }
  function resolveFeedback(project, feedbackId, input = {}) {
    W.assert(input.context, 'decision:approve');
    const feedback = project.feedbackRecords.find(item => item.feedbackId === feedbackId); if (!feedback) throw new Error('反馈不存在');
    const work = feedback.workItem;
    if (work?.kind === 'change-set') {
      const change = project.changeSets.find(item => item.id === work.id); if (change?.status !== 'published') return { ok: false, errors: ['关联 Change Set 尚未审核发布，反馈不能关闭'] };
      feedback.resolvedBaselineId = change.publishedBaselineId || project.versionLayers.baseline;
      if (work.issueId) {
        const issue = project.issues.find(item => item.id === work.issueId);
        if (issue) { issue.status = 'resolved'; issue.resolution = input.resolution || `关联变更 ${change.id} 已发布并完成反馈验收`; issue.resolvedAt = C.now(); issue.resolvedBaselineId = feedback.resolvedBaselineId; issue.replies ||= []; issue.replies.push({ by: input.by || '验收人', text: issue.resolution, at: C.now() }); }
      }
    } else if (work?.kind === 'task') {
      const task = project.tasks.find(item => item.id === work.id); if (task?.status !== 'completed') return { ok: false, errors: ['关联任务尚未验收完成'] };
    } else if (!input.allowWithoutWork) return { ok: false, errors: ['请先创建知识问题、Change Set 或产品任务'] };
    feedback.status = 'closed'; feedback.resolution = input.resolution || '关联工作已完成并验证'; feedback.updatedAt = C.now(); feedback.closedAt = C.now(); feedback.history.push({ at: C.now(), by: input.by || '验收人', event: 'closed', resolution: feedback.resolution }); Access.touch(project, 'feedback.changed', { feedbackId }); return { ok: true, feedback };
  }
  function parseAgentRun(input, project) {
    let records;
    try { records = typeof input === 'string' ? (input.trim().split(/\r?\n/).filter(line => line.trim()).length > 1 ? input.split(/\r?\n/).filter(line => line.trim()).map(line => JSON.parse(line)) : [JSON.parse(input)]) : [clone(input)]; }
    catch (error) { return { ok: false, errors: [`Agent Run JSON/JSONL 无法解析：${error.message}`] }; }
    const run = records.length === 1 && records[0].runId ? records[0] : { ...records[0], executionEvents: [...(records[0]?.executionEvents || []), ...records.slice(1)] };
    if (!run.runId || !run.taskId || !run.agentIdentity || !Array.isArray(run.executionEvents)) return { ok: false, errors: ['Agent Run 需要 runId、taskId、agentIdentity 和 executionEvents'] };
    if (JSON.stringify(run).length > 4 * 1024 * 1024) return { ok: false, errors: ['执行记录超过 4 MiB 原型上限，请拆分或导出附件'] };
    if (/(?:<script|javascript:|onerror\s*=)/i.test(JSON.stringify(run))) return { ok: false, errors: ['执行记录包含危险活动内容，已拒绝导入'] };
    const events = run.executionEvents.map((event, index) => ({ eventId: event.eventId || `${run.runId}-EV-${index + 1}`, at: event.at || run.startTime || C.now(), type: event.type || 'observation', recordType: event.recordType || 'reconstructed-analysis-record', content: Access.redact(event.content || event.summary || ''), sourceRefs: clone(event.sourceRefs || []), toolCallId: event.toolCallId || null, confidence: event.confidence || null }));
    const toolCalls = (run.toolCalls || events.filter(event => event.type === 'tool-call')).map((call, index) => ({ toolCallId: call.toolCallId || `${run.runId}-TC-${index + 1}`, recordType: call.recordType || 'reconstructed-analysis-record', rawCaptured: call.rawCaptured === true, provider: call.provider || 'external', server: call.server || null, toolName: call.toolName || 'unknown', startedAt: call.startedAt || call.at || null, completedAt: call.completedAt || null, status: call.status || 'completed', inputSummary: Access.redact(call.inputSummary || ''), outputSummary: Access.redact(call.outputSummary || call.content || ''), referencedResources: clone(call.referencedResources || call.sourceRefs || []), error: Access.redact(call.error || ''), sourceAnchors: clone(call.sourceAnchors || []) }));
    const normalized = { runId: run.runId, projectId: project.meta.id, taskId: run.taskId, agentIdentity: run.agentIdentity, model: run.model || null, provider: run.provider || null, startTime: run.startTime || events[0]?.at || C.now(), endTime: run.endTime || null, status: run.status || 'completed', userInstructions: Access.redact(run.userInstructions || ''), executionEvents: events, toolCalls, sourceRefs: clone(run.sourceRefs || []), proposedKnowledge: clone(run.proposedKnowledge || []), unresolvedQuestions: clone(run.unresolvedQuestions || []), outcome: Access.redact(run.outcome || ''), reviewStatus: run.reviewStatus || 'pending', simulated: run.simulated === true, evidenceClassification: run.evidenceClassification || (run.rawCaptured ? 'raw-captured-event' : 'reconstructed-analysis-record'), rawCaptured: run.rawCaptured === true, verification: clone(run.verification || null), importProposalId: run.importProposalId || null, createdAt: C.now() };
    return { ok: true, run: normalized };
  }
  function importAgentRun(project, input, context) {
    W.assert(context, 'issue:write');
    const parsed = parseAgentRun(input, project); if (!parsed.ok) return parsed;
    const existing = project.agentRuns.find(run => run.runId === parsed.run.runId);
    if (existing) return { ok: true, run: existing, idempotent: true };
    project.agentRuns.unshift(parsed.run); Access.touch(project, 'agent.run.changed', { runId: parsed.run.runId }); return { ok: true, run: parsed.run };
  }
  function agentQuestionToIssue(project, runId, question, input = {}) {
    W.assert(input.context, 'issue:write');
    const run = project.agentRuns.find(item => item.runId === runId); if (!run) throw new Error('Agent Run 不存在');
    const rootId = input.rootId || run.proposedKnowledge?.[0]?.subject || project.meta.id;
    const issue = C.createIssue(project, { taskId: run.taskId, objectId: rootId, title: question.title || question, impact: question.impact || 'Agent 执行无法独立确认', assignee: input.assignee || '业务负责人', sourceRefs: run.sourceRefs, context: input.context }).issue;
    run.reviewStatus = 'requires-human-review'; Access.touch(project, 'agent.run.changed', { runId, issueId: issue.id }); return { ok: true, issue, run };
  }
  return { INTENTS, inferIntent, ask, answerText, capabilityStatus, createFeedback, triageFeedback, feedbackToWork, resolveFeedback, parseAgentRun, importAgentRun, agentQuestionToIssue };
});
