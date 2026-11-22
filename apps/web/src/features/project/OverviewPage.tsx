import { useQuery } from '@tanstack/react-query';
import { Link, useOutletContext, useParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import type { Schema } from '../../shared/api';
import { useSession } from '../../shared/session';
import { EmptyState } from '../../shared/states';
import type { ProjectOutletContext } from './ProjectShell';
import { knowledgeKindLabel } from './knowledgeLabels';

const kindLabels: Record<string, string> = { ProjectProfile: '项目定位与边界', Feature: '核心业务能力', Component: '业务系统与组件', DomainTerm: '关键业务语言', BusinessRule: '重要业务规则', Rule: '重要业务规则', Design: '设计依据', Observation: '实际观察', Architecture: '技术架构' };
const orderedKinds = ['ProjectProfile', 'Feature', 'Component', 'DomainTerm', 'BusinessRule', 'Rule'];
const overviewKinds = new Set(['Feature', 'Component', 'DomainTerm', 'BusinessRule', 'Rule', 'Design', 'Observation', 'Architecture']);

export function ContentBlocks({ blocks, projectId, unitTitles = new Map<string, string>() }: { blocks: Schema<'ContentBlock'>[]; projectId: string; unitTitles?: ReadonlyMap<string, string> }) {
  return <div className="content-blocks">{blocks.map(block => <div className={`content-block ${block.kind}`} key={block.blockId}>
    {block.text && <p>{block.text}</p>}
    {block.items && <ul>{block.items.map((item, index) => <li key={index}>{item}</li>)}</ul>}
    {block.columns && block.rows && <div className="table-scroll"><table><thead><tr>{block.columns.map(column => <th key={column}>{column}</th>)}</tr></thead><tbody>{block.rows.map((row, index) => <tr key={index}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}</tr>)}</tbody></table></div>}
    {block.references.length > 0 && <div className="citations"><span>深入阅读：</span>{block.references.map(ref => <Link key={`${ref.unitId}-${ref.revisionId}`} to={`/p/${projectId}/knowledge/${ref.unitId}`}>{unitTitles.get(ref.unitId) ?? '打开关联知识'}<span aria-hidden="true"> ↗</span></Link>)}</div>}
  </div>)}</div>;
}

