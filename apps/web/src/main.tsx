import { StrictMode, useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { createRoot } from 'react-dom/client';
import './styles.css';

type Project = { projectId: string; displayName: string; slug: string; currentBaselineId: string };
type ImportEntry = { entryId: string; externalId: string; kind: string; status: string; summary: string; decision: string | null; unitId: string | null };
type ImportBatch = { importId: string; packageId: string; baseBaselineId: string; state: string; version: number; entries: ImportEntry[]; changeSetId: string | null; unknowns: { code: string; message: string; externalId: string | null }[] };
type Revision = { unitId: string; revisionId: string; title: string; kind: string; factKind: string; verification: string; body: Record<string, unknown> };
type Envelope<T> = { data: T; code: string; message?: string };

const decisions = ['reuse_existing', 'new_version', 'new_identity', 'reject', 'needs_evidence'];
const statusLabels: Record<string, string> = { new: '新增', updated: '修订候选', duplicate: '内容等价', conflict: '冲突', invalid: '无效' };
const stateLabels: Record<string, string> = { preview_ready: '等待确认', decided: '已记录决策', staged: '已写入草稿', discarded: '已取消' };

function App() {
  const [projects, setProjects] = useState<Project[]>([]);
  const [projectId, setProjectId] = useState(() => new URLSearchParams(location.search).get('project') ?? '');
  const [imports, setImports] = useState<ImportBatch[]>([]);
  const [selected, setSelected] = useState<ImportBatch | null>(null);
  const [revisions, setRevisions] = useState<Revision[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [choices, setChoices] = useState<Record<string, { decision: string; reason: string }>>({});
  const [csrf, setCsrf] = useState('');
  const [user, setUser] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const project = projects.find(item => item.projectId === projectId);

  const request = useCallback(async <T,>(url: string, init: RequestInit = {}): Promise<T> => {
    const headers = new Headers(init.headers);
    if (init.method && init.method !== 'GET') headers.set('X-CSRF-Token', csrf);
    const response = await fetch(url, { ...init, headers, credentials: 'same-origin' });
    if (response.status === 401) { location.assign(`/auth/login?returnUrl=${encodeURIComponent(location.pathname + location.search)}`); throw new Error('正在登录'); }
    const body = await response.json() as Envelope<T>;
    if (!response.ok) throw new Error(body.message ?? `请求失败（${response.status}）`);
    return body.data;
  }, [csrf]);

  const chooseProject = (id: string) => {
    setProjectId(id); setSelected(null); setRevisions([]); setChoices({}); setError(''); setMessage('');
    const url = new URL(location.href); if (id) url.searchParams.set('project', id); else url.searchParams.delete('project'); history.replaceState(null, '', url);
  };

  const refresh = useCallback(async () => {
    if (!projectId) return;
    const page = await request<Envelope<ImportBatch[]> | { items: ImportBatch[] }>(`/api/v1/projects/${projectId}/imports?limit=100`);
    setImports('items' in page ? page.items : []);
  }, [projectId, request]);

  useEffect(() => {
    request<{ displayName: string; csrfToken: string }>('/auth/session').then(async session => {
      setUser(session.displayName); setCsrf(session.csrfToken);
      const page = await fetch('/api/v1/projects/?limit=100', { credentials: 'same-origin' }).then(r => r.json()) as Envelope<{ items: Project[] }>;
      setProjects(page.data.items);
      if (!projectId && page.data.items.length === 1) chooseProject(page.data.items[0]!.projectId);
    }).catch(reason => setError(reason.message));
  }, []);

  useEffect(() => { refresh().catch(reason => setError(reason.message)); }, [refresh]);

  const preview = async (id: string) => {
    setBusy(true); setError('');
    try {
      const batch = await request<ImportBatch>(`/api/v1/projects/${projectId}/imports/${id}/preview`);
      setSelected(batch); setRevisions([]); setChoices({});
      if (batch.changeSetId) await readCandidates(batch.changeSetId);
    } catch (reason) { setError((reason as Error).message); } finally { setBusy(false); }
  };

  const readCandidates = async (changeSetId: string) => {
    const result = await request<{ items: Revision[] }>(`/api/v1/projects/${projectId}/units?mode=draft&purpose=change&changeSetId=${changeSetId}&limit=100`);
    setRevisions(result.items);
  };

  const upload = async (event: FormEvent) => {
    event.preventDefault(); if (!file || !project) return;
    setBusy(true); setError(''); setMessage('');
    try {
      const form = new FormData(); form.append('file', file); form.append('baseBaselineId', project.currentBaselineId);
      const batch = await request<ImportBatch>(`/api/v1/projects/${projectId}/imports/upload`, { method: 'POST', headers: { 'Idempotency-Key': crypto.randomUUID() }, body: form });
      setSelected(batch); setFile(null); (document.getElementById('package-file') as HTMLInputElement).value = '';
      setMessage(`已完成 ${batch.entries.length} 项差异预览；尚未写入业务草稿。`); await refresh();
    } catch (reason) { setError((reason as Error).message); } finally { setBusy(false); }
  };

  const decide = async () => {
    if (!selected) return;
    const required = selected.entries.filter(entry => entry.status === 'conflict' || selected.unknowns.some(unknown => unknown.externalId === entry.externalId && unknown.code === 'IMPORTED_CONFLICT'));
    const inputs = required.map(entry => ({ entryId: entry.entryId, ...choices[entry.entryId] }));
    if (inputs.some(input => !input.decision || !input.reason?.trim())) { setError('请为每项冲突选择处理方式并填写业务理由。'); return; }
    setBusy(true); setError('');
    try {
      const batch = await request<ImportBatch>(`/api/v1/projects/${projectId}/imports/${selected.importId}/decisions`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"${selected.version}"` }, body: JSON.stringify({ decisions: inputs }) });
      setSelected(batch); setMessage('冲突决策已记录；仍需暂存并通过正常审核、发布。'); await refresh();
    } catch (reason) { setError((reason as Error).message); } finally { setBusy(false); }
  };

  const stage = async () => {
    if (!selected) return;
    setBusy(true); setError('');
    try {
      const result = await request<{ changeSetId: string }>(`/api/v1/projects/${projectId}/imports/${selected.importId}/stage`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"${selected.version}"` }, body: '{}' });
      setMessage('导入已原子写入草稿 ChangeSet；未获批准，也未发布。'); await preview(selected.importId); await readCandidates(result.changeSetId); await refresh();
    } catch (reason) { setError((reason as Error).message); } finally { setBusy(false); }
  };

  const discard = async () => {
    if (!selected || !confirm('取消这个导入提案？此操作不会删除任何已发布历史。')) return;
    setBusy(true); setError('');
    try {
      const batch = await request<ImportBatch>(`/api/v1/projects/${projectId}/imports/${selected.importId}/discard`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"${selected.version}"` }, body: JSON.stringify({ reason: '用户在预览界面取消导入' }) });
      setSelected(batch); setMessage('导入已取消，业务草稿未发生变化。'); await refresh();
    } catch (reason) { setError((reason as Error).message); } finally { setBusy(false); }
  };

  const requiredConflicts = selected?.entries.filter(entry => entry.status === 'conflict' || selected.unknowns.some(unknown => unknown.externalId === entry.externalId && unknown.code === 'IMPORTED_CONFLICT')) ?? [];

  return <>
    <header className="topbar"><a className="brand" href="/"><span className="brand-mark">BA</span><span>Business Atlas<small>业务认知工作空间</small></span></a><nav><a className="active" href="#intake">资料接入</a><a href="#history">导入历史</a><a href="#reading">候选阅读</a></nav><span className="user">{user || '正在确认身份'}<i title="已认证会话" /></span></header>
    <main>
      <section className="intro" id="intake"><div><p className="eyebrow">PROJECT INTAKE / AKP</p><h1>让旧项目知识，<br /><em>有依据地进入 Atlas。</em></h1><p>保留原始来源与外部身份，先比较再确认。导入只生成待审核候选，绝不自动批准。</p></div><div className="project-picker"><label htmlFor="project">当前项目</label><select id="project" value={projectId} onChange={event => chooseProject(event.target.value)}><option value="">选择项目工作空间</option>{projects.map(item => <option value={item.projectId} key={item.projectId}>{item.displayName} · {item.slug}</option>)}</select><span>{projects.length} 个可访问项目 · 权限由服务端校验</span></div></section>
      {!projectId ? <section className="empty"><b>选择一个真实项目开始</b><p>平台不会为导入演示虚构项目或业务事实。</p></section> : <>
        <section className="upload-panel"><div className="step">01</div><div className="upload-copy"><h2>上传知识包</h2><p>支持旧版 <code>AKP/0.1</code>、原生 <code>AtlasProposal/1.0</code>，JSON 或安全 ZIP。原始包保存在项目私有存储。</p><ul><li>结构、引用与来源完整性校验</li><li>同包重放返回原结果；同 ID 不同内容拒绝</li><li>扫描、权限、hash 与基线检查</li></ul></div><form onSubmit={upload}><label className={`file-drop ${file ? 'has-file' : ''}`} htmlFor="package-file"><input id="package-file" type="file" accept=".json,.zip,application/json,application/zip" onChange={event => setFile(event.target.files?.[0] ?? null)} /><strong>{file ? file.name : '选择 AKP 文件'}</strong><span>{file ? `${(file.size / 1024).toFixed(1)} KiB · 点击可更换` : '最大 100 MiB · 文件内容不会自动执行'}</span></label><button className="primary" disabled={!file || busy} type="submit">校验并生成预览 <span>→</span></button></form></section>
        {(message || error) && <div className={error ? 'notice error' : 'notice'} role="status">{error || message}</div>}
        <div className="workspace-grid">
          <section className="history" id="history"><div className="section-head"><div><p className="eyebrow">02 / HISTORY</p><h2>导入记录</h2></div><button className="quiet" onClick={() => refresh()}>刷新</button></div>{imports.length === 0 ? <p className="muted">当前项目还没有导入记录。</p> : <div className="import-list">{imports.map(item => <button key={item.importId} onClick={() => preview(item.importId)} className={selected?.importId === item.importId ? 'selected' : ''}><code>{item.packageId}</code><span className={`state ${item.state}`}>{stateLabels[item.state] ?? item.state}</span><small>{item.entries.length} 项 · v{item.version}</small></button>)}</div>}</section>
          <section className="preview"><div className="section-head"><div><p className="eyebrow">03 / REVIEW</p><h2>差异与确认</h2></div>{selected && <code>{selected.packageId}</code>}</div>{!selected ? <div className="empty compact"><b>上传或选择一条导入记录</b><p>新增、修订、等价重复与冲突会在这里分开呈现。</p></div> : <>
            <div className="summary">{['new', 'updated', 'duplicate', 'conflict'].map(status => <div key={status}><strong>{selected.entries.filter(entry => entry.status === status).length}</strong><span>{statusLabels[status]}</span></div>)}</div>
            <div className="entries">{selected.entries.map(entry => <article key={entry.entryId}><div><span className={`diff ${entry.status}`}>{statusLabels[entry.status] ?? entry.status}</span><code>{entry.externalId}</code></div><b>{entry.kind}</b><p>{entry.summary}</p>{entry.decision && <small>决策：{entry.decision}</small>}</article>)}</div>
            {selected.unknowns.length > 0 && <details className="unknowns"><summary>来源问题与未知（{selected.unknowns.length}）</summary>{selected.unknowns.map(item => <p key={`${item.code}-${item.externalId}`}><b>{item.message}</b><small>{item.code} · {item.externalId}</small></p>)}</details>}
            {requiredConflicts.length > 0 && !['staged', 'discarded'].includes(selected.state) && <div className="decisions"><h3>人工冲突决策</h3>{requiredConflicts.map(entry => <div className="decision-row" key={entry.entryId}><code>{entry.externalId}</code><select aria-label={`${entry.externalId} 处理方式`} value={choices[entry.entryId]?.decision ?? ''} onChange={event => setChoices({ ...choices, [entry.entryId]: { decision: event.target.value, reason: choices[entry.entryId]?.reason ?? '' } })}><option value="">选择处理方式</option>{decisions.map(value => <option key={value}>{value}</option>)}</select><input aria-label={`${entry.externalId} 决策理由`} placeholder="业务理由（必填）" value={choices[entry.entryId]?.reason ?? ''} onChange={event => setChoices({ ...choices, [entry.entryId]: { decision: choices[entry.entryId]?.decision ?? '', reason: event.target.value } })} /></div>)}<button className="secondary" onClick={decide} disabled={busy}>保存冲突决策</button></div>}
            {!['staged', 'discarded'].includes(selected.state) && <div className="actions"><button className="primary" onClick={stage} disabled={busy || selected.entries.some(entry => entry.status === 'invalid')}>原子暂存为草稿</button><button className="danger" onClick={discard} disabled={busy}>取消导入</button><span>后续仍须正常人工审核与发布</span></div>}
          </>}
          </section>
        </div>
        <section className="reading" id="reading"><div className="section-head"><div><p className="eyebrow">04 / READING</p><h2>接入后阅读入口</h2></div>{selected?.changeSetId && <button className="quiet" onClick={() => readCandidates(selected.changeSetId!)}>读取当前候选</button>}</div>{revisions.length ? <div className="revision-grid">{revisions.map(item => <details key={item.revisionId}><summary><span>{item.kind}</span><b>{item.title}</b><small>{item.factKind} · {item.verification}</small></summary><pre>{JSON.stringify(item.body, null, 2)}</pre>{item.kind === 'Source' && <a href={`/api/v1/projects/${projectId}/sources/${item.unitId}/content?mode=draft&purpose=change&changeSetId=${selected?.changeSetId}`}>按当前权限下载原始来源</a>}</details>)}</div> : <p className="muted">暂存后可从这里阅读候选及其来源。发布知识仍走治理模块。</p>}</section>
      </>}
    </main><footer>Business Atlas · 导入、观察与业务批准是三个不同事件。</footer>
  </>;
}

const root = document.getElementById('root');
if (!root) throw new Error('Missing application root');
createRoot(root).render(<StrictMode><App /></StrictMode>);
