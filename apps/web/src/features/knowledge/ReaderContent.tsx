import { Link, useLocation } from 'react-router-dom';
import type { Schema } from '../../shared/api';

type Section = Schema<'Section'>;
type Unknown = Schema<'Unknown'>;

export const unknownReasons: Record<Unknown['reason'], string> = {
  missing: '未记录', conflict: '存在冲突', insufficient: '信息不足', unverified: '尚未核验',
  stale: '依据已过期', not_applicable: '当前不适用', restricted_context: '当前上下文不可见',
};

export function UnknownList({ items }: { items: Unknown[] }) {
  if (!items.length) return null;
  return <div className="reader-unknown-list">{items.map(item => <article key={item.key}>
    <div><span className={`severity ${item.severity}`}>{unknownReasons[item.reason]}</span><strong>{item.description}</strong></div>
    {item.nextAction && <p>{item.nextAction}</p>}
  </article>)}</div>;
}

function ContentBlocks({ blocks, projectId, onSources }: { blocks: Schema<'ContentBlock'>[]; projectId: string; onSources: () => void }) {
  const location = useLocation();
  const current = `${location.pathname}${location.search}${location.hash}`;
  if (!blocks.length) return <p className="reader-empty">当前阅读上下文没有已发布内容；这里不会根据代码或相似名称补造业务事实。</p>;
  return <div className="content-blocks">{blocks.map(block => <div className={`content-block ${block.kind}`} id={block.blockId} key={block.blockId}>
    {block.text && <p>{block.text}</p>}
    {block.items && <ul>{block.items.map((item, index) => <li key={index}>{item}</li>)}</ul>}
    {block.columns && block.rows && <div className="table-scroll"><table><thead><tr>{block.columns.map((column, index) => <th key={`${column}-${index}`} scope="col">{column}</th>)}</tr></thead><tbody>{block.rows.map((row, index) => <tr key={index}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}</tr>)}</tbody></table></div>}
    {block.references.length > 0 && <div className="citations"><span>同版本依据：</span>{block.references.map(ref => <Link key={`${ref.unitId}-${ref.revisionId}`} to={`/p/${projectId}/knowledge/${ref.unitId}?from=${encodeURIComponent(current)}&focus=${encodeURIComponent(block.blockId)}`}>查看相关知识<span aria-hidden="true"> ↗</span></Link>)}<button className="citation-source" onClick={onSources}>查看当前来源</button></div>}
  </div>)}</div>;
}

export function ReaderSection({ section, projectId, onSources }: { section: Section; projectId: string; onSources: () => void }) {
  return <section className="reader-section" id={section.key} aria-labelledby={`${section.key}-title`}>
    <h2 id={`${section.key}-title`}>{section.title}</h2>
    <ContentBlocks blocks={section.blocks} projectId={projectId} onSources={onSources} />
    {section.children && section.children.length > 0 && <div className="reader-children">{section.children.map(child => section.blocks.length === 0 && child.blocks.length === 0 && child.expandRef ?
      <Link className="reader-related-link" key={child.key} to={`/p/${projectId}/knowledge/${child.expandRef.split('/units/')[1]?.split('/')[0] ?? ''}`}>{child.title}<span>进入阅读 →</span></Link> :
      <details className="reader-child" key={child.key}><summary>{child.title}<span>展开阶段、条件与后继</span></summary><ContentBlocks blocks={child.blocks} projectId={projectId} onSources={onSources} /></details>)}</div>}
  </section>;
}