export default function OverviewPage() {
  const { project, overview } = useOutletContext<ProjectOutletContext>();
  const { projectId = '' } = useParams();
  const session = useSession();
  const units = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'units', project.currentBaselineId, 'overview-features'], queryFn: () => atlasApi.units(projectId, { kind: 'Feature' }), enabled: Boolean(overview) });
  const profileUnits = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'units', project.currentBaselineId, 'overview-profile'], queryFn: () => atlasApi.units(projectId, { kind: 'ProjectProfile' }), enabled: Boolean(overview) });
  const changeSets = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'change-sets', 'overview'], queryFn: () => atlasApi.changeSets(projectId), enabled: Boolean(project) });
  if (!overview) return <div className="loading-state" role="status"><span className="spinner" />正在组合项目定位与核心知识</div>;
  const sections = [...overview.sections].sort((a, b) => {
    const ai = orderedKinds.indexOf(a.key); const bi = orderedKinds.indexOf(b.key);
    return (ai < 0 ? 99 : ai) - (bi < 0 ? 99 : bi);
  });
  const profile = sections.find(section => section.key === 'ProjectProfile');
  const profileRevision = profileUnits.data?.items[0];
  const contentSections = sections.filter(section => overviewKinds.has(section.key));
  const pendingReviews = changeSets.data?.items.filter(item => ['submitted', 'in_review', 'changes_requested'].includes(item.state)) ?? [];
  const unitTitles = new Map((units.data?.items ?? []).map(item => [item.unitId, item.title]));
  return <div className="overview-page">
    <header className="overview-heading"><div><p className="eyebrow">项目概览 / PROJECT OVERVIEW</p><h1>{project.displayName}</h1><p className="lede">{overview.purpose || overview.heading}</p></div><Link className="overview-catalog-link" to={`/p/${projectId}/catalog`}>项目目录 <span aria-hidden="true">→</span></Link></header>
    <div className="overview-start-grid">
      <section className="overview-profile" aria-labelledby="project-profile-title">
        <p className="overview-section-label">项目定位</p>
        <div className="overview-profile-content">{profile ? <ContentBlocks blocks={profile.blocks} projectId={projectId} unitTitles={unitTitles} /> : profileRevision ? <ContentBlocks blocks={profileRevision.blocks} projectId={projectId} unitTitles={unitTitles} /> : <p className="overview-empty-copy">当前授权的已发布基线没有项目定位内容。</p>}</div>
        {profile?.children && profile.children.length > 0 && <div className="overview-profile-links">{profile.children.slice(0, 4).map(child => <Link key={child.key} to={`/p/${projectId}/knowledge/${child.key}`}>{child.title}<span aria-hidden="true">→</span></Link>)}</div>}
        <h2 id="project-profile-title" className="sr-only">项目定位</h2>
      </section>
      <section className="overview-work-entry" aria-labelledby="overview-work-title">
        <p className="overview-section-label">项目协作</p><h2 id="overview-work-title">从当前工作继续</h2>
        <Link className="overview-work-link" to={`/p/${projectId}/review`}><span><strong>变更与审核</strong><small>{changeSets.isPending ? '正在读取审核队列' : changeSets.isError ? '审核队列暂不可用' : changeSets.data?.nextCursor ? `至少 ${pendingReviews.length} 项待处理变更` : `${pendingReviews.length} 项待处理变更`}</small></span><span aria-hidden="true">→</span></Link>
        <Link className="overview-work-link" to={`/p/${projectId}/tasks`}><span><strong>任务与问题</strong><small>查看目标、调查结论和下一步</small></span><span aria-hidden="true">→</span></Link>
      </section>
    </div>
    {overview.unknowns.length > 0 && <section className="unknown-panel"><div><p className="eyebrow">需要注意</p><h2>重要未知与待确认</h2></div><div>{overview.unknowns.slice(0, 6).map(item => <article key={item.key}><span className={`severity ${item.severity}`}>{item.severity === 'blocking' ? '阻断' : item.severity === 'warning' ? '待确认' : '提示'}</span><p>{item.description}</p>{item.nextAction && <small>{item.nextAction}</small>}</article>)}</div></section>}
    {units.data && units.data.items.filter(item => item.kind !== 'ProjectProfile').length > 0 && <section className="recommended-reading"><div className="section-heading"><div><p className="eyebrow">推荐理解入口</p><h2>从重要知识开始</h2></div></div><div className="recommended-list">{units.data.items.filter(item => item.kind !== 'ProjectProfile').slice(0, 6).map(item => <Link key={item.unitId} to={`/p/${projectId}/knowledge/${item.unitId}?from=${encodeURIComponent(`/p/${projectId}/overview`)}`}><span className="kind-label">{knowledgeKindLabel(item.kind)}</span><strong>{item.title}</strong><span className="read-link" aria-hidden="true">→</span></Link>)}</div></section>}
    {contentSections.length === 0 && !units.data?.items.length ? <EmptyState title="当前基线还没有可阅读的项目知识" action={<Link className="primary link-button" to={`/p/${projectId}/imports`}>接入项目资料</Link>}>系统不会用演示内容填充真实项目。接入资料后，审核并发布的知识会显示在这里。</EmptyState> : <div className="overview-sections">{contentSections.map(section => <section key={section.key} className="overview-section"><div className="section-heading"><h2>{kindLabels[section.key] ?? section.title}</h2></div><ContentBlocks blocks={section.blocks} projectId={projectId} unitTitles={unitTitles} />{section.children && section.children.length > 0 && <div className="subsection-links">{section.children.map(child => <Link key={child.key} to={`/p/${projectId}/knowledge/${child.key}`}>{child.title}<span>进入阅读 →</span></Link>)}</div>}</section>)}</div>}
  </div>;
}
