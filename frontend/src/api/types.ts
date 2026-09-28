export type NodeType =
  | 'trigger'
  | 'http_request'
  | 'db_query'
  | 'transform'
  | 'condition'
  | 'loop'
  | 'delay'

export interface GraphNodeDef {
  id: string
  type: NodeType
  config: Record<string, unknown>
}

/** Graph JSON as persisted by the backend (positions is a frontend-only extension; backend ignores it). */
export interface WorkflowGraph {
  nodes: GraphNodeDef[]
  positions?: Record<string, { x: number; y: number }>
}

export interface Workflow {
  id: string
  name: string
  description?: string
  enabled: boolean
  graph: WorkflowGraph | null
  createdAt: string
  updatedAt: string
}

export interface RunStep {
  id: string
  nodeId: string
  nodeType: string
  status: 'success' | 'failed' | 'skipped'
  input?: unknown
  output?: unknown
  error?: string
  attempts: number
  durationMs: number
  startedAt: string
}

export interface Run {
  id: string
  workflowId: string
  status: 'running' | 'success' | 'failed'
  input?: unknown
  output?: unknown
  error?: string
  startedAt: string
  finishedAt?: string
  steps: RunStep[]
}

export type ConnectionKind = 'http' | 'db'
export type DbType = 'mssql' | 'postgres' | 'mysql'

export interface Connection {
  id: string
  name: string
  kind: ConnectionKind
  baseUrl?: string
  authType: 'none' | 'api_key' | 'bearer' | 'basic' | 'oauth2'
  authConfig?: Record<string, unknown> | null
  dbType?: DbType
  dbConfig?: Record<string, unknown> | null
  createdAt: string
}

export interface ConnectionTestResult {
  success: boolean
  detail: string
}

export interface ValidationResult {
  valid: boolean
  errors: string[]
}

export type WebhookEventStatus = 'received' | 'succeeded' | 'failed' | 'rejected'

export interface WebhookEvent {
  id: string
  workflowId: string
  status: WebhookEventStatus
  runId?: string
  error?: string
  body?: unknown
  headers?: unknown
  receivedAt: string
}

/** Response of POST /webhook/{id} and POST /api/webhook-events/{id}/replay. */
export interface WebhookTriggerResult {
  eventId: string
  runId: string
  status: 'running' | 'success' | 'failed'
  output?: unknown
  error?: string
}
