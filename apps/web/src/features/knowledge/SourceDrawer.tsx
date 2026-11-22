import { useEffect, useRef } from 'react';
import type { Revision } from '../../shared/api';

const supportLabels = { supports: '支持此结论', refutes: '反驳此结论', contextual: '提供上下文' } as const;
const captureLabels = { rawCaptured: '原始捕获', verifiedScript: '脚本核验', reconstructed: '重建记录', proposed: '候选材料', humanAttestation: '人工确认' } as const;

export default function SourceDrawer({ revision, onClose }: { revision: Revision; onClose: () => void }) {
  const closeButton = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    closeButton.current?.focus();
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('keydown', onKey); previous?.focus(); };
  }, [onClose]);
  const evidence = revision.evidence ?? [];
  return <div className="drawer-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <aside className="source-drawer" role="dialog" aria-modal="true" aria-labelledby="source-drawer-title">
      <div className="dialog-head"><div><p className="eyebrow">按需查看证据</p><h2 id="source-drawer-title">结论来源与适用范围</h2></div><button ref={closeButton} className="icon-button" aria-label="关闭来源" onClick={onClose}>×</button></div>
      <p className="source-subject">{revision.title}<span>{revision.revNo ? `修订 ${revision.revNo}` : '知识修订'} · 已绑定当前阅读版本</span></p>
      {evidence.length === 0 ? <div className="source-empty"><strong>未提供可查看的来源证据</strong><p>这不等于结论已经被证实。可在候选编辑中补充依据，再交由审核人确认。</p></div> : <div className="evidence-list">{evidence.map((item, index) => <article key={`${item.source.revisionId}-${index}`}>
        <div><strong>{supportLabels[item.support]}</strong><span>{captureLabels[item.captureKind]}</span></div>
        <dl>
          {item.locator.description && <div><dt>说明</dt><dd>{item.locator.description}</dd></div>}
          {item.locator.quote && <div><dt>原文摘录</dt><dd>{item.locator.quote}</dd></div>}
          {item.locator.path && <div><dt>资料定位</dt><dd>{item.locator.path}{item.locator.lineStart ? `：${item.locator.lineStart}${item.locator.lineEnd ? `–${item.locator.lineEnd}` : ''}` : ''}</dd></div>}
          {item.locator.page !== undefined && <div><dt>页码</dt><dd>{item.locator.page}</dd></div>}
          {item.locator.uri && <div><dt>来源地址</dt><dd>{item.locator.uri}</dd></div>}
          {item.locator.commitSha && <div><dt>代码版本</dt><dd>{item.locator.commitSha}</dd></div>}
          <div><dt>精确来源修订</dt><dd>{item.source.revisionId}</dd></div>
        </dl>
      </article>)}</div>}
      <p className="drawer-note">只显示当前账号和当前基线获准读取的来源元数据。受权限限制或未提供的内容不会在此猜测补全。</p>
    </aside>
  </div>;
}
