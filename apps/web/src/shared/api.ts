import createClient from 'openapi-fetch';
import type { components, paths } from './api.generated';

export type Schema<K extends keyof components['schemas']> = components['schemas'][K];
export type Project = Schema<'ProjectSummary'>;
export type ProjectPage = Schema<'ProjectSummaryPage'>;
export type ReadingView = Schema<'ReadingView'>;
export type ReadingPurpose = 'understand' | 'implement' | 'change' | 'verify';
export type CatalogNode = Schema<'CatalogNode'>;
export type Revision = Schema<'RevisionView'>;
export type RevisionPage = Schema<'RevisionViewPage'>;
export type RevisionInput = Schema<'RevisionInput'>;
export type RuleResult = Schema<'RuleResult'>;
export type ChangeSet = Schema<'ChangeSet'>;
export type ChangeSetPage = Schema<'ChangeSetPage'>;
export type ReviewRequest = Schema<'ReviewRequest'>;
export type Baseline = Schema<'Baseline'>;
export type BaselinePage = Schema<'BaselinePage'>;
export type DiffItem = Schema<'DiffItem'>;
export type Activity = Omit<Schema<'Activity'>, 'target'> & { target: { unitId?: string; revisionId?: string; taskId?: string; changeSetId?: string; body: string } };
export type ActivityPage = { items: Activity[]; nextCursor?: string };
export type SearchPage = Schema<'SearchHitPage'>;
export type ImportBatch = Schema<'ImportBatch'>;
export type Conversation = Schema<'Conversation'>;
export type ConversationPage = Schema<'ConversationPage'>;
export type AssistantMessage = Schema<'Message'>;
export type MessagePage = Schema<'MessagePage'>;
export type ConversationJob = Schema<'Job'>;
export type Task = Schema<'Task'>;
export type TaskPage = Schema<'TaskPage'>;
export type Impact = {
  impactId?: string; taskId: string; target?: Schema<'UnitRef'>; locator?: Schema<'SourceLocator'>;
  path: { edge: Schema<'RevisionRef'>; predicate: string; direction: 'forward' | 'reverse' }[];
  disposition: 'required' | 'potential' | 'no_change' | 'deferred' | 'unknown';
  inspectionState: 'candidate' | 'inspected' | 'verified' | 'stale'; investigationId?: string;
  reason: string; risk: 'low' | 'medium' | 'high' | 'critical'; assignedTaskId?: string;
  evidence: Schema<'EvidenceRef'>[]; closeState: 'open' | 'resolved' | 'deferred'; version?: number;
};
export type ImpactPage = { items: Impact[]; nextCursor?: string };
export type TaskContext = Schema<'TaskContext'>;
export type Closure = Schema<'Closure'>;
export type AcceptanceInput = Schema<'AcceptanceInput'>;
export type DeliveryStatus = Schema<'DeliveryStatus'>;
export type DeliveryStatusPage = Schema<'DeliveryStatusPage'>;
export type AgentRunPage = Schema<'AgentRunPage'>;
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
  reading: (projectId: string, rootUnitId: string, options: { purpose?: ReadingPurpose; baselineId?: string; changeSetId?: string } = {}) => unwrap<ReadingView>(client.POST('/api/v1/projects/{projectId}/reading-plans', { params: { path: { projectId } }, body: { rootUnitId, readContext: { mode: options.changeSetId ? 'draft' : options.baselineId ? 'historical' : 'published', baselineId: options.baselineId, changeSetId: options.changeSetId, purpose: options.purpose ?? 'understand' } } })),
  unit: (projectId: string, unitId: string, baselineId?: string) => unwrap<Revision>(client.GET('/api/v1/projects/{projectId}/units/{unitId}', { params: { path: { projectId, unitId }, query: { mode: baselineId ? 'historical' : 'published', baselineId, purpose: 'understand' } } })),
  baselines: (projectId: string, cursor?: string) => unwrap<BaselinePage>(client.GET('/api/v1/projects/{projectId}/baselines', { params: { path: { projectId }, query: { limit: 50, cursor } } })),
  evaluateRule: (projectId: string, unitId: string, facts: Record<string, unknown>, baselineId?: string) => unwrap<RuleResult>(client.POST('/api/v1/projects/{projectId}/rules/{unitId}/evaluate', { params: { path: { projectId, unitId } }, body: { facts, readContext: { mode: baselineId ? 'historical' : 'published', baselineId, purpose: 'understand' } } })),
  createChangeSet: (projectId: string, title: string, baseBaselineId: string, key: string) => unwrap<ChangeSet>(client.POST('/api/v1/projects/{projectId}/change-sets', { params: { path: { projectId }, header: { 'Idempotency-Key': key } }, body: { title, baseBaselineId } })),
  reviseUnitDraft: (projectId: string, changeSetId: string, unitId: string, revision: RevisionInput, key: string, ifMatch: string) => unwrap<Revision>(client.POST('/api/v1/projects/{projectId}/change-sets/{changeSetId}/units/{unitId}/revisions', { params: { path: { projectId, changeSetId, unitId }, header: { 'Idempotency-Key': key, 'If-Match': `"${ifMatch.replaceAll('"', '')}"` } }, body: revision })),
  changeSet: (projectId: string, changeSetId: string) => unwrap<ChangeSet>(client.GET('/api/v1/projects/{projectId}/change-sets/{changeSetId}', { params: { path: { projectId, changeSetId } } })),
  submitChangeSet: (projectId: string, changeSetId: string, version: number, summary: Schema<'ContentBlock'>[], key: string) => unwrap<ChangeSet>(client.POST('/api/v1/projects/{projectId}/change-sets/{changeSetId}/submit', { params: { path: { projectId, changeSetId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: { summary, impactIds: [] } })),
  changeSets: (projectId: string, cursor?: string) => unwrap<ChangeSetPage>(client.GET('/api/v1/projects/{projectId}/change-sets', { params: { path: { projectId }, query: { limit: 100, cursor } } })),
  reviewChangeSet: (projectId: string, changeSetId: string, projectVersion: number, request: ReviewRequest, key: string) => unwrap<ChangeSet>(client.POST('/api/v1/projects/{projectId}/change-sets/{changeSetId}/review', { params: { path: { projectId, changeSetId }, header: { 'Idempotency-Key': key, 'If-Match': `"${projectVersion}"` } }, body: request })),
  reopenChangeSet: (projectId: string, changeSetId: string, projectVersion: number, reason: string, newBaseBaselineId: string | undefined, key: string) => unwrap<ChangeSet>(client.POST('/api/v1/projects/{projectId}/change-sets/{changeSetId}/reopen', { params: { path: { projectId, changeSetId }, header: { 'Idempotency-Key': key, 'If-Match': `"${projectVersion}"` } }, body: { reason, newBaseBaselineId } })),
  publishChangeSet: (projectId: string, changeSetId: string, projectVersion: number, submitDigest: string, baseBaselineId: string, key: string) => unwrap<Baseline>(client.POST('/api/v1/projects/{projectId}/change-sets/{changeSetId}/publish', { params: { path: { projectId, changeSetId }, header: { 'Idempotency-Key': key, 'If-Match': `"${projectVersion}"` } }, body: { submitDigest, baseBaselineId } })),
  comments: (projectId: string, changeSetId: string) => unwrap<ActivityPage>(client.GET('/api/v1/projects/{projectId}/comments', { params: { path: { projectId }, query: { changeSetId, limit: 100 } } })),
  createComment: (projectId: string, input: Schema<'ActivityInput'>, key: string) => unwrap<Activity>(client.POST('/api/v1/projects/{projectId}/comments', { params: { path: { projectId }, header: { 'Idempotency-Key': key } }, body: input })),
  resolveComment: (projectId: string, activityId: string, version: number, reason: string, changeSetId: string, key: string) => unwrap<Activity>(client.POST('/api/v1/projects/{projectId}/comments/{activityId}/resolve', { params: { path: { projectId, activityId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: { reason, changeSetId } })),
  conversations: (projectId: string) => unwrap<ConversationPage>(client.GET('/api/v1/projects/{projectId}/conversations', { params: { path: { projectId }, query: { limit: 100 } } })),
  createConversation: (projectId: string, rootUnitId: string | undefined, baselineId: string, historical: boolean, key: string) => unwrap<Conversation>(client.POST('/api/v1/projects/{projectId}/conversations', { params: { path: { projectId }, header: { 'Idempotency-Key': key } }, body: { rootUnitId, title: '项目上下文问答', readContext: { mode: historical ? 'historical' : 'published', baselineId, purpose: 'understand' } } })),
  messages: (projectId: string, conversationId: string) => unwrap<MessagePage>(client.GET('/api/v1/projects/{projectId}/conversations/{conversationId}/messages', { params: { path: { projectId, conversationId }, query: { limit: 100 } } })),
  sendMessage: (projectId: string, conversation: Conversation, currentBaselineId: string, text: string, key: string) => unwrap<ConversationJob>(client.POST('/api/v1/projects/{projectId}/conversations/{conversationId}/messages', { params: { path: { projectId, conversationId: conversation.conversationId }, header: { 'Idempotency-Key': key, 'If-Match': `"${conversation.version}"` } }, body: { text, readContext: { mode: conversation.baselineId === currentBaselineId ? 'published' : 'historical', baselineId: conversation.baselineId, purpose: 'understand' } } })),
  cancelConversation: (projectId: string, conversationId: string, jobId: string, version: number, key: string) => unwrap<ConversationJob>(client.POST('/api/v1/projects/{projectId}/conversations/{conversationId}/cancel', { params: { path: { projectId, conversationId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: { jobId } })),
  createFeedback: (projectId: string, input: Schema<'ActivityInput'>, key: string) => unwrap<Activity>(client.POST('/api/v1/projects/{projectId}/feedback', { params: { path: { projectId }, header: { 'Idempotency-Key': key } }, body: input })),
  tasks: (projectId: string, cursor?: string) => unwrap<TaskPage>(client.GET('/api/v1/projects/{projectId}/tasks', { params: { path: { projectId }, query: { limit: 100, cursor } } })),
  createTask: (projectId: string, intent: string, baseBaselineId: string, key: string) => unwrap<Task>(client.POST('/api/v1/projects/{projectId}/tasks', { params: { path: { projectId }, header: { 'Idempotency-Key': key } }, body: { intent, baseBaselineId } })),
  task: (projectId: string, taskId: string) => unwrap<Task>(client.GET('/api/v1/projects/{projectId}/tasks/{taskId}', { params: { path: { projectId, taskId } } })),
  impacts: (projectId: string, taskId: string) => unwrap<ImpactPage>(client.GET('/api/v1/projects/{projectId}/tasks/{taskId}/impacts', { params: { path: { projectId, taskId }, query: { limit: 100 } } })),
  taskContext: (projectId: string, contextId: string) => unwrap<TaskContext>(client.GET('/api/v1/projects/{projectId}/contexts/{contextId}', { params: { path: { projectId, contextId } } })),
  agentRuns: (projectId: string, taskId: string) => unwrap<AgentRunPage>(client.GET('/api/v1/projects/{projectId}/agent-runs', { params: { path: { projectId }, query: { taskId, limit: 100 } } })),
  taskComments: (projectId: string, taskId: string) => unwrap<ActivityPage>(client.GET('/api/v1/projects/{projectId}/comments', { params: { path: { projectId }, query: { taskId, limit: 100 } } })),
  issues: (projectId: string) => unwrap<ActivityPage>(client.GET('/api/v1/projects/{projectId}/issues', { params: { path: { projectId }, query: { limit: 100 } } })),
  feedback: (projectId: string) => unwrap<ActivityPage>(client.GET('/api/v1/projects/{projectId}/feedback', { params: { path: { projectId }, query: { limit: 100 } } })),
  createIssue: (projectId: string, input: Schema<'ActivityInput'>, key: string) => unwrap<Activity>(client.POST('/api/v1/projects/{projectId}/issues', { params: { path: { projectId }, header: { 'Idempotency-Key': key } }, body: input })),
  resolveIssue: (projectId: string, activityId: string, version: number, reason: string, taskId: string | undefined, key: string) => unwrap<Activity>(client.POST('/api/v1/projects/{projectId}/issues/{activityId}/resolve', { params: { path: { projectId, activityId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: { reason, taskId } })),
  resolveFeedback: (projectId: string, activityId: string, version: number, reason: string, taskId: string | undefined, changeSetId: string | undefined, key: string) => unwrap<Activity>(client.POST('/api/v1/projects/{projectId}/feedback/{activityId}/resolve', { params: { path: { projectId, activityId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: { reason, taskId, changeSetId } })),
  transitionTask: (projectId: string, taskId: string, version: number, event: 'begin_investigation' | 'request_decision' | 'begin_implementation' | 'submit_verification' | 'request_cancel' | 'resume', reason: string, contextId: string | undefined, key: string) => unwrap<Task>(client.POST('/api/v1/projects/{projectId}/tasks/{taskId}/transition', { params: { path: { projectId, taskId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: { event, reason, contextId } })),
  decideImpact: (projectId: string, taskId: string, impactId: string, version: number, impact: Impact, key: string) => unwrap<Impact>(client.PUT('/api/v1/projects/{projectId}/tasks/{taskId}/impacts/{impactId}', { params: { path: { projectId, taskId, impactId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: impact })),
  closure: (projectId: string, taskId: string, contextId: string) => unwrap<Closure>(client.GET('/api/v1/projects/{projectId}/tasks/{taskId}/closure', { params: { path: { projectId, taskId }, query: { contextId } } })),
  deliveries: (projectId: string, cursor?: string) => unwrap<DeliveryStatusPage>(client.GET('/api/v1/projects/{projectId}/knowledge-delivery', { params: { path: { projectId }, query: { limit: 100, cursor } } })),
  recordAcceptance: (projectId: string, taskId: string, version: number, input: AcceptanceInput, key: string) => unwrap<Schema<'IdView'>>(client.POST('/api/v1/projects/{projectId}/tasks/{taskId}/acceptance', { params: { path: { projectId, taskId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: input })),
  closeTask: (projectId: string, taskId: string, version: number, contextId: string, reason: string, key: string) => unwrap<Closure>(client.POST('/api/v1/projects/{projectId}/tasks/{taskId}/close', { params: { path: { projectId, taskId }, header: { 'Idempotency-Key': key, 'If-Match': `"${version}"` } }, body: { contextId, reason } })),
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
