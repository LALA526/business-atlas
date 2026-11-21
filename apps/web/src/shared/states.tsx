import type { ReactNode } from 'react';
import { ApiError } from './api';

export function LoadingState({ label = '正在读取已授权内容' }: { label?: string }) {
  return <div className="loading-state" role="status"><span className="spinner" aria-hidden="true" />{label}</div>;
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const status = error instanceof ApiError ? error.status : 0;
  const title = status === 403 ? '当前账号无权查看此内容' : status === 404 ? '内容不存在或不在当前权限范围内' : status === 503 ? '读取服务暂时不可用' : '暂时无法读取内容';
  return <div className="error-state" role="alert"><strong>{title}</strong><p>{error instanceof Error ? error.message : '请稍后重试。'}</p>{onRetry && <button className="secondary" onClick={onRetry}>重新读取</button>}</div>;
}

export function EmptyState({ title, children, action }: { title: string; children: ReactNode; action?: ReactNode }) {
  return <div className="empty-state"><strong>{title}</strong><p>{children}</p>{action}</div>;
}
