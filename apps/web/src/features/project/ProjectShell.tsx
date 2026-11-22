import { useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, NavLink, Outlet, useNavigate, useParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import type { Project, ReadingView } from '../../shared/api';
import { useSession } from '../../shared/session';
import { ErrorState, LoadingState } from '../../shared/states';
import AssistantPanel from '../assistant/AssistantPanel';

export type ProjectOutletContext = { project: Project; overview?: ReadingView };
const entries = [
  ['overview', '项目概览', '理解项目与核心能力'],
  ['catalog', '知识目录', '按五维目录浏览'],
  ['business', '业务与系统', '从功能与系统阅读'],
  ['review', '变更与审核', '处理待审成果'],
  ['tasks', '任务与问题', '提出需求与观察进展'],
  ['delivery', '交付与验收', '核对真实交付状态'],
] as const;

export default function ProjectShell() {
  const { projectId = '' } = useParams();
  const session = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [menuOpen, setMenuOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [assistantOpen, setAssistantOpen] = useState(false);
  const [query, setQuery] = useState('');
  const searchButton = useRef<HTMLButtonElement>(null);
  const closeSearch = () => { setSearchOpen(false); requestAnimationFrame(() => searchButton.current?.focus()); };
  const projects = useQuery({ queryKey: [session.principalId, 'projects'], queryFn: atlasApi.projects });
  const project = projects.data?.items.find(item => item.projectId === projectId);
  const overview = useQuery({
    queryKey: [session.principalId, projectId, project?.policyEpoch ?? 0, 'overview', project?.currentBaselineId],
    queryFn: () => atlasApi.overview(projectId), enabled: Boolean(project), retry: false,
  });
  const chooseProject = (nextId: string) => {
    if (!nextId || nextId === projectId) return;
    queryClient.removeQueries({ predicate: item => item.queryKey[0] === session.principalId && item.queryKey[1] !== 'projects' && item.queryKey[1] !== nextId });
    setMenuOpen(false); setSearchOpen(false); setQuery('');
    navigate(`/p/${nextId}/overview`);
  };
  const submitSearch = (event: FormEvent) => {
    event.preventDefault(); if (!query.trim()) return;
    navigate(`/p/${projectId}/catalog?q=${encodeURIComponent(query.trim())}`); closeSearch();
  };

  return <div className="app-shell">
    <aside className={menuOpen ? 'sidebar open' : 'sidebar'}>
      <Link className="brand" to={`/p/${projectId}/overview`}><span className="brand-mark">BA</span><span>Business Atlas<small>业务认知工作空间</small></span></Link>
      <div className="workspace-label">当前工作空间</div>
      <select className="shell-project-select" aria-label="切换项目" value={projectId} onChange={event => chooseProject(event.target.value)}>{projects.data?.items.map(item => <option key={item.projectId} value={item.projectId}>{item.displayName}</option>)}</select>
      <nav className="primary-nav" aria-label="项目主导航">{entries.map(([path, label, description]) => <NavLink key={path} to={`/p/${projectId}/${path}`} onClick={() => setMenuOpen(false)}><span>{label}</span><small>{description}</small></NavLink>)}</nav>
      <div className="sidebar-foot"><Link to={`/p/${projectId}/imports`}>资料接入与导入历史 <span>↗</span></Link><Link to="/projects">全部项目</Link></div>
    </aside>
    {menuOpen && <button className="nav-scrim" aria-label="关闭导航" onClick={() => setMenuOpen(false)} />}
    <div className="shell-content">
      <header className="shell-header"><button className="menu-button" aria-label="打开导航" onClick={() => setMenuOpen(true)}>☰</button><div><strong>{project?.displayName ?? '项目工作空间'}</strong><small>{project?.slug}</small></div><button ref={searchButton} className="search-trigger" onClick={() => setSearchOpen(true)}><span>⌕</span>搜索业务知识<kbd>/</kbd></button><span className="context-chip">正式发布</span><span className="user"><i />{session.displayName}<small>已认证会话</small></span></header>
      <main className="route-main">
        {projects.isPending ? <LoadingState label="正在确认项目访问权限" /> : projects.isError ? <ErrorState error={projects.error} onRetry={() => projects.refetch()} /> : !project ? <ErrorState error={new Error('项目不存在，或当前账号无权访问。')} /> : overview.isError ? <ErrorState error={overview.error} onRetry={() => overview.refetch()} /> : <Outlet context={{ project, overview: overview.data } satisfies ProjectOutletContext} />}
      </main>
    </div>
    {project && <button className="assistant-launch" aria-expanded={assistantOpen} aria-controls="project-assistant" onClick={() => setAssistantOpen(value => !value)}><span aria-hidden="true">AI</span>{assistantOpen ? '收起项目助手' : '询问项目助手'}</button>}
    {project && <AssistantPanel project={project} open={assistantOpen} onClose={() => setAssistantOpen(false)} />}
    {searchOpen && <div className="modal-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) closeSearch(); }}><section className="search-dialog" role="dialog" aria-modal="true" aria-labelledby="search-title"><div className="dialog-head"><div><p className="eyebrow">当前项目内搜索</p><h2 id="search-title">找到功能、规则或业务名词</h2></div><button className="icon-button" aria-label="关闭搜索" onClick={closeSearch}>×</button></div><form onSubmit={submitSearch}><label htmlFor="global-search">搜索词</label><div className="search-input-row"><input id="global-search" autoFocus value={query} onChange={event => setQuery(event.target.value)} placeholder="输入业务名称，无需内部 ID" /><button className="primary" disabled={!query.trim()}>搜索</button></div></form><p>搜索只返回当前项目、当前账号有权阅读的内容。</p></section></div>}
  </div>;
}
