import { useState } from 'react';
import type { FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useOutletContext, useParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import type { AcceptanceInput } from '../../shared/api';
import { useSession } from '../../shared/session';
import { EmptyState, ErrorState, LoadingState } from '../../shared/states';
import type { ProjectOutletContext } from '../project/ProjectShell';
import { closureStateLabels, deliveryStateLabels, shortId, taskStateLabels } from '../task/labels';

export default function DeliveryWorkspace() {
  const { project } = useOutletContext<ProjectOutletContext>();
  const { projectId = '', taskId } = useParams();
  const session = useSession();
  const tasks = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'tasks'], queryFn: () => atlasApi.tasks(projectId) });
  const deliveries = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'deliveries'], queryFn: () => atlasApi.deliveries(projectId) });
  return <div className="delivery-workspace">
    <div className="page-heading"><p className="eyebrow">业务事实、证据与交付对账</p><h1>交付与验收</h1><p>目标与保持项、实际验证、影响处置、知识审批和部署版本分别核对。编译或任务状态不能代替真实业务证据。</p></div>
    <div className="task-layout"><aside className="task-queue"><div className="section-head"><h2>待核对任务</h2><button className="quiet" onClick={() => { tasks.refetch(); deliveries.refetch(); }}>刷新</button></div>{tasks.isPending ? <LoadingState /> : tasks.isError ? <ErrorState error={tasks.error} onRetry={() => tasks.refetch()} /> : !tasks.data.items.length ? <EmptyState title="没有可验收任务">先在任务与问题中提出真实意图。</EmptyState> : <div className="task-list">{tasks.data.items.map(task => <Link key={task.taskId} className={task.taskId === taskId ? 'selected' : ''} to={`/p/${projectId}/delivery/${task.taskId}`}><span className={`task-state ${task.state}`}>{taskStateLabels[task.state]}</span><strong>{task.title || task.intent}</strong><small>{task.nextAction || '查看验收阻断原因'}</small></Link>)}</div>}</aside>
      <section className="task-detail">{taskId ? <DeliveryDetail key={taskId} projectId={projectId} taskId={taskId} policyEpoch={project.policyEpoch} /> : <EmptyState title="选择任务核对实际交付">只有服务器重新核验全部必要条件后，才可提交最终验收。</EmptyState>}</section></div>
    {deliveries.isError && <ErrorState error={deliveries.error} onRetry={() => deliveries.refetch()} />}
  </div>;
}

