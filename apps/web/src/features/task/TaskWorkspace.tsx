import { useState } from 'react';
import type { FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useOutletContext, useParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import type { Activity, Impact } from '../../shared/api';
import { useSession } from '../../shared/session';
import { EmptyState, ErrorState, LoadingState } from '../../shared/states';
import type { ProjectOutletContext } from '../project/ProjectShell';
import { activityStateLabels, impactStateLabels, shortId, taskStateLabels } from './labels';

const revisionLink = (projectId: string, unitId: string) => `/p/${projectId}/knowledge/${unitId}`;

export default function TaskWorkspace() {
  const { project } = useOutletContext<ProjectOutletContext>();
  const { projectId = '', taskId } = useParams();
  const session = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [intent, setIntent] = useState('');
  const [issueText, setIssueText] = useState('');
  const tasks = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'tasks'], queryFn: () => atlasApi.tasks(projectId) });
  const issues = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'issues'], queryFn: () => atlasApi.issues(projectId) });
  const feedback = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'feedback'], queryFn: () => atlasApi.feedback(projectId) });
  const create = useMutation({
    mutationFn: () => atlasApi.createTask(projectId, intent.trim(), project.currentBaselineId, crypto.randomUUID()),
    onSuccess: task => { setIntent(''); queryClient.invalidateQueries({ queryKey: [session.principalId, projectId, project.policyEpoch, 'tasks'] }); navigate(`/p/${projectId}/tasks/${task.taskId}`); },
  });
  const createIssue = useMutation({ mutationFn: () => atlasApi.createIssue(projectId, { body: issueText.trim() }, crypto.randomUUID()), onSuccess: () => { setIssueText(''); issues.refetch(); } });
  const submit = (event: FormEvent) => { event.preventDefault(); if (intent.trim()) create.mutate(); };

  return <div className="task-workspace">
    <div className="page-heading"><p className="eyebrow">任务、问题与真实调查</p><h1>从真实意图开始</h1><p>只需说明希望改变什么。目标、保持项和未知会随调查逐步明确；平台不会把空泛描述伪装成完整规范，也不会默认生成十五类调查表。</p></div>
    <form className="task-create" onSubmit={submit}><label htmlFor="task-intent">你想解决什么问题或改变什么？</label><textarea id="task-intent" value={intent} onChange={event => setIntent(event.target.value)} placeholder="描述真实业务意图、遇到的问题或期望结果" rows={3} /><div><span>可稍后在任务中补充关联知识与证据。</span><button className="primary" disabled={!intent.trim() || create.isPending}>{create.isPending ? '正在提出…' : '提出任务'}</button></div>{create.isError && <ErrorState error={create.error} />}</form>
    <div className="task-layout">
      <aside className="task-queue" aria-label="任务列表"><div className="section-head"><h2>任务</h2><button className="quiet" onClick={() => tasks.refetch()}>刷新</button></div>
        {tasks.isPending ? <LoadingState label="正在读取任务" /> : tasks.isError ? <ErrorState error={tasks.error} onRetry={() => tasks.refetch()} /> : tasks.data.items.length === 0 ? <EmptyState title="还没有任务">在上方写下真实意图即可开始。</EmptyState> : <div className="task-list">{tasks.data.items.map(task => <Link key={task.taskId} className={taskId === task.taskId ? 'selected' : ''} to={`/p/${projectId}/tasks/${task.taskId}`}><span className={`task-state ${task.state}`}>{taskStateLabels[task.state]}</span><strong>{task.title || task.intent}</strong><small>{task.nextAction || '等待下一步调查'}</small></Link>)}</div>}
      </aside>
      <section className="task-detail">{taskId ? <TaskDetail key={taskId} projectId={projectId} taskId={taskId} policyEpoch={project.policyEpoch} /> : <EmptyState title="选择任务查看进展">可阅读目标、保持项、已确认结论、需要人决策的未知以及真实交付状态。</EmptyState>}</section>
    </div>
    <section className="collaboration-inbox"><div className="section-head"><div><p className="eyebrow">反馈分流与回链</p><h2>问题与反馈</h2></div></div><form className="issue-create" onSubmit={(event: FormEvent) => { event.preventDefault(); if (issueText.trim()) createIssue.mutate(); }}><label htmlFor="new-issue">提出一个需要跟踪的问题</label><div><input id="new-issue" value={issueText} onChange={event => setIssueText(event.target.value)} placeholder="描述问题，可随后分流到任务或知识变更" /><button className="secondary" disabled={!issueText.trim() || createIssue.isPending}>提交问题</button></div>{createIssue.isError && <ErrorState error={createIssue.error} />}</form><div className="collaboration-columns"><ActivityList title="问题" items={issues.data?.items} error={issues.error} pending={issues.isPending} onResolve={(item, reason, linkedTaskId) => atlasApi.resolveIssue(projectId, item.id, item.version, reason, linkedTaskId || undefined, crypto.randomUUID())} onChanged={() => { issues.refetch(); tasks.refetch(); }} /><ActivityList title="反馈" items={feedback.data?.items} error={feedback.error} pending={feedback.isPending} onResolve={(item, reason, linkedTaskId) => atlasApi.resolveFeedback(projectId, item.id, item.version, reason, linkedTaskId || undefined, item.target.changeSetId, crypto.randomUUID())} onChanged={() => { feedback.refetch(); tasks.refetch(); }} /></div></section>
  </div>;
}

