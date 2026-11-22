import { useInfiniteQuery } from '@tanstack/react-query';
import { Link, useNavigate, useOutletContext, useParams, useSearchParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import { useSession } from '../../shared/session';
import { EmptyState, ErrorState, LoadingState } from '../../shared/states';
import type { ProjectOutletContext } from '../project/ProjectShell';
import ChangeReview from './ChangeReview';
import { changeStateLabels } from './reviewLabels';

const filters = [
  { key: 'pending', label: '待审核', states: ['submitted', 'in_review'] },
  { key: 'returned', label: '待修订', states: ['draft', 'changes_requested', 'rejected', 'conflict'] },
  { key: 'approved', label: '待发布', states: ['approved'] },
  { key: 'published', label: '已发布', states: ['published'] },
  { key: 'all', label: '全部成果', states: [] },
] as const;

type FilterKey = typeof filters[number]['key'];

export default function ReviewWorkspace() {
  const { project } = useOutletContext<ProjectOutletContext>();
  const { projectId = '', changeSetId } = useParams();
  const session = useSession();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const filter = (searchParams.get('view') ?? 'pending') as FilterKey;
  const activeFilter = filters.some(item => item.key === filter) ? filter : 'pending';
  const changeSets = useInfiniteQuery({
    queryKey: [session.principalId, projectId, project.policyEpoch, 'review-queue'],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => atlasApi.changeSets(projectId, pageParam),
    getNextPageParam: last => last.nextCursor ?? undefined,
  });
  const baselines = useInfiniteQuery({
    queryKey: [session.principalId, projectId, project.policyEpoch, 'review-baselines'],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) => atlasApi.baselines(projectId, pageParam),
    getNextPageParam: last => last.nextCursor ?? undefined,
  });
  const items = changeSets.data?.pages.flatMap(page => page.items) ?? [];
  const definition = filters.find(item => item.key === activeFilter)!;
  const visible = definition.states.length ? items.filter(item => definition.states.some(state => state === item.state)) : items;
  const select = (id: string) => navigate(`/p/${projectId}/review/${id}?view=${activeFilter}`);

  return <div className="review-workspace">
    <div className="page-heading"><p className="eyebrow">HUMAN REVIEW / GOVERNANCE</p><h1>变更与审核</h1><p>审核的是可阅读的业务成果及其影响，不是底层记录。批准、退回和发布均由服务端按当前身份与固定版本核验。</p></div>
    <div className="review-layout">
      <aside className="review-queue" aria-label="审核成果队列">
        <div className="section-head"><div><p className="eyebrow">REVIEW QUEUE</p><h2>成果队列</h2></div><button className="quiet" onClick={() => changeSets.refetch()} disabled={changeSets.isFetching}>刷新</button></div>
        <div className="review-filters" role="tablist" aria-label="成果状态">{filters.map(item => <button key={item.key} role="tab" aria-selected={activeFilter === item.key} className={activeFilter === item.key ? 'active' : ''} onClick={() => navigate(`/p/${projectId}/review${changeSetId ? `/${changeSetId}` : ''}?view=${item.key}`)}>{item.label}<span>{item.states.length ? items.filter(change => item.states.some(state => state === change.state)).length : items.length}</span></button>)}</div>
        {changeSets.isPending ? <LoadingState label="正在读取有权查看的成果" /> : changeSets.isError ? <ErrorState error={changeSets.error} onRetry={() => changeSets.refetch()} /> : visible.length === 0 ? <EmptyState title="此视图没有成果">切换状态查看其他成果，或从知识阅读页创建候选。</EmptyState> : <div className="review-list">{visible.map(item => <button key={item.changeSetId} className={changeSetId === item.changeSetId ? 'selected' : ''} onClick={() => select(item.changeSetId)}><span className={`review-state ${item.state}`}>{changeStateLabels[item.state]}</span><strong>{item.title}</strong><small>{item.items.length} 项成果 · 第 {item.submittedSeq ?? 0} 次提交</small>{item.state === 'approved' && <small>全部必要批准已完成，尚未发布</small>}</button>)}</div>}{changeSets.hasNextPage && <button className="secondary load-more" disabled={changeSets.isFetchingNextPage} onClick={() => changeSets.fetchNextPage()}>{changeSets.isFetchingNextPage ? '正在读取…' : '加载更多成果'}</button>}
        <Link className="import-shortcut" to={`/p/${projectId}/imports`}>资料接入与导入预览 <span>↗</span></Link>
      </aside>
      <section className="review-detail" aria-label="成果审核详情">
        {!changeSetId ? <EmptyState title="选择一项成果开始审核">查看业务内容的前后差异、保持项、未知与审批责任，再决定批准、要求修改或拒绝。</EmptyState> : <ChangeReview key={changeSetId} projectId={projectId} changeSetId={changeSetId} />}
      </section>
    </div>
    <section className="baseline-history"><div className="section-head"><div><p className="eyebrow">PUBLISHED HISTORY</p><h2>正式发布历史</h2></div><button className="quiet" onClick={() => baselines.refetch()}>刷新</button></div><p>历史基线不可被审核界面直接改写；每次发布都有独立基线与内容摘要。</p>{baselines.isError ? <ErrorState error={baselines.error} onRetry={() => baselines.refetch()} /> : <div className="baseline-list">{baselines.data?.pages.flatMap(page => page.items).map(baseline => <article key={baseline.baselineId}><strong>基线 #{baseline.number}</strong><span>{baseline.changeSetId ? <Link to={`/p/${projectId}/review/${baseline.changeSetId}?view=published`}>查看对应成果</Link> : '项目初始基线'}</span><small>{new Date(baseline.createdAt).toLocaleString('zh-CN')} · 摘要 {baseline.digest.slice(0, 12)}…</small></article>)}</div>}{baselines.hasNextPage && <button className="secondary load-more" disabled={baselines.isFetchingNextPage} onClick={() => baselines.fetchNextPage()}>加载更早基线</button>}</section>
  </div>;
}
