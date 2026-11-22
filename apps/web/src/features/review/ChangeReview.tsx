import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { ApiError, atlasApi } from '../../shared/api';
import type { Schema } from '../../shared/api';
import { useSession } from '../../shared/session';
import { ErrorState, LoadingState } from '../../shared/states';
import { UnknownList } from '../knowledge/ReaderContent';
import DecisionDialog from './DecisionDialog';
import type { Decision } from './DecisionDialog';
import InlineComments from './InlineComments';
import RevisionCompare from './RevisionCompare';
import type { ReviewAnchor } from './RevisionCompare';
import { changeStateLabels } from './reviewLabels';

const approvalStatus: Record<string, string> = { pending: '等待人工决定', approved: '已批准', rejected: '已拒绝', stale: '权限或版本已失效' };
const changeActionLabels: Record<string, string> = { add: '新增', revise: '修订', retire: '停用', preserve: '保持', revalidate: '复核' };

function Summary({ blocks }: { blocks: Schema<'ContentBlock'>[] }) {
  if (!blocks.length) return <p className="muted">提交人未提供额外摘要；请直接核对下方固定版本差异。</p>;
  return <div className="content-blocks">{blocks.map(block => <div className={`content-block ${block.kind}`} key={block.blockId}>{block.text && <p>{block.text}</p>}{block.items && <ul>{block.items.map((item, index) => <li key={index}>{item}</li>)}</ul>}{block.columns && block.rows && <div className="table-scroll"><table><thead><tr>{block.columns.map(column => <th key={column}>{column}</th>)}</tr></thead><tbody>{block.rows.map((row, index) => <tr key={index}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}</tr>)}</tbody></table></div>}</div>)}</div>;
}

