import { useState } from 'react';
import type { FormEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useOutletContext, useParams, useSearchParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import { useSession } from '../../shared/session';
import { EmptyState, ErrorState, LoadingState } from '../../shared/states';
import type { ProjectOutletContext } from './ProjectShell';
import { factKindLabel, knowledgeKindLabel, verificationLabel } from './knowledgeLabels';

export default function CatalogPage() {
  const { project } = useOutletContext<ProjectOutletContext>();
  const { projectId = '' } = useParams();
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const query = params.get('q') ?? '';
  const [draft, setDraft] = useState(query);
  const [category, setCategory] = useState('');
  const [unitCursors, setUnitCursors] = useState<(string | undefined)[]>([undefined]);
  const unitCursor = unitCursors[unitCursors.length - 1];
  const key = [session.principalId, projectId, project.policyEpoch];
  const catalog = useQuery({ queryKey: [...key, 'catalog', project.currentBaselineId], queryFn: () => atlasApi.catalog(projectId) });
  const search = useQuery({ queryKey: [...key, 'search', project.currentBaselineId, query], queryFn: () => atlasApi.search(projectId, query), enabled: Boolean(query.trim()) });
  const units = useQuery({ queryKey: [...key, 'units', project.currentBaselineId, category, unitCursor], queryFn: () => atlasApi.units(projectId, { categoryKey: category, cursor: unitCursor }), enabled: Boolean(category) && !query });
  const nextUnitCursor = units.data?.nextCursor ?? undefined;
  const categoryLabel = catalog.data?.flatMap(dimension => dimension.children ?? []).find(child => child.key === category)?.label;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const value = draft.trim();
    setCategory('');
    setUnitCursors([undefined]);
    setParams(value ? { q: value } : {});
  };

  return <div className="catalog-page">
    <div className="page-heading"><div><p className="eyebrow">知识目录</p><h1>查找项目知识</h1><p>沿固定五维骨架查找业务语言、规则与实现。空分类和受限内容会清楚区分。</p></div></div>
    <form className="catalog-search" onSubmit={submit}>
      <label htmlFor="catalog-query">在当前项目中搜索</label>
      <div>
        <input id="catalog-query" value={draft} onChange={event => setDraft(event.target.value)} placeholder="功能、规则、术语或系统名称" />
        <button className="primary" disabled={!draft.trim()}>搜索知识</button>
        {query && <button type="button" className="quiet" onClick={() => { setDraft(''); setParams({}); }}>清除</button>}
      </div>
    </form>
    {query ? <section className="search-results">
      <div className="section-heading"><h2>“{query}” 的搜索结果</h2>{search.data && <span>{search.data.items.length} 项可见结果</span>}</div>
      {search.isPending ? <LoadingState label="正在搜索授权知识" /> : search.isError ? <ErrorState error={search.error} onRetry={() => search.refetch()} /> : search.data.items.length === 0 ? <EmptyState title="没有找到可见知识">尝试业务名称或别名；无权限内容不会出现在结果中。</EmptyState> : <div className="unit-results">
        {search.data.items.map(item => <Link key={item.unitId} to={`/p/${projectId}/knowledge/${item.unitId}?from=${encodeURIComponent(`/p/${projectId}/catalog?q=${encodeURIComponent(query)}`)}`}>
          <span className="kind-label">{knowledgeKindLabel(item.kind)}</span><h3>{item.title}</h3>{item.snippet && <p>{item.snippet}</p>}<span className="read-link">进入阅读 →</span>
        </Link>)}
      </div>}
    </section> : <div className="catalog-layout">
      <section className="dimension-tree" aria-label="五维知识目录">
        {catalog.isPending ? <LoadingState label="正在读取五维目录" /> : catalog.isError ? <ErrorState error={catalog.error} onRetry={() => catalog.refetch()} /> : catalog.data.map((dimension, index) => <details key={dimension.key} open={index === 0 || Boolean(category && dimension.children?.some(child => child.key === category))}>
          <summary><span>{dimension.label}</span><small>{dimension.children?.length ?? 0} 类</small></summary>
          <div>{dimension.children?.map(child => <button key={child.key} className={category === child.key ? 'active' : ''} onClick={() => { setCategory(child.key); setUnitCursors([undefined]); }}><span>{child.label}</span><small>{child.description}</small></button>)}</div>
        </details>)}
      </section>
      <section className="category-content">
        {!category ? <EmptyState title="选择一个知识类型">左侧固定五维包含项目实际扩展类型；选择后可浏览该分类下的可见知识。</EmptyState> : units.isPending ? <LoadingState /> : units.isError ? <ErrorState error={units.error} onRetry={() => units.refetch()} /> : units.data.items.length === 0 ? <EmptyState title="这个分类暂时没有可见知识">可以返回其他分类，或从资料接入建立真实知识。空分类不等于存在受限对象。</EmptyState> : <>
          <div className="section-heading"><h2>{categoryLabel ?? '所选知识'}</h2><span>本页 {units.data.items.length} 项{units.data.nextCursor ? ' · 还有更多' : ''}</span></div>
          <div className="unit-results">{units.data.items.map(item => <Link key={item.unitId} to={`/p/${projectId}/knowledge/${item.unitId}?from=${encodeURIComponent(`/p/${projectId}/catalog`)}`}>
            <span className="kind-label">{knowledgeKindLabel(item.kind)}</span><h3>{item.title}</h3><p>{factKindLabel(item.factKind)} · {verificationLabel(item.verification)}</p><span className="read-link">进入阅读 →</span>
          </Link>)}</div>
          {(unitCursors.length > 1 || nextUnitCursor) && <nav className="catalog-pager" aria-label="知识列表分页">
            <button type="button" className="quiet" disabled={unitCursors.length < 2} onClick={() => setUnitCursors(current => current.slice(0, -1))}>上一页</button>
            <span>第 {unitCursors.length} 页</span>
            <button type="button" className="quiet" disabled={!nextUnitCursor} onClick={() => nextUnitCursor && setUnitCursors(current => [...current, nextUnitCursor])}>下一页</button>
          </nav>}
        </>}
      </section>
    </div>}
  </div>;
}
