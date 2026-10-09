import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Connection, GraphNodeDef, WorkflowGraph } from '../api/types'
import { TYPE_LABELS, asStringArray, isValidNodeId } from '../builder/nodeDefs'

interface Props {
  node: GraphNodeDef
  graph: WorkflowGraph
  onChange: (config: Record<string, unknown>) => void
  onDelete: () => void
  /** Rename this step (its id) across the whole graph, including $.steps references. */
  onRename: (newId: string) => void
}

function JsonField({ label, value, onChange }: { label: string; value: unknown; onChange: (v: unknown) => void }) {
  const [text, setText] = useState(JSON.stringify(value ?? {}, null, 2))
  const [parseError, setParseError] = useState<string>()
  useEffect(() => {
    setText(JSON.stringify(value ?? {}, null, 2))
  }, [value])
  return (
    <div>
      <label>{label}</label>
      <textarea
        value={text}
        onChange={(e) => {
          setText(e.target.value)
          try {
            onChange(JSON.parse(e.target.value))
            setParseError(undefined)
          } catch {
            setParseError('Invalid JSON')
          }
        }}
      />
      {parseError && <div className="error-text">{parseError}</div>}
    </div>
  )
}

function NodeListPicker({
  label,
  graph,
  selected,
  onChange,
}: {
  label: string
  graph: WorkflowGraph
  selected: string[]
  onChange: (ids: string[]) => void
}) {
  return (
    <div>
      <label>{label}</label>
      {graph.nodes.map((n) => (
        <label key={n.id} style={{ display: 'flex', gap: 8, alignItems: 'center', margin: '4px 0' }}>
          <input
            type="checkbox"
            style={{ width: 'auto' }}
            checked={selected.includes(n.id)}
            onChange={(e) => {
              const next = e.target.checked ? [...selected, n.id] : selected.filter((x) => x !== n.id)
              onChange(next)
            }}
          />
          <span style={{ fontSize: 13 }}>{n.id} ({TYPE_LABELS[n.type]})</span>
        </label>
      ))}
    </div>
  )
}