function TaskDetail({ projectId, taskId, policyEpoch }: { projectId: string; taskId: string; policyEpoch: number }) {
  const session = useSession();
  const queryClient = useQueryClient();
  const [comment, setComment] = useState('');
  const [transitionReason, setTransitionReason] = useState('');
  const [decisions, setDecisions] = useState<Record<string, { disposition: Impact['disposition']; reason: string }>>({});
  const task = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'task', taskId], queryFn: () => atlasApi.task(projectId, taskId) });
  const impacts = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'impacts', taskId], queryFn: () => atlasApi.impacts(projectId, taskId) });
  const runs = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'runs', taskId], queryFn: () => atlasApi.agentRuns(projectId, taskId) });
  const comments = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'task-comments', taskId], queryFn: () => atlasApi.taskComments(projectId, taskId) });
  const context = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'context', task.data?.contextId], queryFn: () => atlasApi.taskContext(projectId, task.data!.contextId!), enabled: Boolean(task.data?.contextId) });
  const refresh = () => { queryClient.invalidateQueries({ queryKey: [session.principalId, projectId, policyEpoch] }); };
  const addComment = useMutation({ mutationFn: () => atlasApi.createComment(projectId, { taskId, body: comment.trim() }, crypto.randomUUID()), onSuccess: () => { setComment(''); comments.refetch(); } });
  const transition = useMutation({ mutationFn: (event: 'begin_investigation' | 'request_decision' | 'begin_implementation' | 'submit_verification' | 'request_cancel' | 'resume') => atlasApi.transitionTask(projectId, taskId, task.data!.version, event, transitionReason.trim(), task.data!.contextId, crypto.randomUUID()), onSuccess: () => { setTransitionReason(''); refresh(); } });
  const decide = useMutation({ mutationFn: (impact: Impact) => { const decision = decisions[impact.impactId!]!; return atlasApi.decideImpact(projectId, taskId, impact.impactId!, impact.version!, { ...impact, disposition: decision.disposition, reason: decision.reason.trim() }, crypto.randomUUID()); }, onSuccess: refresh });

  if (task.isPending) return <LoadingState label="正在读取任务与当前权限" />;
  if (task.isError) return <ErrorState error={task.error} onRetry={() => task.refetch()} />;
  const current = task.data;
  const nextEvent = current.state === 'captured' || current.state === 'blocked' ? 'begin_investigation' : current.state === 'needs_decision' ? 'resume' : current.state === 'ready' ? 'begin_implementation' : current.state === 'implementing' ? 'submit_verification' : undefined;
  return <>
    <header className="task-detail-head"><div><span className={`task-state ${current.state}`}>{taskStateLabels[current.state]}</span><h2>{current.title || current.intent}</h2></div><Link className="secondary link-button" to={`/p/${projectId}/delivery/${current.taskId}`}>核对交付与验收 →</Link></header>
    <p className="task-original"><strong>原始意图</strong>{current.intent}</p>
    <div className="task-summary-grid">
      <section><p className="eyebrow">想改变什么</p><h3>目标与保持</h3>{current.targetChanges.length ? current.targetChanges.map((change, index) => <article className="summary-item" key={index}><strong>{change.description}</strong><p>{change.reason}</p><small>{change.current ? <>现状：<Link to={revisionLink(projectId, change.current.unitId)}>查看原知识</Link></> : '现状尚未关联'} · {change.action === 'preserve' ? '保持' : '候选目标'}</small></article>) : <p className="unknown-copy">目标仍待调查确认；未自动生成业务规则。</p>}{current.preserve.length > 0 && <div className="preserve-list"><strong>明确保持</strong>{current.preserve.map(ref => <Link key={ref.revisionId} to={revisionLink(projectId, ref.unitId)}>查看需保持的知识 ↗</Link>)}</div>}</section>
      <section><p className="eyebrow">已确认结论</p><h3>调查与处置</h3>{impacts.isPending ? <LoadingState /> : impacts.isError ? <ErrorState error={impacts.error} /> : impacts.data.items.filter(item => item.inspectionState !== 'candidate').length === 0 ? <p className="unknown-copy">尚无实际调查结论。候选关系不等于已验证事实。</p> : impacts.data.items.filter(item => item.inspectionState !== 'candidate').map(item => <article className="summary-item" key={item.impactId}><strong>{item.reason}</strong><small>{impactStateLabels[item.disposition]} · {impactStateLabels[item.inspectionState]}</small></article>)}</section>
      <section className="attention"><p className="eyebrow">需要关注</p><h3>未知、冲突与风险</h3>{current.unknowns.length === 0 ? <p className="unknown-copy">当前没有登记的未知；仍需以调查证据核对。</p> : current.unknowns.map(unknown => <article className="summary-item" key={unknown.key}><strong>{unknown.description}</strong><p>{unknown.severity === 'blocking' ? '阻断' : '注意'} · {unknown.reason}</p>{unknown.nextAction && <small>建议：{unknown.nextAction}</small>}</article>)}</section>
      <section><p className="eyebrow">下一步与交付</p><h3>{current.nextAction || '等待调查或处理'}</h3><p>任务状态不会直接改写已批准知识。代码、知识、测试和部署需在交付页分别核验。</p>{context.data && <p className={`context-status ${context.data.status}`}>任务上下文：{context.data.status === 'valid' ? '有效' : context.data.status === 'stale' ? '已过期，需重新复核' : '信息不足'}</p>}</section>
    </div>
    <details className="task-disclosure" open={impacts.data?.items.some(item => item.closeState === 'open')}><summary>影响路径与实际处置 <span>{impacts.data?.items.length ?? 0} 项</span></summary>
      {impacts.data?.items.length ? <div className="impact-list">{impacts.data.items.map(impact => { const decision = decisions[impact.impactId!] ?? { disposition: impact.disposition, reason: impact.reason }; return <article key={impact.impactId}><header><div><strong>{impact.reason}</strong><p>{impact.target ? <Link to={revisionLink(projectId, impact.target.unitId)}>查看影响知识</Link> : '目标知识尚未关联或不可见'}</p></div><span>{impactStateLabels[impact.inspectionState]}</span></header>{impact.path.length > 0 && <p className="impact-path">关系路径：{impact.path.map(step => `${step.direction === 'forward' ? '正向' : '反向'} ${step.predicate}`).join(' → ')}</p>}<div className="impact-facts"><span>{impactStateLabels[impact.disposition]}</span><span>风险：{impact.risk}</span><span>{impact.evidence.length} 条证据</span></div>{impact.inspectionState === 'candidate' && <p className="inline-warning">仅为语义候选，未执行源码或实际行为调查。</p>}<div className="impact-decision"><label>处置<select value={decision.disposition} onChange={event => setDecisions(value => ({ ...value, [impact.impactId!]: { ...decision, disposition: event.target.value as Impact['disposition'] } }))}><option value="required">必须处理</option><option value="potential">仍需观察</option><option value="no_change">确认不变</option><option value="deferred">延后处理</option><option value="unknown">尚不确定</option></select></label><label>理由<input value={decision.reason} onChange={event => setDecisions(value => ({ ...value, [impact.impactId!]: { ...decision, reason: event.target.value } }))} /></label><button className="secondary" disabled={!decision.reason.trim() || decide.isPending} onClick={() => decide.mutate(impact)}>记录处置</button></div></article>; })}</div> : <EmptyState title="尚未发现或生成影响候选">没有真实关系依据时保持为空，不用空白类别代替调查。</EmptyState>}
    </details>
    <details className="task-disclosure"><summary>调查执行、任务上下文与评论</summary><div className="task-evidence-grid"><section><h3>真实 Agent 运行</h3>{runs.data?.items.length ? runs.data.items.map(run => <p key={run.runId}><strong>{run.state}</strong> · {run.provider || '执行器未报告来源'}<br /><small>{shortId(run.runId)} · 版本 {run.version}</small></p>) : <p className="muted">尚无真实执行记录；当前进展可能仍在等待执行器。</p>}<h3>任务上下文</h3>{context.data ? <><p>基线 {shortId(context.data.baselineId)} · 策略纪元 {context.data.policyEpoch}</p><p>{context.data.acceptanceCases.length} 个验收案例 · {context.data.knowledgeRequirements.length} 项知识要求</p></> : <p className="muted">尚未生成精确任务上下文。</p>}</section><section><h3>任务讨论</h3>{comments.data?.items.map(item => <blockquote key={item.id}>{item.body}<footer>{new Date(item.createdAt).toLocaleString('zh-CN')}</footer></blockquote>)}<form onSubmit={(event: FormEvent) => { event.preventDefault(); if (comment.trim()) addComment.mutate(); }}><label htmlFor="task-comment">补充意见或业务说明</label><textarea id="task-comment" rows={3} value={comment} onChange={event => setComment(event.target.value)} /><button className="secondary" disabled={!comment.trim() || addComment.isPending}>发表评论</button>{addComment.isError && <ErrorState error={addComment.error} />}</form></section></div></details>
    {nextEvent && <form className="task-next-action" onSubmit={(event: FormEvent) => { event.preventDefault(); if (transitionReason.trim()) transition.mutate(nextEvent); }}><label htmlFor="transition-reason">推进流程的说明</label><input id="transition-reason" value={transitionReason} onChange={event => setTransitionReason(event.target.value)} placeholder="说明已完成的决策或准备工作" /><button className="primary" disabled={!transitionReason.trim() || transition.isPending}>{nextEvent === 'begin_investigation' ? '开始实际调查' : nextEvent === 'resume' ? '继续处理' : nextEvent === 'begin_implementation' ? '开始实施' : '提交验证'}</button>{transition.isError && <ErrorState error={transition.error} />}</form>}
    {decide.isError && <ErrorState error={decide.error} />}
  </>;
}

