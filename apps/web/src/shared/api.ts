import createClient from 'openapi-fetch';
import type { components, paths } from './api.generated';

export type Schema<K extends keyof components['schemas']> = components['schemas'][K];
export type Project = Schema<'ProjectSummary'>;
export type ProjectPage = Schema<'ProjectSummaryPage'>;
export type ReadingView = Schema<'ReadingView'>;
export type CatalogNode = Schema<'CatalogNode'>;
export type Revision = Schema<'RevisionView'>;
export type RevisionPage = Schema<'RevisionViewPage'>;
export type SearchPage = Schema<'SearchHitPage'>;
export type ImportBatch = Schema<'ImportBatch'>;
export type Session = { principalId: string; displayName: string; expiresAt: string; csrfToken: string };

let csrfToken = '';
export function setCsrfToken(value: string) { csrfToken = value; }

export class ApiError extends Error {
  constructor(message: string, readonly status: number, readonly code = 'REQUEST_FAILED') { super(message); this.name = 'ApiError'; }
}

const client = createClient<paths>({ credentials: 'same-origin' });
client.use({
  onRequest({ request }) {
    if (request.method !== 'GET' && csrfToken) request.headers.set('X-CSRF-Token', csrfToken);
    return request;
  },
});

function redirectToLogin() {
  const returnUrl = `${location.pathname}${location.search}${location.hash}`;
  location.assign(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`);
}

async function unwrap<T>(pending: Promise<{ data?: unknown; error?: unknown; response: Response }>): Promise<T> {
  const result = await pending;
  if (result.response.status === 401) redirectToLogin();
  if (result.error !== undefined || !result.response.ok) {
    const body = (result.error ?? result.data) as { message?: string; code?: string } | undefined;
    throw new ApiError(body?.message ?? `请求失败（${result.response.status}）`, result.response.status, body?.code);
  }
  return (result.data as { data: T }).data;
}

export const atlasApi = {
  session: async () => {
    const response = await fetch('/auth/session', { credentials: 'same-origin', headers: { Accept: 'application/json' } });
    if (response.status === 401) { redirectToLogin(); throw new ApiError('正在前往登录', 401, 'UNAUTHENTICATED'); }
    const body = await response.json() as Session & { message?: string };
    if (!response.ok) throw new ApiError(body.message ?? '无法确认登录会话', response.status);
    setCsrfToken(body.csrfToken);
    return body;
  },
  projects: () => unwrap<ProjectPage>(client.GET('/api/v1/projects', { params: { query: { limit: 100 } } })),
  createProject: (displayName: string, slug: string) => unwrap<Project>(client.POST('/api/v1/projects', {
    params: { header: { 'Idempotency-Key': crypto.randomUUID() } }, body: { displayName, slug },
  })),
  overview: (projectId: string) => unwrap<ReadingView>(client.GET('/api/v1/projects/{projectId}/dashboard', { params: { path: { projectId }, query: { mode: 'published', purpose: 'understand' } } })),
  catalog: (projectId: string) => unwrap<CatalogNode[]>(client.GET('/api/v1/projects/{projectId}/catalog', { params: { path: { projectId }, query: { mode: 'published', purpose: 'understand' } } })),
  units: (projectId: string, query: { kind?: string; categoryKey?: string; cursor?: string } = {}) => unwrap<RevisionPage>(client.GET('/api/v1/projects/{projectId}/units', { params: { path: { projectId }, query: { ...query, limit: 100, mode: 'published', purpose: 'understand' } } })),
  search: (projectId: string, q: string, kind?: string, cursor?: string) => unwrap<SearchPage>(client.GET('/api/v1/projects/{projectId}/search', { params: { path: { projectId }, query: { q, kind, cursor, limit: 50, mode: 'published', purpose: 'understand' } } })),
  reading: (projectId: string, rootUnitId: string) => unwrap<ReadingView>(client.POST('/api/v1/projects/{projectId}/reading-plans', { params: { path: { projectId } }, body: { rootUnitId, readContext: { mode: 'published', purpose: 'understand' } } })),
};

export async function apiRequest<T>(url: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.method && init.method !== 'GET' && csrfToken) headers.set('X-CSRF-Token', csrfToken);
  const response = await fetch(url, { ...init, headers, credentials: 'same-origin' });
  if (response.status === 401) redirectToLogin();
  const body = await response.json() as { data?: T; message?: string; code?: string };
  if (!response.ok) throw new ApiError(body.message ?? `请求失败（${response.status}）`, response.status, body.code);
  return body.data as T;
}