export default function NodeConfigPanel({ node, graph, onChange, onDelete, onRename }: Props) {
  const [connections, setConnections] = useState<Connection[]>([])
  const [nameText, setNameText] = useState(node.id)
  const [renameError, setRenameError] = useState<string>()
  useEffect(() => {
    void api.listConnections().then(setConnections).catch(() => setConnections([]))
  }, [])

  // Applied on blur/Enter so typing does not thrash the graph on every keystroke.
  const applyRename = () => {
    const value = nameText.trim()
    if (value === node.id) {
      setRenameError(undefined)
      return
    }
    if (!value) {
      setRenameError('Step name cannot be empty.')
      return
    }
    if (!isValidNodeId(value)) {
      setRenameError('Letters, digits, "-" and "_" only — the name is used in $.steps.<name> references.')
      return
    }
    if (graph.nodes.some((n) => n.id === value)) {
      setRenameError(`"${value}" is already used by another step.`)
      return
    }
    setRenameError(undefined)
    onRename(value)
  }

  const config = node.config
  const set = (patch: Record<string, unknown>) => onChange({ ...config, ...patch })
  const str = (key: string) => (typeof config[key] === 'string' ? (config[key] as string) : '')
  const num = (key: string, fallback: number) =>
    typeof config[key] === 'number' ? (config[key] as number) : fallback

  const httpConnections = connections.filter((c) => c.kind === 'http')

  return (
    <div>
      <h3 style={{ marginTop: 0 }}>
        {TYPE_LABELS[node.type]} <span style={{ color: '#94a3b8', fontWeight: 400 }}>({node.id})</span>
      </h3>

      <label>Step name (used in {"$.steps.<name>"} references)</label>
      <input
        value={nameText}
        spellCheck={false}
        placeholder="e.g. fetch-orders"
        onChange={(e) => setNameText(e.target.value)}
        onBlur={applyRename}
        onKeyDown={(e) => {
          if (e.key === 'Enter') e.currentTarget.blur()
        }}
      />
      {renameError && <div className="error-text">{renameError}</div>}

      {node.type === 'trigger' && (
        <>
          <label>Trigger type</label>
          <select value={str('trigger') || 'manual'} onChange={(e) => set({ trigger: e.target.value })}>
            <option value="manual">Manual</option>
            <option value="webhook">Webhook</option>
            <option value="schedule">Schedule</option>
          </select>
          {str('trigger') === 'schedule' && (
            <>
              <label>Interval (seconds)</label>
              <input type="number" min={1} value={num('intervalSeconds', 60)} onChange={(e) => set({ intervalSeconds: Number(e.target.value) })} />
            </>
          )}
        </>
      )}

      {node.type === 'http_request' && (
        <>
          <label>URL (supports {"{$.paths}"} and "$." references)</label>
          <input value={str('url')} placeholder="https://api.example.com/orders" onChange={(e) => set({ url: e.target.value })} />
          <label>Method</label>
          <select value={str('method') || 'GET'} onChange={(e) => set({ method: e.target.value })}>
            {['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD'].map((m) => (
              <option key={m}>{m}</option>
            ))}
          </select>
          <label>HTTP connection (optional auth)</label>
          <select value={str('connectionId')} onChange={(e) => set({ connectionId: e.target.value || undefined })}>
            <option value="">— none —</option>
            {httpConnections.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
          <JsonField label="Headers" value={config['headers'] ?? {}} onChange={(v) => set({ headers: v })} />
          <JsonField label="Query parameters" value={config['query'] ?? {}} onChange={(v) => set({ query: v })} />
          <JsonField label="Body (JSON template)" value={config['body'] ?? null} onChange={(v) => set({ body: v })} />
          <label>Timeout (seconds)</label>
          <input type="number" min={1} value={num('timeoutSeconds', 30)} onChange={(e) => set({ timeoutSeconds: Number(e.target.value) })} />
        </>
      )}

      {node.type === 'db_query' && (
        <>
          <label>Database connection</label>
          <select value={str('connectionId')} onChange={(e) => set({ connectionId: e.target.value })}>
            <option value="">— select —</option>
            {connections
              .filter((c) => c.kind === 'db')
              .map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name} ({c.dbType})
                </option>
              ))}
          </select>
          <label>SQL (parameters as :name, mapped below)</label>
          <textarea value={str('sql')} placeholder="SELECT * FROM orders WHERE id = :orderId" onChange={(e) => set({ sql: e.target.value })} />
          <label>Mode</label>
          <select value={str('mode') || 'query'} onChange={(e) => set({ mode: e.target.value })}>
            <option value="query">Query (SELECT)</option>
            <option value="execute">Execute (INSERT/UPDATE/DELETE)</option>
          </select>
          <JsonField label="Parameters (name → value or '$.' path)" value={config['params'] ?? {}} onChange={(v) => set({ params: v })} />
          <label>Timeout (seconds)</label>
          <input type="number" min={1} value={num('timeoutSeconds', 30)} onChange={(e) => set({ timeoutSeconds: Number(e.target.value) })} />
        </>
      )}

      {node.type === 'transform' && (
        <JsonField label="Mapping (target key → value or '$.' path)" value={config['mapping'] ?? {}} onChange={(v) => set({ mapping: v })} />
      )}

      {node.type === 'condition' && (
        <>
          <label>Left operand ("$. path" or value)</label>
          <input value={str('left')} placeholder="$.steps.transform-1.score" onChange={(e) => set({ left: e.target.value })} />
          <label>Operator</label>
          <select value={str('operator') || 'eq'} onChange={(e) => set({ operator: e.target.value })}>
            {['eq', 'ne', 'gt', 'lt', 'contains', 'exists'].map((o) => (
              <option key={o}>{o}</option>
            ))}
          </select>
          <label>Right operand</label>
          <input value={String(config['right'] ?? '')} onChange={(e) => set({ right: e.target.value })} />
          <NodeListPicker label="On true — run nodes" graph={graph} selected={asStringArray(config['on_true'])} onChange={(ids) => set({ on_true: ids })} />
          <NodeListPicker label="On false — run nodes" graph={graph} selected={asStringArray(config['on_false'])} onChange={(ids) => set({ on_false: ids })} />
        </>
      )}

      {node.type === 'loop' && (
        <>
          <label>Source array ("$. path")</label>
          <input value={str('source')} placeholder="$.steps.http-1.body.items" onChange={(e) => set({ source: e.target.value })} />
          <NodeListPicker label="Body — run per item" graph={graph} selected={asStringArray(config['body'])} onChange={(ids) => set({ body: ids })} />
        </>
      )}

      {node.type === 'delay' && (
        <>
          <label>Delay (seconds)</label>
          <input type="number" min={0} value={num('seconds', 1)} onChange={(e) => set({ seconds: Number(e.target.value) })} />
        </>
      )}

      {node.type !== 'trigger' && (
        <>
          <label>Retry attempts</label>
          <input type="number" min={1} value={num('retry', 1)} onChange={(e) => set({ retry: { attempts: Number(e.target.value) } })} />
          <label>On error</label>
          <select value={str('onError') || 'stop'} onChange={(e) => set({ onError: e.target.value })}>
            <option value="stop">Stop the run</option>
            <option value="continue">Continue with next node</option>
          </select>
        </>
      )}

      <div style={{ marginTop: 20 }}>
        <button type="button" className="danger" onClick={onDelete}>
          Delete node
        </button>
      </div>
    </div>
  )
}