function ActivityList({ title, items, pending, error, onResolve, onChanged }: { title: string; items?: Activity[]; pending: boolean; error: unknown; onResolve: (item: Activity, reason: string, taskId: string) => Promise<Activity>; onChanged: () => void }) {
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [links, setLinks] = useState<Record<string, string>>({});
  const resolve = useMutation({ mutationFn: (item: Activity) => onResolve(item, reasons[item.id]?.trim() ?? '', links[item.id]?.trim() ?? ''), onSuccess: onChanged });
  return <section><h3>{title}</h3>{pending ? <LoadingState /> : error ? <ErrorState error={error} /> : !items?.length ? <p className="muted">暂无记录。</p> : items.map(item => <article className="activity-record" key={item.id}><span>{activityStateLabels[item.state]}</span><p>{item.body}</p>{item.target.unitId && <Link to={revisionLink(useParams().projectId ?? '', item.target.unitId)}>回到原知识 ↗</Link>}{item.state !== 'closed' && <div className="activity-resolution"><label>处理结果<input value={reasons[item.id] ?? ''} onChange={event => setReasons(value => ({ ...value, [item.id]: event.target.value }))} placeholder="说明分流、处理或验证结果" /></label><label>关联任务 ID（可选）<input value={links[item.id] ?? item.target.taskId ?? ''} onChange={event => setLinks(value => ({ ...value, [item.id]: event.target.value }))} /></label><button className="secondary" disabled={!reasons[item.id]?.trim() || resolve.isPending} onClick={() => resolve.mutate(item)}>记录闭环</button></div>}</article>)}{resolve.isError && <ErrorState error={resolve.error} />}</section>;
}
