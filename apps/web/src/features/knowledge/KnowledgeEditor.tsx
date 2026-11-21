import { useState } from 'react';
import type { FormEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { ApiError, atlasApi } from '../../shared/api';
import type { ChangeSet, Project, Revision, RevisionInput, Schema } from '../../shared/api';

function readableText(revision: Revision) {
  return revision.blocks.map(block => block.text ?? block.items?.join('\n') ?? '').filter(Boolean).join('\n\n');
}

export default function KnowledgeEditor({ project, revision, onClose }: { project: Project; revision: Revision; onClose: () => void }) {
  const [title, setTitle] = useState(revision.title);
  const [text, setText] = useState(readableText(revision));
  const [reason, setReason] = useState('');
  const [changeSet, setChangeSet] = useState<ChangeSet>();
  const block: Schema<'ContentBlock'> = { blockId: 'edited-business-content', kind: 'paragraph', text, references: [{ unitId: revision.unitId, revisionId: revision.revisionId }] };
  const save = useMutation({ mutationFn: async () => {
    const created = await atlasApi.createChangeSet(project.projectId, `修订：${title.trim()}`, project.currentBaselineId, crypto.randomUUID());
    const candidate: RevisionInput = {
      unitId: revision.unitId, title: title.trim(), kind: revision.kind, schemaVersion: revision.schemaVersion,
      factKind: revision.factKind, verification: 'unverified', applicability: revision.applicability ?? { unrestricted: true },
      body: revision.body, blocks: [block], evidence: revision.evidence ?? [], unknowns: revision.unknowns,
      categoryKeys: revision.categoryKeys ?? [],
    };
    await atlasApi.reviseUnitDraft(project.projectId, created.changeSetId, revision.unitId, candidate, crypto.randomUUID(), String(created.version));
    return atlasApi.changeSet(project.projectId, created.changeSetId);
  }, onSuccess: setChangeSet });
  const submit = useMutation({ mutationFn: async () => {
    if (!changeSet) throw new Error('请先保存候选草稿。');
    const current = await atlasApi.changeSet(project.projectId, changeSet.changeSetId);
    return atlasApi.submitChangeSet(project.projectId, current.changeSetId, current.version, [{ blockId: 'submission-summary', kind: 'paragraph', text: reason.trim(), references: [{ unitId: revision.unitId, revisionId: revision.revisionId }] }], crypto.randomUUID());
  }, onSuccess: setChangeSet });
  const error = save.error ?? submit.error;
  const conflict = error instanceof ApiError && (error.status === 409 || error.status === 412);

  const onSubmit = (event: FormEvent) => { event.preventDefault(); if (!changeSet) save.mutate(); };
  return <section className="knowledge-editor" aria-labelledby="editor-title">
    <div className="section-heading"><div><p className="eyebrow">候选编辑</p><h2 id="editor-title">修改业务内容</h2></div><button className="quiet" onClick={onClose}>返回阅读</button></div>
    <p className="editor-intro">编辑会创建独立候选修订，不会直接覆盖已发布知识，也不会代替人工审批。原始类型、适用范围、来源和未知项随候选保留。</p>
    <form onSubmit={onSubmit}>
      <label htmlFor="candidate-title">标题<input id="candidate-title" required value={title} onChange={event => setTitle(event.target.value)} disabled={Boolean(changeSet)} /></label>
      <label htmlFor="candidate-content">可读业务内容<textarea id="candidate-content" required rows={12} value={text} onChange={event => setText(event.target.value)} disabled={Boolean(changeSet)} placeholder="写下业务目的、条件和结果；不确定之处请明确标注未知。" /></label>
      <label htmlFor="candidate-reason">本次修改原因<textarea id="candidate-reason" required rows={3} value={reason} onChange={event => setReason(event.target.value)} disabled={Boolean(changeSet)} placeholder="说明要解决的业务问题，供审核人理解差异。" /></label>
      {text.includes('一致则应用') && <p className="editor-warning">“一致则应用”缺少可核验的比较依据。请说明比较对象、判断条件和预期结果，或将其保留为明确未知。</p>}
      {error && <div className="notice error" role="alert"><strong>{conflict ? '版本发生冲突，未覆盖他人的修改' : '操作未完成'}</strong><p>{error.message}</p>{conflict && <p>请重新读取当前版本，核对前后差异后再基于当前版本提交。不要使用重复提交绕过冲突。</p>}</div>}
      {changeSet && <div className="notice" role="status"><strong>{changeSet.state === 'submitted' || changeSet.state === 'in_review' ? '候选已提交人工审核' : '候选草稿已保存，尚未提交审核'}</strong><p>已发布内容保持不变。候选版本：{changeSet.version}</p></div>}
      <div className="editor-actions">{!changeSet ? <button className="primary" disabled={save.isPending || !title.trim() || !text.trim() || !reason.trim()}>{save.isPending ? '正在保存…' : '保存候选草稿'}</button> : !['submitted', 'in_review'].includes(changeSet.state) ? <button type="button" className="primary" disabled={submit.isPending} onClick={() => submit.mutate()}>{submit.isPending ? '正在提交…' : '提交人工审核'}</button> : <Link className="primary link-button" to={`/p/${project.projectId}/review`}>前往变更与审核 →</Link>}<button type="button" className="secondary" onClick={onClose}>取消</button></div>
    </form>
  </section>;
}
