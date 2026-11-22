import { useQuery } from '@tanstack/react-query';
import { apiRequest } from '../../shared/api';
import type { ChangeSet, Revision, Schema } from '../../shared/api';
import { useSession } from '../../shared/session';
import { ErrorState, LoadingState } from '../../shared/states';

type Item = ChangeSet['items'][number];
type Block = Schema<'ContentBlock'>;
export type ReviewAnchor = { unitId: string; revisionId?: string; blockId?: string; quote?: string };

export const actionLabels: Record<Item['action'], string> = { add: '新增成果', revise: '修订成果', retire: '退役成果', preserve: '明确保持', revalidate: '重新核验' };

function blockText(block?: Block) {
  if (!block) return '';
  return [block.text, ...(block.items ?? []), ...(block.rows ?? []).flat()].filter(Boolean).join('\n');
}

function BlockContent({ block }: { block?: Block }) {
  if (!block) return <p className="diff-absent">此版本没有对应段落。</p>;
  return <div className={`content-block ${block.kind}`} id={`review-${block.blockId}`}>
    {block.text && <p>{block.text}</p>}
    {block.items && <ul>{block.items.map((item, index) => <li key={index}>{item}</li>)}</ul>}
    {block.columns && block.rows && <div className="table-scroll"><table><thead><tr>{block.columns.map(column => <th scope="col" key={column}>{column}</th>)}</tr></thead><tbody>{block.rows.map((row, index) => <tr key={index}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}</tr>)}</tbody></table></div>}
  </div>;
}

function bodyEntries(revision?: Revision) {
  if (!revision) return [];
  const labels: Record<string, string> = { purpose: '业务目的', problem: '需要解决的问题', goal: '期望结果', scope: '适用范围', definition: '定义', statement: '约束说明', responsibility: '职责', description: '说明', successCriteria: '成功标准', nonGoals: '不在范围内', nonResponsibility: '不承担职责', steps: '步骤', outcomes: '业务结果', errors: '异常处理' };
  return Object.entries(labels).flatMap(([key, label]) => {
    const value = revision.body[key];
    if (typeof value === 'string' && value) return [{ label, value }];
    if (Array.isArray(value) && value.every(item => typeof item === 'string')) return [{ label, value: value.join('；') }];
    return [];
  });
}

export default function RevisionCompare({ projectId, changeSet, item, onAnnotate }: { projectId: string; changeSet: ChangeSet; item: Item; onAnnotate: (anchor: ReviewAnchor) => void }) {
  const session = useSession();
  const contextKey = [session.principalId, projectId, changeSet.changeSetId, changeSet.submittedSeq ?? 0, item.unitId];
  const candidate = useQuery({
    queryKey: [...contextKey, 'candidate', item.proposedRevisionId], enabled: Boolean(item.proposedRevisionId),
    queryFn: () => apiRequest<Revision>(changeSet.state === 'published' && changeSet.publishedBaselineId ? `/api/v1/projects/${projectId}/units/${item.unitId}?mode=historical&purpose=review&baselineId=${changeSet.publishedBaselineId}` : `/api/v1/projects/${projectId}/units/${item.unitId}?mode=draft&purpose=review&baselineId=${changeSet.baseBaselineId}&changeSetId=${changeSet.changeSetId}`),
  });
  const before = useQuery({
    queryKey: [...contextKey, 'before', item.baseRevisionId], enabled: Boolean(item.baseRevisionId),
    queryFn: () => apiRequest<Revision>(`/api/v1/projects/${projectId}/units/${item.unitId}?mode=historical&purpose=review&baselineId=${changeSet.baseBaselineId}`),
  });
  const loading = candidate.isPending || before.isPending;
  const afterRevision = candidate.data;
  const beforeRevision = before.data;
  const ids = [...new Set([...(beforeRevision?.blocks.map(block => block.blockId) ?? []), ...(afterRevision?.blocks.map(block => block.blockId) ?? [])])];
  const afterBlocks = new Map(afterRevision?.blocks.map(block => [block.blockId, block]));
  const beforeBlocks = new Map(beforeRevision?.blocks.map(block => [block.blockId, block]));
  const fallback = bodyEntries(afterRevision).length ? bodyEntries(afterRevision) : bodyEntries(beforeRevision);

  return <article className="revision-compare">
    <header><div><span className={`change-action ${item.action}`}>{actionLabels[item.action]}</span><h3>{afterRevision?.title ?? beforeRevision?.title ?? '正在读取成果标题'}</h3></div><span className="dependency-group">依赖组：{item.dependencyGroup}</span></header>
    <p className="change-reason"><strong>本次变更理由：</strong>{item.reason}</p>
    {loading ? <LoadingState label="正在读取固定版本的前后内容" /> : candidate.isError ? <ErrorState error={candidate.error} onRetry={() => candidate.refetch()} /> : before.isError ? <ErrorState error={before.error} onRetry={() => before.refetch()} /> : <>
      <div className="revision-labels"><div><span>批准前</span><strong>{beforeRevision ? `第 ${beforeRevision.revNo ?? '?'} 版` : '无既有成果'}</strong>{beforeRevision && <small>内容摘要 {beforeRevision.sha256.slice(0, 12)}…</small>}</div><div><span>本次候选</span><strong>{afterRevision ? `第 ${afterRevision.revNo ?? '?'} 版` : item.action === 'retire' ? '退役后不再发布' : '无候选正文'}</strong>{afterRevision && <small>内容摘要 {afterRevision.sha256.slice(0, 12)}…</small>}</div></div>
      {ids.length > 0 ? <div className="diff-blocks">{ids.map(id => {
        const oldBlock = beforeBlocks.get(id); const newBlock = afterBlocks.get(id);
        const changed = blockText(oldBlock) !== blockText(newBlock);
        const anchorRevision = afterRevision ?? beforeRevision;
        return <section className={changed ? 'diff-block changed' : 'diff-block unchanged'} key={id}><div className="diff-block-head"><span>{changed ? oldBlock && newBlock ? '内容有变化' : newBlock ? '新增段落' : '删除段落' : '保持不变'}</span>{anchorRevision && <button className="quiet" onClick={() => onAnnotate({ unitId: item.unitId, revisionId: anchorRevision.revisionId, blockId: id, quote: blockText(newBlock ?? oldBlock) })}>批注此段</button>}</div><div className="diff-columns"><div><BlockContent block={oldBlock} /></div><div><BlockContent block={newBlock} /></div></div></section>;
      })}</div> : fallback.length > 0 ? <div className="semantic-fallback">{fallback.map(entry => <div key={entry.label}><strong>{entry.label}</strong><p>{entry.value}</p></div>)}<button className="secondary" onClick={() => onAnnotate({ unitId: item.unitId, revisionId: (afterRevision ?? beforeRevision)?.revisionId, quote: fallback.map(entry => entry.value).join('\n') })}>批注此成果</button></div> : <p className="muted">此治理动作没有候选正文。请结合变更理由、影响与审批范围核验。</p>}
      {(afterRevision?.unknowns.length || beforeRevision?.unknowns.length) ? <div className="revision-unknowns"><strong>候选中的未知与限制</strong>{(afterRevision?.unknowns ?? beforeRevision?.unknowns ?? []).map(unknown => <p key={unknown.key}>{unknown.description}{unknown.nextAction ? ` · 下一步：${unknown.nextAction}` : ''}</p>)}</div> : null}
    </>}
  </article>;
}
