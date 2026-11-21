import { useState } from 'react';
import type { FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router-dom';
import { atlasApi } from '../shared/api';
import { useSession } from '../shared/session';
import { EmptyState, ErrorState, LoadingState } from '../shared/states';

export default function ProjectsPage() {
  const session = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const projects = useQuery({ queryKey: [session.principalId, 'projects'], queryFn: atlasApi.projects });
  const [creating, setCreating] = useState(false);
  const [displayName, setDisplayName] = useState('');
  const [slug, setSlug] = useState('');
  const create = useMutation({
    mutationFn: () => atlasApi.createProject(displayName.trim(), slug.trim()),
    onSuccess: async project => {
      await queryClient.invalidateQueries({ queryKey: [session.principalId, 'projects'] });
      navigate(`/p/${project.projectId}/overview`);
    },
  });
  const submit = (event: FormEvent) => { event.preventDefault(); if (displayName.trim() && slug.trim()) create.mutate(); };

  return <div className="projects-page">
    <header className="public-header"><Link className="brand" to="/projects"><span className="brand-mark">BA</span><span>Business Atlas<small>业务认知工作空间</small></span></Link><span className="user"><i />{session.displayName}</span></header>
    <main className="projects-main">
      <p className="eyebrow">你的业务工作空间</p><h1>从真实项目开始理解。</h1>
      <p className="lede">每个项目拥有独立的知识、权限与版本历史。选择项目，或创建一个不含虚构业务内容的空白空间。</p>
      {projects.isPending ? <LoadingState label="正在读取可访问项目" /> : projects.isError ? <ErrorState error={projects.error} onRetry={() => projects.refetch()} /> : <>
        <div className="project-list-head"><h2>可访问项目 <span>{projects.data.items.length}</span></h2><button className="secondary" onClick={() => setCreating(value => !value)}>{creating ? '取消' : '新建空白项目'}</button></div>
        {creating && <form className="create-project" onSubmit={submit}><div><label htmlFor="project-name">项目名称</label><input id="project-name" autoFocus required maxLength={200} value={displayName} onChange={event => setDisplayName(event.target.value)} placeholder="例如：实际目标项目名称" /></div><div><label htmlFor="project-slug">唯一标识</label><input id="project-slug" required pattern="[a-z0-9][a-z0-9-]*" maxLength={80} value={slug} onChange={event => setSlug(event.target.value.toLowerCase())} placeholder="project-slug" /></div><button className="primary" disabled={create.isPending || !displayName.trim() || !slug.trim()}>{create.isPending ? '正在创建…' : '创建并进入'}</button>{create.isError && <ErrorState error={create.error} />}</form>}
        {projects.data.items.length === 0 ? <EmptyState title="还没有可访问项目">创建空白项目后，可通过资料接入或需求入口逐步建立知识。</EmptyState> : <div className="project-cards">{projects.data.items.map(project => <Link key={project.projectId} to={`/p/${project.projectId}/overview`} className="project-card"><span className="project-monogram">{project.displayName.slice(0, 1).toUpperCase()}</span><div><h3>{project.displayName}</h3><p>{project.slug}</p><small>当前发布基线 · {project.currentBaselineId.slice(0, 8)}</small></div><span aria-hidden="true">→</span></Link>)}</div>}
      </>}
    </main>
  </div>;
}