function DeliveryDetail({ projectId, taskId, policyEpoch }: { projectId: string; taskId: string; policyEpoch: number }) {
  const session = useSession();
  const queryClient = useQueryClient();
  const task = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'task', taskId], queryFn: () => atlasApi.task(projectId, taskId) });
  const contextId = task.data?.contextId;
  const closure = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'closure', taskId, contextId], queryFn: () => atlasApi.closure(projectId, taskId, contextId!), enabled: Boolean(contextId) });
  const deliveries = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'deliveries'], queryFn: () => atlasApi.deliveries(projectId) });
  const context = useQuery({ queryKey: [session.principalId, projectId, policyEpoch, 'context', contextId], queryFn: () => atlasApi.taskContext(projectId, contextId!), enabled: Boolean(contextId) });
  const refresh = () => queryClient.invalidateQueries({ queryKey: [session.principalId, projectId, policyEpoch] });
  if (task.isPending) return <LoadingState label="正在读取目标与交付状态" />;
  if (task.isError) return <ErrorState error={task.error} onRetry={() => task.refetch()} />;
  const current = task.data;
  const ownDeliveries = deliveries.data?.items.filter(item => item.taskId === taskId) ?? [];
  return <>
    <header className="task-detail-head"><div><span className={`task-state ${current.state}`}>{taskStateLabels[current.state]}</span><h2>{current.title || current.intent}</h2></div><Link className="secondary link-button" to={`/p/${projectId}/tasks/${taskId}`}>返回任务调查 →</Link></header>
    <div className="delivery-goal"><section><h3>本次目标</h3><p>{current.intent}</p>{current.targetChanges.map((change, index) => <p key={index}><strong>{change.description}</strong><br /><small>{change.reason}</small></p>)}</section><section><h3>必须保持</h3>{current.preserve.length ? current.preserve.map(ref => <Link key={ref.revisionId} to={`/p/${projectId}/knowledge/${ref.unitId}`}>查看保持约束 ↗</Link>) : <p className="unknown-copy">尚未登记保持约束；不代表没有约束。</p>}</section></div>
    {!contextId ? <div className="closure-blocked"><strong>尚不能验收</strong><p>任务没有有效的版本化上下文，无法关联验收案例与知识依据。请先在任务调查中形成任务上下文。</p></div> : <>
      {context.data?.status !== 'valid' && context.data && <div className="notice error">任务上下文{context.data.status === 'stale' ? '已过期' : '信息不足'}，须重新复核，不能用旧合同完成验收。</div>}
      <section className="closure-panel"><div className="section-head"><div><p className="eyebrow">服务端完整门槛</p><h3>{closure.data ? closureStateLabels[closure.data.state] : '正在核查'}</h3></div><button className="quiet" onClick={() => closure.refetch()} disabled={closure.isFetching}>重新核查</button></div>{closure.isPending ? <LoadingState label="正在核对业务、影响、证据、审批和部署" /> : closure.isError ? <ErrorState error={closure.error} onRetry={() => closure.refetch()} /> : <div className="closure-checks">{closure.data.checks.map(check => <article className={check.status} key={check.key}><span>{check.status === 'pass' ? '通过' : check.status === 'not_applicable' ? '不适用' : check.status === 'unknown' ? '未知' : '阻断'}</span><div><strong>{check.key}</strong><p>{check.reason}</p>{check.nextAction && <small>下一步：{check.nextAction}</small>}{check.evidence.length > 0 && <details><summary>{check.evidence.length} 条版本锚证据</summary>{check.evidence.map((evidence, index) => <p key={index}>{evidence.locator.path || evidence.locator.uri || evidence.locator.description || '已授权来源'} · {evidence.captureKind}</p>)}</details>}</div></article>)}</div>}</section>
      <AcceptanceForm projectId={projectId} taskId={taskId} version={current.version} contextId={contextId} onRecorded={refresh} />
      <CloseTask projectId={projectId} taskId={taskId} version={current.version} contextId={contextId} enabled={closure.data?.state === 'ready_for_acceptance'} onClosed={refresh} />
    </>}
    <section className="delivery-records"><div className="section-head"><div><p className="eyebrow">独立交付轴</p><h3>代码、知识与部署对账</h3></div></div>{deliveries.isPending ? <LoadingState /> : ownDeliveries.length === 0 ? <EmptyState title="尚无交付记录">代码合并、知识审核、Manifest 与部署均不能由此界面伪造。</EmptyState> : ownDeliveries.map(delivery => <article key={delivery.deliveryId}><span className={`delivery-state ${delivery.status}`}>{deliveryStateLabels[delivery.status]}</span><dl><div><dt>代码提交</dt><dd>{delivery.commitSha}</dd></div><div><dt>构建摘要</dt><dd>{delivery.buildDigest}</dd></div><div><dt>知识成果</dt><dd>{delivery.changeSetId ? <Link to={`/p/${projectId}/review/${delivery.changeSetId}`}>查看审批与发布 ↗</Link> : '未关联'}</dd></div><div><dt>已发布基线</dt><dd>{delivery.publishedBaselineId ? shortId(delivery.publishedBaselineId) : '尚未发布'}</dd></div></dl>{delivery.unknowns.map(unknown => <p className="inline-warning" key={unknown.key}>{unknown.description} {unknown.nextAction && `下一步：${unknown.nextAction}`}</p>)}</article>)}</section>
  </>;
}

