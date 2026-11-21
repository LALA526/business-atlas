import { useQuery } from '@tanstack/react-query';
import { Link, useOutletContext, useParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import type { Schema } from '../../shared/api';
import { useSession } from '../../shared/session';
import { EmptyState } from '../../shared/states';
import type { ProjectOutletContext } from './ProjectShell';

const kindLabels: Record<string, string> = { ProjectProfile: '项目定位与边界', Feature: '核心业务能力', Component: '业务系统与组件', DomainTerm: '关键业务语言', BusinessRule: '重要业务规则', Rule: '重要业务规则' };
const orderedKinds = ['ProjectProfile', 'Feature', 'Component', 'DomainTerm', 'BusinessRule', 'Rule'];

export function ContentBlocks({ blocks, projectId }: { blocks: Schema<'ContentBlock'>[]; projectId: string }) {
  return <div className="content-blocks">{blocks.map(block => <div className={`content-block ${block.kind}`} key={block.blockId}>
    {block.text && <p>{block.text}</p>}
    {block.items && <ul>{block.items.map((item, index) => <li key={index}>{item}</li>)}</ul>}
    {block.columns && block.rows && <div className="table-scroll"><table><thead><tr>{block.columns.map(column => <th key={column}>{column}</th>)}</tr></thead><tbody>{block.rows.map((row, index) => <tr key={index}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}</tr>)}</tbody></table></div>}
    {block.references.length > 0 && <div className="citations"><span>深入阅读：</span>{block.references.map(ref => <Link key={`${ref.unitId}-${ref.revisionId}`} to={`/p/${projectId}/knowledge/${ref.unitId}`}>{ref.unitId.slice(0, 8)}<span aria-hidden="true"> ↗</span></Link>)}</div>}
  </div>)}</div>;
}

export default function OverviewPage() {
  const { project, overview } = useOutletContext<ProjectOutletContext>();
  const { projectId = '' } = useParams();
  const session = useSession();
  const units = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'units', project.currentBaselineId, 'overview'], queryFn: () => atlasApi.units(projectId), enabled: Boolean(overview) });
  if (!overview) return <div className="loading-state" role="status"><span className="spinner" />正在组合项目定位与核心知识</div>;
  const sections = [...overview.sections].sort((a, b) => {
    const ai = orderedKinds.indexOf(a.key); const bi = orderedKinds.indexOf(b.key);
    return (ai < 0 ? 99 : ai) - (bi < 0 ? 99 : bi);
  });
  const features = overview.sections.find(section => section.key === 'Feature');
  return <div className="overview-page">
    <section className="overview-hero"><div><p className="eyebrow">PROJECT OVERVIEW</p><h1>{project.displayName}</h1><p className="lede">从这里理解项目的业务定位、核心能力与系统，不必先知道内部知识 ID。</p><div className="hero-actions">{features?.blocks.length ? <a className="primary link-button" href="#capabilities">开始了解核心能力 <span>↓</span></a> : <Link className="primary link-button" to={`/p/${projectId}/imports`}>接入第一批真实资料 <span>→</span></Link>}<Link className="text-link" to={`/p/${projectId}/catalog`}>浏览五维知识目录 →</Link></div></div><dl className="context-summary"><div><dt>阅读范围</dt><dd>已发布知识</dd></div><div><dt>项目标识</dt><dd>{project.slug}</dd></div><div><dt>基线</dt><dd title={overview.context.baselineId}>{overview.context.baselineId.slice(0, 8)}</dd></div><div><dt>内容完整性</dt><dd>{overview.completeness === 'complete' ? '当前授权范围完整' : '部分内容，可继续深入'}</dd></div></dl></section>
    {overview.unknowns.length > 0 && <section className="unknown-panel"><div><p className="eyebrow">需要注意</p><h2>重要未知与待确认</h2></div><div>{overview.unknowns.slice(0, 6).map(item => <article key={item.key}><span className={`severity ${item.severity}`}>{item.severity === 'blocking' ? '阻断' : item.severity === 'warning' ? '待确认' : '提示'}</span><p>{item.description}</p>{item.nextAction && <small>{item.nextAction}</small>}</article>)}</div></section>}
    {units.data && units.data.items.length > 0 && <section className="recommended-reading"><div className="section-heading"><div><p className="eyebrow">推荐起点</p><h2>直接深入一个业务主题</h2></div><span>按名称选择，无需 ID</span></div><div className="unit-results">{units.data.items.filter(item => item.kind !== 'ProjectProfile').slice(0, 6).map(item => <Link key={item.unitId} to={`/p/${projectId}/knowledge/${item.unitId}?from=${encodeURIComponent(`/p/${projectId}/overview`)}`}><span className="kind-label">{item.kind}</span><h3>{item.title}</h3>{typeof item.body.purpose === 'string' && <p>{item.body.purpose}</p>}<span className="read-link">阅读相关规则、设计与来源 →</span></Link>)}</div></section>}
    {sections.length === 0 ? <EmptyState title="这是一个真实空白项目" action={<Link className="primary link-button" to={`/p/${projectId}/imports`}>导入资料</Link>}>当前没有已发布业务知识。你可以从真实资料开始；系统不会为空项目生成虚构定位或能力。</EmptyState> : <div className="overview-sections">{sections.map(section => <section id={section.key === 'Feature' ? 'capabilities' : undefined} key={section.key} className="overview-section"><div className="section-heading"><p className="eyebrow">{section.key}</p><h2>{kindLabels[section.key] ?? section.title}</h2><span>{section.blocks.length} 段可读内容</span></div><ContentBlocks blocks={section.blocks} projectId={projectId} />{section.children && section.children.length > 0 && <div className="subsection-links">{section.children.map(child => <Link key={child.key} to={`/p/${projectId}/knowledge/${child.key}`}>{child.title}<span>进入阅读 →</span></Link>)}</div>}</section>)}</div>}
  </div>;
}
