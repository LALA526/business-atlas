import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import type { ChangeSet, ReviewRequest } from '../../shared/api';

export type Decision = ReviewRequest['decision'];
const copy: Record<Decision, { title: string; action: string; description: string }> = {
  approve: { title: '批准明确范围的成果', action: '确认人工批准', description: '批准只确认所选责任范围，不会直接发布。发布仍需独立权限和再次核验。' },
  request_changes: { title: '要求修改后重新提交', action: '退回修改', description: '请说明需要修改的业务结果。当前候选不会成为正式事实。' },
  reject: { title: '拒绝本次候选', action: '确认拒绝', description: '拒绝本次提交，保留历史与批注；已发布内容不受影响。' },
};

export default function DecisionDialog({ changeSet, decision, busy, error, onCancel, onConfirm }: { changeSet: ChangeSet; decision: Decision; busy: boolean; error: string; onCancel: () => void; onConfirm: (slotIds: string[], reason: string) => void }) {
  const pending = changeSet.approvals.filter(slot => slot.status === 'pending');
  const [selected, setSelected] = useState<string[]>(pending.map(slot => slot.slotId));
  const [reason, setReason] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const closeButton = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    closeButton.current?.focus();
    const keydown = (event: KeyboardEvent) => { if (event.key === 'Escape' && !busy) onCancel(); };
    document.addEventListener('keydown', keydown);
    return () => { document.removeEventListener('keydown', keydown); previous?.focus(); };
  }, [busy, onCancel]);
  const submit = (event: FormEvent) => { event.preventDefault(); if (selected.length && reason.trim() && (decision !== 'approve' || confirmed)) onConfirm(selected, reason.trim()); };
  const groups = [...new Set(changeSet.items.map(item => item.dependencyGroup))];
  return <div className="modal-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) onCancel(); }}><section className="decision-dialog" role="dialog" aria-modal="true" aria-labelledby="decision-title">
    <div className="dialog-head"><div><p className="eyebrow">FIXED SUBMISSION / HUMAN DECISION</p><h2 id="decision-title">{copy[decision].title}</h2></div><button ref={closeButton} className="icon-button" aria-label="关闭审核决定" onClick={onCancel} disabled={busy}>×</button></div>
    <p>{copy[decision].description}</p>
    <dl className="digest-confirmation"><div><dt>提交序号</dt><dd>第 {changeSet.submittedSeq} 次</dd></div><div><dt>固定版本摘要</dt><dd><code>{changeSet.submitDigest?.slice(0, 16)}…</code></dd></div><div><dt>必要依赖组</dt><dd>{groups.join('、')}</dd></div></dl>
    <form onSubmit={submit}>
      <fieldset><legend>本次{decision === 'approve' ? '批准' : '处理'}的审批责任</legend>{pending.map(slot => <label className="approval-choice" key={slot.slotId}><input type="checkbox" checked={selected.includes(slot.slotId)} onChange={event => setSelected(current => event.target.checked ? [...current, slot.slotId] : current.filter(id => id !== slot.slotId))} /><span><strong>{slot.role} · {slot.slotId}</strong><small>{slot.risk} 风险 · {slot.scope.length} 项成果</small></span></label>)}{pending.length === 0 && <p>当前没有待处理的审批责任。</p>}</fieldset>
      <label htmlFor="decision-reason">业务理由（必填）</label><textarea id="decision-reason" rows={4} maxLength={4000} required value={reason} onChange={event => setReason(event.target.value)} placeholder={decision === 'approve' ? '说明已核对的内容、依据与风险接受理由' : '说明不可接受的内容以及需要怎样修改'} />
      {decision === 'approve' && <label className="approval-confirmation"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /><span>我确认正在批准上述固定提交、责任范围和依赖组；这不是自动批准，也不代表已经发布。</span></label>}
      {error && <div className="notice error" role="alert">{error}</div>}
      <div className="dialog-actions"><button type="button" className="secondary" onClick={onCancel} disabled={busy}>取消</button><button className={decision === 'approve' ? 'primary' : 'danger'} disabled={busy || !selected.length || !reason.trim() || (decision === 'approve' && !confirmed)}>{busy ? '正在提交…' : copy[decision].action}</button></div>
    </form>
  </section></div>;
}
