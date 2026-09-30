import type {
  Connection,
  ConnectionTestResult,
  Run,
  ValidationResult,
  WebhookEvent,
  WebhookTriggerResult,
  Workflow,
} from './types'

const BASE_URL: string = import.meta.env.VITE_API_BASE_URL ?? ''

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    credentials: 'include',
    ...init,
  })
  if (!response.ok) {
    // SSO: an unauthenticated API call redirects to the corporate login flow.
    if (response.status === 401 && !path.startsWith('/auth/')) {
      const returnUrl = `${window.location.pathname}${window.location.search}`
      window.location.href = `${BASE_URL}/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`
      return new Promise<T>(() => {}) // navigation happens; keep the promise pending
    }
    let detail = `${response.status} ${response.statusText}`
    try {
      const body = (await response.json()) as { error?: string; errors?: string[] }
      if (body.error) detail = body.error
      if (body.errors?.length) detail += `: ${body.errors.join('; ')}`
    } catch {
      /* non-JSON error body */
    }
    throw new Error(detail)
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export interface SessionState {
  authenticated: boolean
  ssoEnabled: boolean
  name?: string
  email?: string
}

export const auth = {
  me: () => request<SessionState>('/auth/me'),
  signOut: () => {
    // Full navigation: the backend clears the app cookie + Entra session, then
    // lands back on "/" which reloads the SPA.
    window.location.href = `${BASE_URL}/auth/logout`
    return Promise.resolve()
  },
  signIn: (returnUrl?: string) => {
    window.location.href = `${BASE_URL}/auth/login${returnUrl ? `?returnUrl=${encodeURIComponent(returnUrl)}` : ''}`
  },
}

export const api = {
  // Workflows
  listWorkflows: () => request<Workflow[]>('/api/workflows'),
  getWorkflow: (id: string) => request<Workflow>(`/api/workflows/${id}`),
  createWorkflow: (body: { name: string; description?: string; enabled?: boolean; graph?: unknown }) =>
    request<Workflow>('/api/workflows', { method: 'POST', body: JSON.stringify(body) }),
  updateWorkflow: (
    id: string,
    body: { name?: string; description?: string; enabled?: boolean; graph?: unknown },
  ) => request<Workflow>(`/api/workflows/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteWorkflow: (id: string) =>
    request<void>(`/api/workflows/${id}`, { method: 'DELETE' }),
  validateWorkflow: (id: string, graph?: unknown) =>
    request<ValidationResult>(`/api/workflows/${id}/validate`, {
      method: 'POST',
      body: JSON.stringify(graph ? { graph } : {}),
    }),
  runWorkflow: (id: string, input?: unknown) =>
    request<Run>(`/api/workflows/${id}/run`, { method: 'POST', body: JSON.stringify(input ?? {}) }),

  // Runs
  listRuns: (workflowId?: string, limit = 100) =>
    request<Run[]>(`/api/runs?limit=${limit}${workflowId ? `&workflowId=${workflowId}` : ''}`),
  getRun: (id: string) => request<Run>(`/api/runs/${id}`),
  rerunRun: (id: string) => request<Run>(`/api/runs/${id}/rerun`, { method: 'POST' }),

  // Connections
  listConnections: () => request<Connection[]>('/api/connections'),
  getConnection: (id: string) => request<Connection>(`/api/connections/${id}`),
  createConnection: (body: Partial<Connection>) =>
    request<Connection>('/api/connections', { method: 'POST', body: JSON.stringify(body) }),
  updateConnection: (id: string, body: Partial<Connection>) =>
    request<Connection>(`/api/connections/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  deleteConnection: (id: string) =>
    request<void>(`/api/connections/${id}`, { method: 'DELETE' }),
  testConnection: (id: string) =>
    request<ConnectionTestResult>(`/api/connections/${id}/test`, { method: 'POST' }),

  // Webhooks (durable deliveries + simulate)
  triggerWebhook: (workflowId: string, payload: unknown) =>
    request<WebhookTriggerResult>(`/webhook/${workflowId}`, {
      method: 'POST',
      body: JSON.stringify(payload ?? {}),
    }),
  listWebhookEvents: (params?: { workflowId?: string; status?: string; limit?: number }) => {
    const qs = new URLSearchParams()
    if (params?.workflowId) qs.set('workflowId', params.workflowId)
    if (params?.status) qs.set('status', params.status)
    qs.set('limit', String(params?.limit ?? 100))
    return request<WebhookEvent[]>(`/api/webhook-events?${qs.toString()}`)
  },
  getWebhookEvent: (id: string) => request<WebhookEvent>(`/api/webhook-events/${id}`),
  replayWebhookEvent: (id: string) =>
    request<WebhookTriggerResult>(`/api/webhook-events/${id}/replay`, { method: 'POST' }),
}
