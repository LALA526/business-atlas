import { useQuery } from '@tanstack/react-query';
import { Link, useOutletContext, useParams, useSearchParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import { useSession } from '../../shared/session';
import { ErrorState, LoadingState } from '../../shared/states';
import { ContentBlocks } from './OverviewPage';
import type { ProjectOutletContext } from './ProjectShell';

export default function KnowledgePage() {
  const { project } = useOutletContext<ProjectOutletContext>();
  const { projectId = '', unitId = '' } = useParams();
  const [params] = useSearchParams();
  const session = useSession();
  const reading = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'reading', project.currentBaselineId, unitId], queryFn: () => atlasApi.reading(projectId, unitId), retry: false });
  const from = params.get('from');
  const back = from?.startsWith(`/p/${projectId}/`) ? from : `/p/${projectId}/business`;
  return <div className="knowledge-page"><Link className="back-link" to={back}>← 返回原阅读位置</Link>{reading.isPending ? <LoadingState label="正在组合局部知识、规则与来源" /> : reading.isError ? <ErrorState error={reading.error} onRetry={() => reading.refetch()} /> : <><header className="knowledge-heading"><p className="eyebrow">局部知识阅读</p><h1>{reading.data.heading}</h1><p>{reading.data.purpose === 'understand' ? '围绕当前主题组合必要内容，不展开整个项目。' : reading.data.purpose}</p><div><span>已发布基线 {reading.data.context.baselineId.slice(0, 8)}</span><span>{reading.data.completeness === 'complete' ? '完整阅读' : '部分内容'}</span></div></header>{reading.data.unknowns.length > 0 && <section className="reader-unknowns"><h2>需要关注</h2>{reading.data.unknowns.map(item => <article key={item.key}><strong>{item.description}</strong>{item.nextAction && <p>{item.nextAction}</p>}</article>)}</section>}<div className="reader-sections">{reading.data.sections.map(section => <section id={section.key} key={section.key}><h2>{section.title}</h2><ContentBlocks blocks={section.blocks} projectId={projectId} />{section.children?.map(child => <div className="reader-child" key={child.key}><h3>{child.title}</h3><ContentBlocks blocks={child.blocks} projectId={projectId} /></div>)}</section>)}</div></>}</div>;
}
