import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useOutletContext, useParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import { useSession } from '../../shared/session';
import { EmptyState, ErrorState, LoadingState } from '../../shared/states';
import type { ProjectOutletContext } from './ProjectShell';

const views = [{ key: 'Feature', label: '功能与场景', description: '从使用者目标和业务结果开始' }, { key: 'Component', label: '系统与组件', description: '从实际系统职责与契约开始' }] as const;

export default function BusinessPage() {
  const { project } = useOutletContext<ProjectOutletContext>();
  const { projectId = '' } = useParams();
  const session = useSession();
  const [view, setView] = useState<'Feature' | 'Component'>('Feature');
  const units = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'units', project.currentBaselineId, view], queryFn: () => atlasApi.units(projectId, { kind: view }) });
  return <div className="business-page"><div className="page-heading"><p className="eyebrow">BUSINESS & SYSTEMS</p><h1>业务与系统</h1><p>选择你熟悉的业务入口，再按需阅读相关规则、设计与来源。</p></div><div className="segmented" role="tablist" aria-label="业务阅读入口">{views.map(item => <button role="tab" aria-selected={view === item.key} className={view === item.key ? 'active' : ''} key={item.key} onClick={() => setView(item.key)}><strong>{item.label}</strong><small>{item.description}</small></button>)}</div>{units.isPending ? <LoadingState /> : units.isError ? <ErrorState error={units.error} onRetry={() => units.refetch()} /> : units.data.items.length === 0 ? <EmptyState title={`暂无已发布的${view === 'Feature' ? '功能或场景' : '系统或组件'}`}>这里不会展示其他项目内容或虚构入口。可先通过资料接入建立候选，经审核发布后阅读。</EmptyState> : <div className="business-list">{units.data.items.map(item => <Link key={item.unitId} to={`/p/${projectId}/knowledge/${item.unitId}?from=${encodeURIComponent(`/p/${projectId}/business`)}`}><span className="kind-label">{item.kind}</span><h2>{item.title}</h2>{typeof item.body.purpose === 'string' && <p>{item.body.purpose}</p>}<div><span>{item.factKind === 'normative' ? '业务规范' : item.factKind === 'observed' ? '实际观察' : item.factKind}</span><span>进入局部阅读 →</span></div></Link>)}</div>}</div>;
}