function AcceptanceForm({ projectId, taskId, version, contextId, onRecorded }: { projectId: string; taskId: string; version: number; contextId: string; onRecorded: () => void }) {
  const [testId, setTestId] = useState('');
  const [expected, setExpected] = useState('');
  const [observed, setObserved] = useState('');
  const [result, setResult] = useState<AcceptanceInput['result']>('pass');
  const [environment, setEnvironment] = useState('');
  const [buildDigest, setBuildDigest] = useState('');
  const [evidence, setEvidence] = useState('[]');
  const [evidenceError, setEvidenceError] = useState('');
  const record = useMutation({ mutationFn: (input: AcceptanceInput) => atlasApi.recordAcceptance(projectId, taskId, version, input, crypto.randomUUID()), onSuccess: () => { setTestId(''); setExpected(''); setObserved(''); setEvidence('[]'); onRecorded(); } });
  const submit = (event: FormEvent) => {
    event.preventDefault(); setEvidenceError('');
    try {
      const parsed = JSON.parse(evidence) as unknown;
      if (!Array.isArray(parsed)) throw new Error('证据必须是 EvidenceRef 数组。');
      if (result === 'pass' && parsed.length === 0) { setEvidenceError('通过结果必须至少关联一条真实来源、定位和采集方式明确的证据。'); return; }
      record.mutate({ contextId, testId: testId.trim(), expected: expected.trim(), observed: observed.trim(), result, environment: environment.trim() || undefined, buildDigest: buildDigest.trim() || undefined, evidence: parsed });
    } catch (error) { setEvidenceError(error instanceof Error ? error.message : '证据 JSON 无法解析。'); }
  };
  return <details className="acceptance-form"><summary>记录一条实际验证结果</summary><form onSubmit={submit}><p>仅记录已实际执行或由可信 CI 提交的结果。通过结果必须带真实证据；没有执行请选择“未执行”。</p><div className="acceptance-fields"><label>验收案例 / 测试标识<input required value={testId} onChange={event => setTestId(event.target.value)} /></label><label>结果<select value={result} onChange={event => setResult(event.target.value as AcceptanceInput['result'])}><option value="pass">通过</option><option value="fail">失败</option><option value="not_run">未执行</option><option value="unknown">结果未知</option></select></label><label>预期业务结果<textarea required rows={2} value={expected} onChange={event => setExpected(event.target.value)} /></label><label>实际观察<textarea rows={2} value={observed} onChange={event => setObserved(event.target.value)} /></label><label>环境<input value={environment} onChange={event => setEnvironment(event.target.value)} /></label><label>构建摘要<input value={buildDigest} onChange={event => setBuildDigest(event.target.value)} /></label></div><label>证据引用（EvidenceRef JSON 数组）<textarea className="evidence-json" rows={6} value={evidence} onChange={event => setEvidence(event.target.value)} spellCheck={false} /></label><small>每项须含 source.unitId、source.revisionId、locator、support 与 captureKind；来源会由服务端重新授权。</small>{evidenceError && <div className="notice error" role="alert">{evidenceError}</div>}{record.isError && <ErrorState error={record.error} />}<button className="primary" disabled={record.isPending || !testId.trim() || !expected.trim()}>{record.isPending ? '正在记录…' : '记录验证证据'}</button></form></details>;
}

function CloseTask({ projectId, taskId, version, contextId, enabled, onClosed }: { projectId: string; taskId: string; version: number; contextId: string; enabled: boolean; onClosed: () => void }) {
  const [reason, setReason] = useState('');
  const close = useMutation({ mutationFn: () => atlasApi.closeTask(projectId, taskId, version, contextId, reason.trim(), crypto.randomUUID()), onSuccess: onClosed });
  return <form className="final-acceptance" onSubmit={(event: FormEvent) => { event.preventDefault(); if (enabled && reason.trim()) close.mutate(); }}><div><h3>最终业务验收</h3><p>提交时服务器会再次核查所有关键调查、保持约束、验收证据、人工审批、知识发布与部署。此操作不会自动批准知识。</p></div><label htmlFor="acceptance-reason">验收结论与范围</label><textarea id="acceptance-reason" required rows={2} value={reason} onChange={event => setReason(event.target.value)} placeholder="说明实际验收范围与结论" /><button className="primary" disabled={!enabled || !reason.trim() || close.isPending} title={!enabled ? '存在验收阻断，请先补齐真实证据与审批' : undefined}>{close.isPending ? '正在重新核查…' : '提交最终验收'}</button>{!enabled && <small>当前存在阻断或尚未完成核查。</small>}{close.isError && <ErrorState error={close.error} />}</form>;
}
