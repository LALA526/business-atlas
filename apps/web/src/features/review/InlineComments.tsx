import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { atlasApi } from '../../shared/api';
import type { Activity, ChangeSet } from '../../shared/api';
import { useSession } from '../../shared/session';
import { ErrorState, LoadingState } from '../../shared/states';
import type { ReviewAnchor } from './RevisionCompare';

const commentState: Record<Activity['state'], string> = { open: '待处理', addressed: '已关联处理', closed: '已关闭', withdrawn: '已撤回' };

async function sha256(text: string) {
  const bytes = new TextEncoder().encode(text);
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('');
}

function anchorOf(comment: Activity) {
  const target = comment.target as { anchor?: Record<string, unknown> };
  const anchor = target.anchor ?? {};
  return {
    blockId: typeof anchor.blockId === 'string' ? anchor.blockId : undefined,
    submittedSeq: typeof anchor.submittedSeq === 'number' ? anchor.submittedSeq : undefined,
    quote: typeof anchor.quote === 'string' ? anchor.quote : undefined,
  };
}

export default function InlineComments({ projectId, changeSet, anchor, onAnchorConsumed }: { projectId: string; changeSet: ChangeSet; anchor: ReviewAnchor | null; onAnchorConsumed: () => void }) {
  const session = useSession();
  const queryClient = useQueryClient();
  const [body, setBody] = useState('');
  const [quote, setQuote] = useState('');
  const [resolution, setResolution] = useState<Record<string, string>>({});
  const [message, setMessage] = useState('');
  const comments = useQuery({
    queryKey: [session.principalId, projectId, changeSet.changeSetId, 'comments'],
    queryFn: () => atlasApi.comments(projectId, changeSet.changeSetId),
  });
  useEffect(() => { setQuote(anchor?.quote ?? ''); }, [anchor]);
  const invalidate = () => queryClient.invalidateQueries({ queryKey: [session.principalId, projectId, changeSet.changeSetId, 'comments'] });
  const create = useMutation({
    mutationFn: async () => {
      const exactQuote = quote.trim();
      const anchorValue = {
        ...(anchor?.blockId ? { blockId: anchor.blockId } : {}),
        ...(changeSet.submittedSeq ? { submittedSeq: changeSet.submittedSeq } : {}),
        ...(exactQuote ? { quote: exactQuote, quoteHash: await sha256(exactQuote) } : {}),
      };
      return atlasApi.createComment(projectId, {
        changeSetId: changeSet.changeSetId, unitId: anchor?.unitId, revisionId: anchor?.revisionId,
        anchor: anchorValue, body: body.trim(),
      }, crypto.randomUUID());
    },
    onSuccess: () => { setBody(''); setQuote(''); setMessage('批注已保存，并绑定到当前提交与成果版本。'); onAnchorConsumed(); invalidate(); },
  });
  const resolve = useMutation({
    mutationFn: async (comment: Activity) => atlasApi.resolveComment(projectId, comment.id, comment.version, resolution[comment.id]?.trim() ?? '', changeSet.changeSetId, crypto.randomUUID()),
    onSuccess: () => { setMessage('批注处置已记录；知识事实未因此被直接修改。'); invalidate(); },
  });
  const submit = (event: FormEvent) => { event.preventDefault(); if (body.trim()) create.mutate(); };
  const error = create.error ?? resolve.error;

  return <section className="inline-comments" aria-labelledby="comments-title"><div className="section-head"><div><p className="eyebrow">INLINE DISCUSSION</p><h2 id="comments-title">成果批注与复审回应</h2></div><span>{comments.data?.items.length ?? 0} 条</span></div>
    <p className="muted">批注固定到提交序号、候选修订和原文摘要。重新提交后，旧意见仍保留，可对照实际差异确认是否已回应。</p>
    {message && <div className="notice" role="status">{message}</div>}{error && <div className="notice error" role="alert">{error.message}</div>}
    <form onSubmit={submit} className="comment-composer">
      <div className="comment-anchor">{anchor?.blockId ? <>批注段落 <code>{anchor.blockId}</code></> : '批注整个成果'}<span> · 第 {changeSet.submittedSeq ?? 0} 次提交</span>{anchor && <button type="button" className="quiet" onClick={() => { setQuote(''); onAnchorConsumed(); }}>改为整体批注</button>}</div>
      {anchor?.quote && <label>固定原文（可按需要缩小引用范围）<textarea value={quote} onChange={event => setQuote(event.target.value)} rows={3} maxLength={4000} /></label>}
      <label htmlFor="review-comment">批注内容</label><textarea id="review-comment" value={body} onChange={event => setBody(event.target.value)} rows={4} maxLength={20000} placeholder="指出具体业务问题、缺失依据或需要修改的结果" required />
      <button className="primary" disabled={!body.trim() || create.isPending}>{create.isPending ? '正在保存…' : '保存批注'}</button>
    </form>
    {comments.isPending ? <LoadingState label="正在读取批注" /> : comments.isError ? <ErrorState error={comments.error} onRetry={() => comments.refetch()} /> : <div className="comment-list">{comments.data?.items.map(comment => {
      const target = anchorOf(comment);
      return <article key={comment.id} className={comment.state === 'open' ? 'open' : ''}><header><strong>{comment.authorId === session.principalId ? '我的批注' : `项目成员 ${comment.authorId.slice(0, 8)}`}</strong><span className={`comment-state ${comment.state}`}>{commentState[comment.state]}</span><time>{new Date(comment.createdAt).toLocaleString('zh-CN')}</time></header><small>第 {target.submittedSeq ?? '—'} 次提交{target.blockId ? ` · 段落 ${target.blockId}` : ' · 整体成果'}</small>{target.quote && <blockquote>{target.quote}</blockquote>}<p>{comment.body}</p>{comment.state === 'open' && <div className="comment-resolution"><label>处置说明<textarea rows={2} value={resolution[comment.id] ?? ''} onChange={event => setResolution(current => ({ ...current, [comment.id]: event.target.value }))} placeholder="说明如何回应，或为何关闭此意见" /></label><button className="secondary" disabled={!resolution[comment.id]?.trim() || resolve.isPending} onClick={() => resolve.mutate(comment)}>记录处置</button></div>}</article>;
    })}{comments.data?.items.length === 0 && <p className="muted">尚无批注。可在上方差异段落中选择“批注此段”。</p>}</div>}
  </section>;
}
