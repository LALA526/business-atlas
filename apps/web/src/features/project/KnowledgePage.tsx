import { useCallback, useEffect } from 'react';
import type { KeyboardEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useOutletContext, useParams, useSearchParams } from 'react-router-dom';
import { atlasApi } from '../../shared/api';
import type { ReadingPurpose } from '../../shared/api';
import { useSession } from '../../shared/session';
import { ErrorState, LoadingState } from '../../shared/states';
import CaseEvaluator from '../knowledge/CaseEvaluator';
import KnowledgeEditor from '../knowledge/KnowledgeEditor';
import { ReaderSection, UnknownList } from '../knowledge/ReaderContent';
import SourceDrawer from '../knowledge/SourceDrawer';
import type { ProjectOutletContext } from './ProjectShell';
import { factKindLabel, knowledgeKindLabel } from './knowledgeLabels';

const purposes: { key: ReadingPurpose; label: string; description: string }[] = [
  { key: 'understand', label: '理解业务', description: '先读目的、行为与判断' },
  { key: 'implement', label: '查看实现', description: '优先读设计、组件与契约' },
  { key: 'change', label: '准备变更', description: '优先读规则与约束' },
  { key: 'verify', label: '核验结果', description: '优先读案例、测试与观察' },
];

export default function KnowledgePage() {
  const { project } = useOutletContext<ProjectOutletContext>();
  const { projectId = '', unitId = '' } = useParams();
  const [params, setParams] = useSearchParams();
  const session = useSession();
  const purpose = purposes.some(item => item.key === params.get('purpose')) ? params.get('purpose') as ReadingPurpose : 'understand';
  const baseline = params.get('baseline') ?? undefined;
  const editing = params.get('edit') === '1';
  const sourcesOpen = params.get('sources') === '1';
  const reading = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'reading', baseline ?? project.currentBaselineId, purpose, unitId], queryFn: () => atlasApi.reading(projectId, unitId, { purpose, baselineId: baseline }), retry: false });
  const unit = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'unit', baseline ?? project.currentBaselineId, unitId], queryFn: () => atlasApi.unit(projectId, unitId, baseline), retry: false });
  const baselines = useQuery({ queryKey: [session.principalId, projectId, project.policyEpoch, 'baselines'], queryFn: () => atlasApi.baselines(projectId), retry: false });
  const update = (values: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params);
    Object.entries(values).forEach(([key, value]) => value ? next.set(key, value) : next.delete(key));
    setParams(next, { preventScrollReset: true });
  };
  const closeSources = useCallback(() => update({ sources: undefined }), [params]);
  const from = params.get('from');
  const back = from?.startsWith(`/p/${projectId}/`) ? from : `/p/${projectId}/business`;
  const isFeature = unit.data?.kind === 'Feature';
  const sections = reading.data?.sections ?? [];
  const requestedTab = params.get('tab');
  const activeTab = isFeature ? (sections.some(section => section.key === requestedTab) ? requestedTab! : 'rules') : sections[0]?.key;
  const visibleSections = isFeature ? sections.filter(section => section.key === activeTab) : sections;
  const closeEditor = () => update({ edit: undefined });

  useEffect(() => {
    const focus = params.get('focus');
    if (!focus || reading.isPending) return;
    const frame = requestAnimationFrame(() => document.getElementById(focus)?.scrollIntoView({ block: 'center' }));
    return () => cancelAnimationFrame(frame);
  }, [params, reading.isPending, reading.data]);

  const tabKeys = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? sections.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + sections.length) % sections.length;
    const target = sections[next];
    if (!target) return;
    update({ tab: target.key, focus: undefined });
    document.getElementById(`tab-${target.key}`)?.focus();
  };

  return <div className="knowledge-page">
    <Link className="back-link" to={back}>← 返回原阅读位置</Link>
    {reading.isPending || unit.isPending ? <LoadingState label="正在组合局部知识、规则与来源" /> : reading.isError ? <ErrorState error={reading.error} onRetry={() => reading.refetch()} /> : unit.isError ? <ErrorState error={unit.error} onRetry={() => unit.refetch()} /> : <>
      <header className="knowledge-heading">
        <div className="knowledge-title"><p className="eyebrow">{knowledgeKindLabel(unit.data.kind)}</p><h1>{reading.data.heading}</h1><p>围绕当前主题组合必要内容；规则、目标设计与实际观察分开阅读。</p></div>
        <dl className="knowledge-meta"><div><dt>内容性质</dt><dd>{factKindLabel(unit.data.factKind)}</dd></div><div><dt>阅读版本</dt><dd>{baseline ? '历史基线' : '当前已发布'}</dd></div><div><dt>授权完整性</dt><dd>{reading.data.completeness === 'complete' ? '当前范围完整' : '部分内容'}</dd></div></dl>
        <div className="reader-actions"><button className="secondary" onClick={() => update({ sources: sourcesOpen ? undefined : '1' })}>来源与适用范围</button><button className="primary" onClick={() => update({ edit: '1' })}>编辑候选</button></div>
      </header>
      <section className="reader-controls" aria-label="阅读上下文">
        <label>阅读目的<select value={purpose} onChange={event => update({ purpose: event.target.value, focus: undefined })}>{purposes.map(item => <option key={item.key} value={item.key}>{item.label} · {item.description}</option>)}</select></label>
        <label>知识版本<select value={baseline ?? ''} onChange={event => update({ baseline: event.target.value || undefined, focus: undefined })}><option value="">当前已发布</option>{baselines.data?.items.filter(item => item.baselineId !== project.currentBaselineId).map(item => <option key={item.baselineId} value={item.baselineId}>历史基线 #{item.number} · {new Date(item.createdAt).toLocaleDateString('zh-CN')}</option>)}</select></label>
        <span>切换阅读方式或版本只改变投影，不修改知识事实。</span>
      </section>
      {baselines.isError && <p className="inline-warning">历史版本列表暂时不可用；当前阅读不受影响。<button onClick={() => baselines.refetch()}>重试历史列表</button></p>}
      {reading.data.unknowns.length > 0 && <section className="reader-unknowns"><div><p className="eyebrow">就地说明</p><h2>未知、冲突与限制</h2></div><UnknownList items={reading.data.unknowns} /></section>}
      {editing ? <KnowledgeEditor project={project} revision={unit.data} onClose={closeEditor} /> : <>
        {isFeature && <div className="reader-tabs" role="tablist" aria-label="业务知识视图">{sections.map((section, index) => <button id={`tab-${section.key}`} key={section.key} role="tab" aria-selected={activeTab === section.key} aria-controls={`panel-${section.key}`} tabIndex={activeTab === section.key ? 0 : -1} className={activeTab === section.key ? 'active' : ''} onKeyDown={event => tabKeys(event, index)} onClick={() => update({ tab: section.key, focus: undefined })}>{section.title}</button>)}</div>}
        <div className="reader-sections" {...(isFeature ? { role: 'tabpanel', id: `panel-${activeTab}`, 'aria-labelledby': `tab-${activeTab}` } : {})}>
          {activeTab === 'design' && <div className="design-separation"><strong>应然设计与实然行为分开</strong><p>已批准设计说明“应该如何”，实际观察说明“当前看到什么”；技术实现映射不会自动成为业务目标或一致性证明。</p></div>}
          {visibleSections.map(section => <ReaderSection key={section.key} section={section} projectId={projectId} onSources={() => update({ sources: '1' })} />)}
          {visibleSections.length === 0 && <p className="reader-empty">当前目的和版本下没有可显示内容。</p>}
        </div>
        {(unit.data.kind === 'BusinessRule' || unit.data.kind === 'Rule') && <CaseEvaluator projectId={projectId} revision={unit.data} baselineId={baseline ?? project.currentBaselineId} />}
      </>}
      {sourcesOpen && <SourceDrawer revision={unit.data} onClose={closeSources} />}
    </>}
  </div>;
}
