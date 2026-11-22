import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useLocation } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import type { AssistantMessage, ConversationPage, Project } from '../../shared/api';
import { useSession } from '../../shared/session';
import { EmptyState, ErrorState, LoadingState } from '../../shared/states';

const factLabels: Record<string, string> = { normative: '规范结论', observed: '实际观察', documented_intent: '记录意图', design_decision: '设计决定', inferred: '推断', assumption: '待确认假设' };
const statusLabels: Record<string, string> = { ok: '有据回答', partial: '部分回答', need_evidence: '证据不足', conflict: '存在冲突', forbidden: '当前不可读', stale: '上下文已变化', failed: '生成失败' };
const evidenceLabels: Record<string, string> = { rawCaptured: '工具原始记录', verifiedScript: '脚本核验记录', reconstructed: '重建摘要', proposed: '待审提案', humanAttestation: '人工确认' };
const supportLabels: Record<string, string> = { supports: '支持', refutes: '反驳', contextual: '背景' };
const changeLabels: Record<string, string> = { add: '新增', revise: '修订', retire: '停用', preserve: '保持', revalidate: '重新核验' };

type Props = { project: Project; open: boolean; onClose: () => void };
type ActiveJob = { jobId: string; conversationVersion: number };

export default function AssistantPanel({ project, open, onClose }: Props) {
  const session = useSession();
  const location = useLocation();
  const queryClient = useQueryClient();
  const [activeId, setActiveId] = useState<string>();
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [activeJob, setActiveJob] = useState<ActiveJob>();
  const [streamNonce, setStreamNonce] = useState(0);
  const [streaming, setStreaming] = useState(false);
  const [streamDisconnected, setStreamDisconnected] = useState(false);
  const [runError, setRunError] = useState<Error>();
  const [feedbackFor, setFeedbackFor] = useState<AssistantMessage>();
  const [feedback, setFeedback] = useState('');
  const [feedbackSaved, setFeedbackSaved] = useState(false);
  const [saving, setSaving] = useState(false);
  const eventCursor = useRef<Record<string, string>>({});
  const panelRef = useRef<HTMLElement>(null);
  const messagesRef = useRef<HTMLDivElement>(null);
  const returnFocusRef = useRef<HTMLElement | undefined>(undefined);
  const draft = activeId ? drafts[activeId] ?? '' : '';
  const conversationQueryKey = [session.principalId, project.projectId, project.policyEpoch, 'conversations'];
  const conversations = useQuery({ queryKey: conversationQueryKey, queryFn: () => atlasApi.conversations(project.projectId), enabled: open });
  const items = conversations.data?.items ?? [];
  const selected = items.find(item => item.conversationId === activeId);
  const messages = useQuery({
    queryKey: [session.principalId, project.projectId, project.policyEpoch, 'messages', activeId],
    queryFn: () => atlasApi.messages(project.projectId, activeId!), enabled: open && Boolean(activeId),
  });
  const routeMatch = location.pathname.match(/\/knowledge\/([0-9a-f-]{36})$/i);
  const routeUnitId = routeMatch?.[1];
  const routeBaseline = new URLSearchParams(location.search).get('baseline') ?? project.currentBaselineId;
  const storageKey = `atlas.assistant.${session.principalId}.${project.projectId}`;

  useEffect(() => {
    if (!open || !conversations.data || activeId && items.some(item => item.conversationId === activeId)) return;
    let remembered: string | null = null;
    try { remembered = localStorage.getItem(storageKey); } catch { /* Storage may be disabled; server history remains available. */ }
    setActiveId(items.find(item => item.conversationId === remembered)?.conversationId ?? items[0]?.conversationId);
  }, [open, conversations.data, activeId, storageKey]);

  useEffect(() => {
    if (!open || !selected) return;
    try { localStorage.setItem(storageKey, selected.conversationId); } catch { /* Non-essential preference only. */ }
    setActiveJob(undefined); setRunError(undefined); setStreaming(false); setStreamDisconnected(false); setFeedbackFor(undefined); setFeedbackSaved(false);
  }, [selected?.conversationId]);

  useEffect(() => {
    if (open) {
      returnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : undefined;
      panelRef.current?.focus();
    } else {
      returnFocusRef.current?.focus();
      returnFocusRef.current = undefined;
    }
  }, [open]);

  useEffect(() => {
    if (!open || !selected) return;
    const frame = requestAnimationFrame(() => messagesRef.current?.scrollTo({ top: messagesRef.current.scrollHeight }));
    return () => cancelAnimationFrame(frame);
  }, [open, selected?.conversationId, messages.data?.items.length, activeJob?.jobId]);

  useEffect(() => {
    if (!open || !selected) return;
    const after = eventCursor.current[selected.conversationId] ?? '0';
    const source = new EventSource(`/api/v1/projects/${project.projectId}/conversations/${selected.conversationId}/events?after=${encodeURIComponent(after)}`, { withCredentials: true });
    const remember = (event: MessageEvent) => { if (event.lastEventId) eventCursor.current[selected.conversationId] = event.lastEventId; };
    const refresh = () => {
      void messages.refetch();
      void queryClient.invalidateQueries({ queryKey: conversationQueryKey });
    };
    source.addEventListener('message.accepted', event => {
      remember(event as MessageEvent);
      const payload = JSON.parse((event as MessageEvent).data) as { payload?: { jobId?: string } };
      if (payload.payload?.jobId) setActiveJob({ jobId: payload.payload.jobId, conversationVersion: selected.version });
    });
    source.addEventListener('run.started', event => { remember(event as MessageEvent); setRunError(undefined); setStreamDisconnected(false); setStreaming(true); });
    source.addEventListener('run.retrying', event => {
      remember(event as MessageEvent); setStreaming(false);
      const payload = JSON.parse((event as MessageEvent).data) as { payload?: { message?: string } };
      setRunError(new Error(`${payload.payload?.message ?? '模型依赖暂时不可用'} 正在自动重试；已保存的问题不会丢失。`));
    });
    source.addEventListener('message.delta', event => { remember(event as MessageEvent); setStreamDisconnected(false); setStreaming(true); });
    source.addEventListener('message.completed', event => {
      remember(event as MessageEvent); setStreaming(false); setActiveJob(undefined); setRunError(undefined); refresh(); source.close();
    });
    source.addEventListener('run.failed', event => {
      remember(event as MessageEvent); setStreaming(false); setActiveJob(undefined);
      const payload = JSON.parse((event as MessageEvent).data) as { payload?: { message?: string } };
      setRunError(new Error(payload.payload?.message ?? '模型调用失败；可以修正问题后重试。')); refresh(); source.close();
    });
    source.addEventListener('conversation.cancel_requested', event => { remember(event as MessageEvent); setStreaming(false); setActiveJob(undefined); refresh(); source.close(); });
    source.onerror = () => { setStreaming(false); setStreamDisconnected(true); };
    return () => source.close();
  }, [open, selected?.conversationId, project.policyEpoch, streamNonce]);

  const selectConversation = (id: string) => { setActiveId(id); setRunError(undefined); setFeedbackFor(undefined); };
  const updateDraft = (value: string) => { if (activeId) setDrafts(current => ({ ...current, [activeId]: value })); };
  const createConversation = async () => {
    setSaving(true); setRunError(undefined);
    try {
      const created = await atlasApi.createConversation(project.projectId, routeUnitId, routeBaseline, routeBaseline !== project.currentBaselineId, crypto.randomUUID());
      queryClient.setQueryData<ConversationPage>(conversationQueryKey, old => ({ items: [created, ...(old?.items ?? [])], nextCursor: old?.nextCursor ?? null }));
      setActiveId(created.conversationId); setStreamNonce(value => value + 1);
    } catch (error) { setRunError(error as Error); } finally { setSaving(false); }
  };
  const send = async (event: FormEvent) => {
    event.preventDefault();
    if (!selected || !draft.trim() || activeJob) return;
    setSaving(true); setRunError(undefined);
    try {
      const job = await atlasApi.sendMessage(project.projectId, selected, project.currentBaselineId, draft.trim(), crypto.randomUUID());
      queryClient.setQueryData<ConversationPage>(conversationQueryKey, old => old && ({ ...old, items: old.items.map(item => item.conversationId === selected.conversationId ? { ...item, version: item.version + 1 } : item) }));
      setActiveJob({ ...job, conversationVersion: selected.version + 1 }); updateDraft(''); setStreamDisconnected(false); setStreaming(true);
      await messages.refetch(); setStreamNonce(value => value + 1);
    } catch (error) { setRunError(error as Error); await conversations.refetch(); } finally { setSaving(false); }
  };
  const cancel = async () => {
    if (!selected || !activeJob) return;
    setSaving(true);
    try {
      await atlasApi.cancelConversation(project.projectId, selected.conversationId, activeJob.jobId, activeJob.conversationVersion, crypto.randomUUID());
      setActiveJob(undefined); setStreaming(false); await conversations.refetch();
    } catch (error) { setRunError(error as Error); } finally { setSaving(false); }
  };
  const submitFeedback = async (event: FormEvent) => {
    event.preventDefault();
    if (!feedbackFor || !selected || !feedback.trim()) return;
    setSaving(true); setRunError(undefined);
    const reference = feedbackFor.answer?.claims.flatMap(claim => claim.references)[0];
    try {
      await atlasApi.createFeedback(project.projectId, {
        unitId: reference?.unitId ?? selected.rootUnitId,
        revisionId: reference?.revisionId,
        anchor: { conversationId: selected.conversationId, messageId: feedbackFor.messageId, answerStatus: feedbackFor.answer?.status },
        body: feedback.trim(),
      }, crypto.randomUUID());
      setFeedback(''); setFeedbackFor(undefined); setFeedbackSaved(true);
    } catch (error) { setRunError(error as Error); } finally { setSaving(false); }
  };

  if (!open) return null;
  const returnTo = `${location.pathname}${location.search}`;
  return <section ref={panelRef} tabIndex={-1} id="project-assistant" className="assistant-panel" role="dialog" aria-modal="false" aria-labelledby="assistant-title">
    <header className="assistant-head"><div><p className="eyebrow">项目知识助手</p><h2 id="assistant-title">项目助手</h2><p>{selected?.rootUnitId ? '绑定当前知识上下文' : '当前项目上下文'} · 使用已发布知识版本</p></div><button className="icon-button" aria-label="关闭项目助手" onClick={onClose}>×</button></header>
    <div className="assistant-toolbar">
      <label>历史会话<select aria-label="选择历史会话" value={selected?.conversationId ?? ''} onChange={event => selectConversation(event.target.value)}><option value="">选择会话</option>{items.map((item, index) => <option key={item.conversationId} value={item.conversationId}>对话 {items.length - index}{item.rootUnitId === routeUnitId ? ' · 当前知识' : ''}</option>)}</select></label>
      <button className="secondary" disabled={saving} onClick={createConversation}>基于当前位置新建</button>
    </div>
    <div className="assistant-context-note">仅使用当前账号有权读取的已批准内容。推断会明确标注；无证据时不作肯定结论。</div>
    {conversations.isPending ? <LoadingState label="正在恢复有权会话" /> : conversations.isError ? <ErrorState error={conversations.error} onRetry={() => conversations.refetch()} /> : !selected ? <EmptyState title="从当前位置开始提问">新建会话后，提问、引用和追问会持久保存，重开后仍需重新授权。</EmptyState> : <>
      <div ref={messagesRef} className="assistant-messages" aria-live="polite">
        {messages.isPending ? <LoadingState label="正在重新授权并读取历史" /> : messages.isError ? <ErrorState error={messages.error} onRetry={() => messages.refetch()} /> : messages.data?.items.length === 0 ? <p className="assistant-empty">询问当前功能、规则、设计或实际观察。无需输入内部 ID。</p> : messages.data?.items.map(message => <article className={`assistant-message ${message.kind}`} key={message.messageId}>
          <div className="message-meta"><strong>{message.kind === 'user' ? '你' : message.kind === 'system_note' ? '运行说明' : '项目助手'}</strong>{message.answer && <span className={`answer-status ${message.answer.status}`}>{statusLabels[message.answer.status]}</span>}</div>
          {message.kind === 'assistant' && message.answer ? <div className="answer-body">
            {message.answer.claims.map((claim, index) => <div className="answer-claim" key={`${message.messageId}-${index}`}><span className="fact-badge">{factLabels[claim.factKind] ?? claim.factKind}</span><p>{claim.text}</p>{claim.unknownReason && <small>{claim.unknownReason}</small>}{claim.references.length > 0 && <div className="answer-citations">{claim.references.map((reference, referenceIndex) => <Link key={`${reference.unitId}-${reference.revisionId}-${referenceIndex}`} to={`/p/${project.projectId}/knowledge/${reference.unitId}?baseline=${selected.baselineId}&from=${encodeURIComponent(returnTo)}`}>打开出处 {claim.references.length > 1 ? referenceIndex + 1 : ''} 与适用范围 →</Link>)}</div>}{claim.evidence.length > 0 && <details className="answer-evidence"><summary>查看 {claim.evidence.length} 条证据及记录性质</summary><ul>{claim.evidence.map((evidence, evidenceIndex) => <li key={`${evidence.source.revisionId}-${evidenceIndex}`}><strong>{evidenceLabels[evidence.captureKind] ?? evidence.captureKind}</strong><span>{supportLabels[evidence.support] ?? evidence.support}此结论</span>{evidence.locator.description && <p>{evidence.locator.description}</p>}{evidence.locator.quote && <blockquote>{evidence.locator.quote}</blockquote>}</li>)}</ul></details>}</div>)}
            {message.answer.issues.map((issue, index) => <div className={`answer-unknown ${issue.severity}`} key={`${issue.criterion}-${index}`}><strong>{issue.explanation}</strong>{issue.suggestion && <p>处理建议：{issue.suggestion}</p>}</div>)}
            {message.answer.unknowns.map(unknown => <div className={`answer-unknown ${unknown.severity}`} key={unknown.key}><strong>{unknown.description}</strong>{unknown.nextAction && <p>{unknown.nextAction}</p>}</div>)}
            {(message.answer.candidateChanges.length > 0 || message.answer.candidateRevisions.length > 0) && <section className="answer-proposals" aria-label="待审知识建议"><strong>知识变更建议 · 尚未提交或批准</strong>{message.answer.candidateChanges.map((change, index) => <div key={`${change.action}-${index}`}><span>{changeLabels[change.action] ?? change.action}</span><p>{change.description}</p><small>原因：{change.reason}</small></div>)}{message.answer.candidateRevisions.length > 0 && <p>另有 {message.answer.candidateRevisions.length} 项候选修订，需通过任务与变更审核入口提交，不会由助手直接写入知识。</p>}<Link to={`/p/${project.projectId}/tasks`}>前往任务入口发起受控变更 →</Link></section>}
            {message.answer.nextActions.length > 0 && <ul className="answer-next">{message.answer.nextActions.map((action, index) => <li key={`${index}-${action}`}>{action}</li>)}</ul>}
            <button className="quiet feedback-link" onClick={() => { setFeedbackFor(message); setFeedback(''); setFeedbackSaved(false); }}>反馈此回答</button>
          </div> : <p>{message.text}</p>}
        </article>)}
        {activeJob && <div className="assistant-progress" role="status"><span className="spinner" />{streaming ? '正在接收模型生成进度；未经验证的内容不会显示为结论。' : '问题已受理，正在授权检索。'}<button className="quiet" disabled={saving} onClick={cancel}>取消</button></div>}
      </div>
      {streamDisconnected && <div className="notice warning" role="status">实时进度连接中断；这不代表运行失败，已保存的问题不会丢失。<button className="quiet" onClick={() => { setStreamDisconnected(false); setRunError(undefined); void messages.refetch(); setStreamNonce(value => value + 1); }}>重新连接并刷新</button></div>}
      {runError && <div className="notice error" role="alert">{runError.message}<button className="quiet" onClick={() => setRunError(undefined)}>关闭</button></div>}
      {feedbackSaved && <p className="notice">反馈已提交为待处理事项，不会直接修改已批准知识。</p>}
      {feedbackFor && <form className="assistant-feedback" onSubmit={submitFeedback}><label htmlFor="assistant-feedback">指出错误或需要补充的材料</label><textarea id="assistant-feedback" autoFocus rows={3} maxLength={8000} value={feedback} onChange={event => setFeedback(event.target.value)} placeholder="说明哪条结论、条件或引用有问题" /><div><button className="primary" disabled={saving || !feedback.trim()}>提交治理反馈</button><button type="button" className="quiet" onClick={() => setFeedbackFor(undefined)}>取消</button></div></form>}
      <form className="assistant-composer" onSubmit={send}><label htmlFor="assistant-question">基于当前会话追问</label><textarea id="assistant-question" rows={3} maxLength={32000} value={draft} onChange={event => updateDraft(event.target.value)} placeholder="例如：这条规则适用于什么情况？哪些部分尚无证据？" /><button className="primary" disabled={saving || Boolean(activeJob) || !draft.trim()}>{saving && !activeJob ? '正在提交…' : '发送问题'}</button></form>
    </>}
  </section>;
}