export default function ChangeReview({ projectId, changeSetId }: { projectId: string; changeSetId: string }) {
  const session = useSession();
  const queryClient = useQueryClient();
  const changeSetQuery = useQuery({
    queryKey: [session.principalId, projectId, 'changeset', changeSetId],
    queryFn: () => atlasApi.changeSet(projectId, changeSetId),
  });
  const changeSet = changeSetQuery.data;
  const [unitId, setUnitId] = useState('');
  const [anchor, setAnchor] = useState<ReviewAnchor | null>(null);
  const [decision, setDecision] = useState<Decision | null>(null);
  const [reopening, setReopening] = useState(false);
  const [reopenReason, setReopenReason] = useState('');
  const [submitSummary, setSubmitSummary] = useState('');
  const [publishOpen, setPublishOpen] = useState(false);
  const [publishConfirmed, setPublishConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [conflict, setConflict] = useState(false);
  const [message, setMessage] = useState('');
  useEffect(() => { if (changeSet?.items.length && !changeSet.items.some(item => item.unitId === unitId)) setUnitId(changeSet.items[0]!.unitId); }, [changeSet, unitId]);
  const closeDecision = useCallback(() => { setDecision(null); setError(''); }, []);

  const refresh = useCallback(async () => {
    await Promise.all([
      changeSetQuery.refetch(),
      queryClient.invalidateQueries({ queryKey: [session.principalId, projectId] }),
      queryClient.invalidateQueries({ queryKey: [session.principalId, 'projects'] }),
    ]);
  }, [changeSetQuery, projectId, queryClient, session.principalId]);
  const run = async (action: (projectVersion: number, key: string) => Promise<unknown>, success: string) => {
    setBusy(true); setError(''); setConflict(false); setMessage('');
    try {
      const projects = await atlasApi.projects();
      const current = projects.items.find(project => project.projectId === projectId);
      if (!current) throw new ApiError('项目已不存在，或当前账号已失去访问权限。', 403);
      await action(current.version, crypto.randomUUID());
      setMessage(success); setDecision(null); setReopening(false); setReopenReason(''); setSubmitSummary(''); setPublishOpen(false); setPublishConfirmed(false);
      await refresh();
      return true;
    } catch (reason) {
      const apiError = reason as ApiError;
      setConflict(apiError.status === 409 || apiError.status === 412);
      setError(apiError.message ?? '操作未完成，请重试。');
      return false;
    } finally { setBusy(false); }
  };
  const review = (slotIds: string[], reason: string) => {
    if (!changeSet?.submitDigest || !changeSet.submittedSeq || !decision) return;
    run((version, key) => atlasApi.reviewChangeSet(projectId, changeSetId, version, { submitDigest: changeSet.submitDigest!, submittedSeq: changeSet.submittedSeq!, slotIds, decision, reason }, key),
      decision === 'approve' ? '人工批准已由服务端记录。只有全部必要责任批准后，成果才进入待发布状态。' : decision === 'reject' ? '拒绝决定已记录；正式已发布事实未发生变化。' : '修改要求已记录；候选人可重新打开、修订并提交新的固定版本。');
  };
  const reopen = (event: FormEvent) => {
    event.preventDefault(); if (!changeSet || !reopenReason.trim()) return;
    run(async (version, key) => {
      const projects = await atlasApi.projects();
      const latestBase = projects.items.find(project => project.projectId === projectId)?.currentBaselineId;
      return atlasApi.reopenChangeSet(projectId, changeSetId, version, reopenReason.trim(), latestBase, key);
    }, '成果已重新打开为草稿。请核对旧批注和当前基线，再提交新的固定版本。');
  };
  const resubmit = (event: FormEvent) => {
    event.preventDefault(); if (!changeSet || !submitSummary.trim()) return;
    const summary = [...changeSet.summary, { blockId: `submission-${(changeSet.submittedSeq ?? 0) + 1}`, kind: 'paragraph' as const, text: submitSummary.trim(), references: [] }];
    run((version, key) => atlasApi.submitChangeSet(projectId, changeSetId, version, summary, key), '新的固定提交已生成，原批准不会沿用；请由有权人员重新审核。');
  };
  const publish = () => {
    if (!changeSet?.submitDigest) return;
    run((version, key) => atlasApi.publishChangeSet(projectId, changeSetId, version, changeSet.submitDigest!, changeSet.baseBaselineId, key), '发布已完成。页面将重新读取服务端正式基线，不使用本地状态替代发布事实。').then(succeeded => { if (succeeded) queryClient.invalidateQueries({ queryKey: [session.principalId, projectId] }); });
  };

  if (changeSetQuery.isPending) return <LoadingState label="正在读取成果、差异与审批责任" />;
  if (changeSetQuery.isError || !changeSet) return <ErrorState error={changeSetQuery.error} onRetry={() => changeSetQuery.refetch()} />;
  const selected = changeSet.items.find(item => item.unitId === unitId) ?? changeSet.items[0];
  const canReview = changeSet.state === 'submitted' || changeSet.state === 'in_review';
  const canReopen = ['changes_requested', 'rejected', 'approved', 'conflict'].includes(changeSet.state);
  const pending = changeSet.approvals.filter(slot => slot.status === 'pending');
  return <div className="change-review">
    <header className="change-review-header"><div><span className={`review-state ${changeSet.state}`}>{changeStateLabels[changeSet.state]}</span><h2>{changeSet.title}</h2><p>第 {changeSet.submittedSeq ?? 0} 次提交 · {changeSet.items.length} 项成果 · 基于已发布版本</p></div><button className="quiet" onClick={() => refresh()} disabled={busy}>重新核验</button></header>
    {message && <div className="notice" role="status">{message}</div>}
    {error && !decision && <div className="notice error" role="alert"><strong>{conflict ? '版本或权限已变化，未覆盖他人工作' : '操作未完成'}</strong><p>{error}</p>{conflict && <button className="secondary" onClick={() => { setError(''); refresh(); }}>读取最新成果与权限</button>}</div>}
    <section className="submission-summary"><p className="eyebrow">01 / INTENT</p><h3>本次需要解决什么</h3><Summary blocks={changeSet.summary} /></section>
    {changeSet.unknowns.length > 0 && <section className="review-unknowns"><h3>需要人判断的未知与限制</h3><UnknownList items={changeSet.unknowns} /></section>}
     <section className="approval-responsibilities"><div className="section-head"><div><p className="eyebrow">02 / RESPONSIBILITY</p><h3>当前审批责任</h3></div><span>不是自动审批</span></div><div className="approval-slots">{changeSet.approvals.map(slot => <article key={slot.slotId}><div><strong>{slot.role}</strong><span className={`approval-status ${slot.status}`}>{approvalStatus[slot.status]}</span></div><small>{slot.risk} 风险 · 负责 {slot.scope.length} 项成果</small></article>)}{changeSet.approvals.length === 0 && <p className="muted">草稿提交后将生成必要审批责任。</p>}</div><p className="permission-note">按钮仅表示当前流程可执行的操作；具体角色、本人不可审自己提交、撤权与审批时效均由服务端再次强制校验。</p></section>
    <section className="change-contents"><div className="section-head"><div><p className="eyebrow">03 / READABLE DIFF</p><h3>逐项核对业务成果</h3></div><span>左侧为批准前，右侧为候选</span></div>
       {changeSet.items.length > 1 && <div className="change-item-tabs" role="tablist" aria-label="变更成果">{changeSet.items.map(item => <button role="tab" aria-selected={selected?.unitId === item.unitId} className={selected?.unitId === item.unitId ? 'active' : ''} key={item.unitId} onClick={() => { setUnitId(item.unitId); setAnchor(null); }}>{changeActionLabels[item.action] ?? '核对成果'}<span>{item.reason}</span></button>)}</div>}
      {selected ? <RevisionCompare projectId={projectId} changeSet={changeSet} item={selected} onAnnotate={setAnchor} /> : <p className="muted">此变更集没有可阅读的成果项。</p>}
    </section>
    <InlineComments projectId={projectId} changeSet={changeSet} anchor={anchor} onAnchorConsumed={() => setAnchor(null)} />
    <section className="review-actions"><p className="eyebrow">04 / DECISION & PUBLICATION</p><h3>人工决定与独立发布</h3>
      {canReview && <><p>仍有 {pending.length} 项审批责任待处理。批准只针对当前固定提交；任何重提都需要重新批准。</p><div className="actions"><button className="primary" disabled={busy || !pending.length} onClick={() => { setError(''); setDecision('approve'); }}>批准明确范围</button><button className="secondary" disabled={busy || !pending.length} onClick={() => { setError(''); setDecision('request_changes'); }}>要求修改</button><button className="danger" disabled={busy || !pending.length} onClick={() => { setError(''); setDecision('reject'); }}>拒绝本次候选</button></div></>}
      {changeSet.state === 'approved' && <div className="publish-panel"><div><strong>全部必要批准已齐备，尚未发布</strong><p>发布会再次检查当前基线、固定摘要、批准完整性及批准人的当前权限。</p></div><button className="primary" disabled={busy} onClick={() => { setPublishOpen(true); setPublishConfirmed(false); }}>核验并发布</button><button className="secondary" disabled={busy} onClick={() => setReopening(true)}>撤回批准并重新打开</button></div>}
      {canReopen && changeSet.state !== 'approved' && <button className="secondary" disabled={busy} onClick={() => setReopening(value => !value)}>{reopening ? '取消重新打开' : changeSet.state === 'conflict' ? '基于当前基线处理冲突' : '重新打开以修订'}</button>}
      {reopening && canReopen && <form className="reopen-form" onSubmit={reopen}><p>重新打开会使旧批准失效，但不会删除历史决定或批注。如项目基线已前进，将请求服务端基于最新基线重开并检查冲突。</p><label htmlFor="reopen-reason">重开理由</label><textarea id="reopen-reason" rows={3} required maxLength={4000} value={reopenReason} onChange={event => setReopenReason(event.target.value)} /><button className="primary" disabled={busy || !reopenReason.trim()}>重新打开为草稿</button></form>}
      {changeSet.state === 'draft' && <form className="resubmit-form" onSubmit={resubmit}><p>提交前确认旧批注已有回应。提交会冻结新的内容与审批摘要，不能沿用上一次批准。</p><label htmlFor="submit-summary">本次提交说明</label><textarea id="submit-summary" rows={3} required maxLength={4000} value={submitSummary} onChange={event => setSubmitSummary(event.target.value)} placeholder="说明本次修订内容，以及对旧意见的回应" /><button className="primary" disabled={busy || !submitSummary.trim() || !changeSet.items.length}>提交人工复审</button></form>}
      {changeSet.state === 'published' && <div className="published-confirmation"><strong>成果已经发布</strong><p>正式基线：{changeSet.publishedBaselineId ?? '由服务端历史确认'}。请从项目阅读页重新读取正式事实。</p><Link className="secondary link-button" to={`/p/${projectId}/overview`}>查看正式发布</Link></div>}
    </section>
    {decision && <DecisionDialog changeSet={changeSet} decision={decision} busy={busy} error={error} onCancel={closeDecision} onConfirm={review} />}
      {publishOpen && <div className="modal-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) setPublishOpen(false); }}><section className="decision-dialog" role="dialog" aria-modal="true" aria-labelledby="publish-title"><div className="dialog-head"><div><p className="eyebrow">发布确认</p><h2 id="publish-title">发布已批准的固定成果</h2></div><button className="icon-button" aria-label="关闭发布确认" onClick={() => setPublishOpen(false)} disabled={busy}>×</button></div><p>服务端将原子创建新基线；若基线、权限或批准已变化，操作会失败，不会覆盖他人发布。</p>{error && <div className="notice error" role="alert">{error}</div>}<dl className="digest-confirmation"><div><dt>批准范围</dt><dd>基于当前已记录的发布版本</dd></div><div><dt>固定提交摘要</dt><dd><code>{changeSet.submitDigest?.slice(0, 16)}…</code></dd></div></dl><label className="approval-confirmation"><input type="checkbox" checked={publishConfirmed} onChange={event => setPublishConfirmed(event.target.checked)} /><span>我确认独立执行发布，并已核对当前批准范围和业务风险。</span></label><div className="dialog-actions"><button className="secondary" disabled={busy} onClick={() => setPublishOpen(false)}>取消</button><button className="primary" disabled={busy || !publishConfirmed} onClick={publish}>{busy ? '正在发布…' : '确认发布新基线'}</button></div></section></div>}
  </div>;
}
