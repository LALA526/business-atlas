import { useMemo, useState } from 'react';
import type { FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import rawData from './self-project.json';
import './self-project.css';

const pages = [
  ['overview', '项目概览'], ['design', '产品设计'], ['knowledge', '知识与系统'],
  ['requirements', '需求与任务'], ['delivery', '变更与交付'], ['sources', '来源与证据'],
] as const;

const repoUrl = 'https://github.com/LALA526/business-atlas';
const pageSize = 20;

type SelfProjectData = {
  project: { name: string; purpose: string; audience: string; boundaries: string[] };
  counts: { requirements: number; tasks: number; features: number };
  requirements: { id: string; title: string; priority: string; behavior: string; acceptance: string; status: string; sourcePath: string }[];
  tasks: { id: string; title: string; status: string; implementation: string; acceptance: string; dependsOn: string[]; requirementIds: string[]; ownerRole: string }[];
  features: { id: string; title: string; requirementId: string; summary: string; sourcePaths: string[] }[];
  design: { productEvolution: { title: string; sourcePath: string }[]; releaseHistory: { title: string; sourcePath: string }[] };
  documents: { path: string; content: string }[];
  sources: { path: string; revision: string; contentHash: string; uri: string }[];
  provenance: { sourceRevision: string; generatedAt: string; sourceScope: string };
};

const data = rawData as SelfProjectData;

function SourceLink({ path }: { path: string }) {
  return <a className="self-source-link" href={`${repoUrl}/blob/main/${path.split('/').map(encodeURIComponent).join('/')}`} target="_blank" rel="noreferrer">{path}<span aria-hidden="true"> ↗</span></a>;
}

function TablePager({ page, pagesCount, onPage }: { page: number; pagesCount: number; onPage: (page: number) => void }) {
  if (pagesCount < 2) return null;
  return <nav className="self-pager" aria-label="分页">
    <button type="button" disabled={page === 0} onClick={() => onPage(page - 1)}>上一页</button>
    <span>{page + 1} / {pagesCount}</span>
    <button type="button" disabled={page + 1 >= pagesCount} onClick={() => onPage(page + 1)}>下一页</button>
  </nav>;
}

export default function SelfProjectPage() {
  const [params, setParams] = useSearchParams();
  const activePage = pages.some(([key]) => key === params.get('view')) ? params.get('view')! : 'overview';
  const query = params.get('q') ?? '';
  const [draft, setDraft] = useState(query);
  const [requirementsPage, setRequirementsPage] = useState(0);
  const [tasksPage, setTasksPage] = useState(0);
  const [catalogKind, setCatalogKind] = useState<'all' | 'feature' | 'source'>('all');

  const filteredRequirements = useMemo(() => {
    const value = query.trim().toLocaleLowerCase();
    return data.requirements.filter(item => !value || `${item.id} ${item.title} ${item.behavior} ${item.acceptance}`.toLocaleLowerCase().includes(value));
  }, [query]);
  const filteredTasks = useMemo(() => {
    const value = query.trim().toLocaleLowerCase();
    return data.tasks.filter(item => !value || `${item.id} ${item.title} ${item.implementation} ${item.acceptance} ${item.requirementIds.join(' ')}`.toLocaleLowerCase().includes(value));
  }, [query]);
  const setView = (view: string) => setParams(current => { current.set('view', view); return current; });
  const search = (event: FormEvent) => {
    event.preventDefault();
    setRequirementsPage(0);
    setTasksPage(0);
    setParams(current => { current.set('view', 'knowledge'); if (draft.trim()) current.set('q', draft.trim()); else current.delete('q'); return current; });
  };
  const requirementPages = Math.ceil(filteredRequirements.length / pageSize);
  const taskPages = Math.ceil(filteredTasks.length / pageSize);

  return <div className="self-public">
    <header className="self-topbar">
      <a className="self-brand" href="/public/self"><span className="self-brand-mark">A</span><strong>Business Atlas</strong><small>Self Project</small></a>
      <div className="self-project-name"><span className="self-live-dot" /><strong>Business Atlas — 产品设计与交付</strong><small>BusinessAtlas-Self</small></div>
      <form className="self-search" onSubmit={search}><label className="sr-only" htmlFor="self-search">搜索本项目</label><input id="self-search" value={draft} onChange={event => setDraft(event.target.value)} placeholder="搜索需求、设计或实现" /><button type="submit" aria-label="搜索">⌕</button></form>
      <span className="self-readonly">公开只读</span>
      <a className="self-manage-link" href="/projects">登录管理</a>
    </header>

    <aside className="self-sidebar">
      {pages.map(([key, label], index) => <div key={key}>
        {index === 0 && <p className="self-nav-label">理解项目</p>}
        {index === 2 && <p className="self-nav-label">阅读知识</p>}
        {index === 3 && <p className="self-nav-label">追踪交付</p>}
        <button type="button" className={activePage === key ? 'active' : ''} onClick={() => setView(key)}>{label}</button>
      </div>)}
      <div className="self-sidebar-status"><span>知识状态</span><strong>源证据只读视图</strong><small>不代表完整 T1.0 验收</small></div>
    </aside>

    <main className="self-main">
      {activePage === 'overview' && <>
        <header className="self-page-heading"><div><p className="self-eyebrow">项目概览 / SELF PROJECT</p><h1>{data.project.name}</h1><p>{data.project.purpose}</p></div><a className="self-button" href={`${repoUrl}/tree/main`}>查看源码仓库 ↗</a></header>
        <div className="self-overview-grid">
          <section className="self-profile-block"><p className="self-section-label">项目定位</p><p className="self-lede">{data.project.purpose}</p><h2>服务对象</h2><p>{data.project.audience}</p><h2>项目边界</h2><ul>{data.project.boundaries.map(item => <li key={item}>{item}</li>)}</ul></section>
          <section className="self-status-block"><p className="self-section-label">当前真实状态</p><strong className="self-status-value">T1.0 尚未完成验收</strong><p>部署成功只证明本次发布可运行，不代表 154 条需求或全部验收已完成。</p><dl><div><dt>基线需求</dt><dd>{data.counts.requirements}</dd></div><div><dt>计划任务</dt><dd>{data.counts.tasks}</dd></div><div><dt>产品能力主题</dt><dd>{data.counts.features}</dd></div></dl><SourceLink path="docs/release/t1.0-handoff/HANDOFF.md" /></section>
        </div>
        <section className="self-section"><div className="self-section-head"><div><p className="self-section-label">产品能力</p><h2>从实际产品设计开始</h2></div><button type="button" className="self-text-button" onClick={() => setView('knowledge')}>浏览知识目录 →</button></div><div className="self-feature-grid">{data.features.map(item => <article className="self-feature" key={item.id}><span>{item.requirementId}</span><h3>{item.title}</h3><p>{item.summary}</p>{item.sourcePaths[0] && <SourceLink path={item.sourcePaths[0]} />}</article>)}</div></section>
        <section className="self-section self-source-strip"><div><p className="self-section-label">设计演进</p><h2>V9.2 工作空间 · V9.3 阅读语义</h2><p>原型仅作为设计证据；演示权限、模拟审批和本地演示数据不导入为生产事实。</p></div><button type="button" className="self-text-button" onClick={() => setView('design')}>查看设计过程 →</button></section>
      </>}

      {activePage === 'design' && <>
        <PageHeading eyebrow="产品设计 / DESIGN HISTORY" title="设计依据与演进" description="区分原型设计意图、当前实现观察和仍未关闭的问题。" />
        <div className="self-timeline">{data.design.productEvolution.map((item, index) => <article key={item.title}><span className="self-timeline-index">0{index + 1}</span><div><p className="self-section-label">记录的设计方向</p><h2>{item.title}</h2><p>{index === 0 ? 'V9.2 提供项目工作空间、六个工作区与 Self Knowledge 的信息架构参考。原型内容并非本项目实际批准知识。' : 'V9.3 评审记录规则案例、应然/实然区分、任务语义和阅读路径的修正方向。该评审不是生产验收。'}</p><SourceLink path={item.sourcePath} /></div></article>)}</div>
        <section className="self-section"><div className="self-section-head"><div><p className="self-section-label">工程约束</p><h2>知识、代码与发布分开记录</h2></div></div><div className="self-boundary-grid"><p>导入只建立候选，不等于批准。</p><p>需求意图不等于当前实现观察。</p><p>PR 合并和部署不等于产品验收。</p></div><SourceLink path="docs/baselines/T1.0/01_技术实施/08_导入与工程知识交付.md" /></section>
      </>}

      {activePage === 'knowledge' && <>
        <PageHeading eyebrow="项目知识 / CATALOG" title="产品能力与工程来源" description="这些条目来自实际产品合同、源码路径和发布记录；源类型与状态会保留。" />
        <div className="self-segmented" role="tablist" aria-label="知识类型"><button role="tab" aria-selected={catalogKind === 'all'} onClick={() => setCatalogKind('all')}>全部知识</button><button role="tab" aria-selected={catalogKind === 'feature'} onClick={() => setCatalogKind('feature')}>产品能力</button><button role="tab" aria-selected={catalogKind === 'source'} onClick={() => setCatalogKind('source')}>设计文档</button></div>
        {catalogKind !== 'source' && <section className="self-feature-grid self-catalog-grid">{data.features.filter(item => !query || `${item.title} ${item.summary} ${item.requirementId}`.toLowerCase().includes(query.toLowerCase())).map(item => <article className="self-feature" key={item.id}><span>{item.requirementId} · 记录意图</span><h2>{item.title}</h2><p>{item.summary}</p>{item.sourcePaths.map(path => <SourceLink path={path} key={path} />)}</article>)}</section>}
        {catalogKind !== 'feature' && <section className="self-section"><div className="self-section-head"><div><p className="self-section-label">设计材料</p><h2>产品基线文档</h2></div></div><div className="self-doc-list">{data.documents.filter(item => !query || item.path.toLowerCase().includes(query.toLowerCase()) || item.content.toLowerCase().includes(query.toLowerCase())).map(item => <details key={item.path}><summary><strong>{item.path.split('/').at(-1)}</strong><small>{item.path}</small></summary><div className="self-doc-content"><SourceLink path={item.path} /><pre>{item.content}</pre></div></details>)}</div></section>}
      </>}

      {activePage === 'requirements' && <>
        <PageHeading eyebrow="基线跟踪 / T1.0" title="需求与任务" description="逐项保留需求合同和任务依赖。状态来自基线文件，不推断实现完成。" />
        <section className="self-section"><div className="self-section-head"><div><p className="self-section-label">{filteredRequirements.length} 条匹配</p><h2>需求追踪</h2></div><SourceLink path="docs/baselines/T1.0/03_开发执行/requirements-trace.csv" /></div>
          <div className="self-table-scroll"><table><thead><tr><th>ID / 标题</th><th>优先级</th><th>状态</th><th>行为意图</th><th>验收意图</th><th>来源</th></tr></thead><tbody>{filteredRequirements.slice(requirementsPage * pageSize, (requirementsPage + 1) * pageSize).map(item => <tr key={item.id}><td><code>{item.id}</code><strong>{item.title}</strong></td><td>{item.priority}</td><td><span className="self-state">{item.status}</span></td><td>{item.behavior}</td><td>{item.acceptance}</td><td><SourceLink path={item.sourcePath} /></td></tr>)}</tbody></table></div>
          <TablePager page={requirementsPage} pagesCount={requirementPages} onPage={setRequirementsPage} />
        </section>
        <section className="self-section"><div className="self-section-head"><div><p className="self-section-label">{filteredTasks.length} 条任务合同</p><h2>任务依赖与验收</h2></div><SourceLink path="docs/baselines/T1.0/03_开发执行/task-dag.json" /></div>
          <div className="self-task-list">{filteredTasks.slice(tasksPage * pageSize, (tasksPage + 1) * pageSize).map(item => <details key={item.id}><summary><code>{item.id}</code><strong>{item.title}</strong><span>{item.status}</span></summary><div className="self-task-detail"><p>{item.implementation}</p><p><strong>验收意图：</strong>{item.acceptance}</p><p><strong>依赖：</strong>{item.dependsOn.length ? item.dependsOn.join(', ') : '无'}</p><p><strong>需求：</strong>{item.requirementIds.join(', ') || '未映射'}</p><p><strong>Owner 角色：</strong>{item.ownerRole}</p></div></details>)}</div>
          <TablePager page={tasksPage} pagesCount={taskPages} onPage={setTasksPage} />
        </section>
      </>}

      {activePage === 'delivery' && <>
        <PageHeading eyebrow="工程观察 / RELEASE" title="变更与部署记录" description="只展示可核验的 PR 与流水线事实，不把它们描述成知识批准或完整验收。" />
        <div className="self-release-list">{data.design.releaseHistory.map(item => <article key={item.title}><span className="self-observed">实际记录</span><h2>{item.title}</h2><p>{item.sourcePath === 'knowledge/evidence/azure-pr-78.json' ? 'PR #78 已合并至 main，merge commit f95aef5。' : 'Build 103 的 Build 与 DeployMac8 阶段成功，发布至公开预览。'}</p><SourceLink path={item.sourcePath} /></article>)}</div>
        <section className="self-section self-not-ready"><p className="self-section-label">未完成</p><h2>本产品仍未完成 T1.0 验收</h2><p>当前控制交接记录 154 项需求均为 not_started、G7 为 not_started。部署成功是运行证据，不是产品完整验收。</p><SourceLink path="docs/release/t1.0-handoff/HANDOFF.md" /></section>
      </>}

      {activePage === 'sources' && <>
        <PageHeading eyebrow="来源与证据 / PROVENANCE" title="可追溯的源记录" description="每个来源绑定代码提交、Git blob 与内容 SHA-256；源码仓库是文件内容的原始位置。" />
        <section className="self-section"><div className="self-source-list">{data.sources.map(source => <article key={source.path}><SourceLink path={source.path} /><code>{source.contentHash}</code><small>Git {source.revision.slice(0, 12)} · {source.uri}</small></article>)}</div></section>
        <p className="self-provenance">生成时间：{new Date(data.provenance.generatedAt).toLocaleString('zh-CN')} · 源提交：{data.provenance.sourceRevision} · {data.provenance.sourceScope}</p>
      </>}
    </main>
  </div>;
}

function PageHeading({ eyebrow, title, description }: { eyebrow: string; title: string; description: string }) {
  return <header className="self-page-heading"><div><p className="self-eyebrow">{eyebrow}</p><h1>{title}</h1><p>{description}</p></div></header>;
}
