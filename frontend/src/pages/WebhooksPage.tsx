import { useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { WebhookEvent, WebhookTriggerResult, Workflow } from '../api/types'

const DEFAULT_PAYLOAD = '{\n  "sku": "WIDGET-1",\n  "quantity": 2,\n  "name": "Ada Lovelace"\n}'

function ResultView({ result }: { result: WebhookTriggerResult }) {
  return (
    <div className="step-row">
      <h4>
        Run <span className={`badge ${result.status}`}>{result.status}</span>
      </h4>
      <div className="json-view">
        {JSON.stringify({ runId: result.runId, output: result.output, error: result.error }, null, 2)}
      </div>
    </div>
  )
}

export default function WebhooksPage() {
  const [workflows, setWorkflows] = useState<Workflow[]>([])
  const [workflowId, setWorkflowId] = useState('')
  const [payloadText, setPayloadText] = useState(DEFAULT_PAYLOAD)
  const [simulateResult, setSimulateResult] = useState<WebhookTriggerResult>()
  const [events, setEvents] = useState<WebhookEvent[]>([])
  const [statusFilter, setStatusFilter] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  const loadEvents = useCallback(async () => {
    try {
      setEvents(await api.listWebhookEvents({ status: statusFilter || undefined }))
    } catch (e) {
      setError(String(e))
    }
  }, [statusFilter])

  useEffect(() => {
    void api
      .listWorkflows()
      .then((all) => {
        setWorkflows(all)
        const webhookReady = all.find((w) => w.enabled)
        if (webhookReady && !workflowId) setWorkflowId(webhookReady.id)
      })
      .catch((e) => setError(String(e)))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    void loadEvents()
  }, [loadEvents])

  const simulate = async () => {
    setError(undefined)
    setSimulateResult(undefined)
    if (!workflowId) {
      setError('Pick a workflow to simulate against.')
      return
    }
    let payload: unknown
    try {
      payload = payloadText.trim() ? JSON.parse(payloadText) : {}
    } catch {
      setError('Payload must be valid JSON.')
      return
    }
    setBusy(true)
    try {
      const result = await api.triggerWebhook(workflowId, payload)
      setSimulateResult(result)
      await loadEvents()
    } catch (e) {
      setError(String(e))
      await loadEvents()
    } finally {
      setBusy(false)
    }
  }

  const replay = async (evt: WebhookEvent) => {
    setError(undefined)
    setBusy(true)
    try {
      const result = await api.replayWebhookEvent(evt.id)
      setSimulateResult(result)
      await loadEvents()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page">
      <h1>Webhooks</h1>

      <section>
        <h2>Simulate a delivery</h2>
        <div className="toolbar-row">
          <select value={workflowId} onChange={(e) => setWorkflowId(e.target.value)}>
            <option value="">Select workflow…</option>
            {workflows.map((w) => (
              <option key={w.id} value={w.id}>
                {w.name}
                {w.enabled ? '' : ' (disabled)'}
              </option>
            ))}
          </select>
          <button type="button" disabled={busy} onClick={() => void simulate()}>
            Send webhook
          </button>
        </div>
        <textarea
          rows={6}
          value={payloadText}
          onChange={(e) => setPayloadText(e.target.value)}
          spellCheck={false}
        />
        {simulateResult && <ResultView result={simulateResult} />}
      </section>

      <section>
        <h2>Durable deliveries</h2>
        <div className="toolbar-row">
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">All statuses</option>
            <option value="received">received</option>
            <option value="succeeded">succeeded</option>
            <option value="failed">failed</option>
            <option value="rejected">rejected</option>
          </select>
          <button type="button" onClick={() => void loadEvents()}>
            Refresh
          </button>
        </div>
        {error && <div className="error-text">{error}</div>}
        <table className="data">
          <thead>
            <tr>
              <th>Event ID</th>
              <th>Workflow</th>
              <th>Status</th>
              <th>Run</th>
              <th>Received</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {events.map((evt) => (
              <tr key={evt.id}>
                <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{evt.id.slice(0, 8)}</td>
                <td>{workflows.find((w) => w.id === evt.workflowId)?.name ?? evt.workflowId.slice(0, 8)}</td>
                <td>
                  <span className={`badge ${evt.status === 'succeeded' ? 'success' : evt.status}`}>
                    {evt.status}
                  </span>
                </td>
                <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{evt.runId?.slice(0, 8) ?? '—'}</td>
                <td>{new Date(evt.receivedAt).toLocaleString()}</td>
                <td>
                  <button type="button" disabled={busy} onClick={() => void replay(evt)}>
                    Replay
                  </button>
                </td>
              </tr>
            ))}
            {events.length === 0 && (
              <tr>
                <td colSpan={6}>No webhook deliveries yet — send one above.</td>
              </tr>
            )}
          </tbody>
        </table>
        {events.some((e) => e.error) && (
          <div style={{ marginTop: 8 }}>
            {events
              .filter((e) => e.error)
              .slice(0, 5)
              .map((e) => (
                <div className="error-text" key={e.id}>
                  {e.id.slice(0, 8)}: {e.error}
                </div>
              ))}
          </div>
        )}
      </section>
    </div>
  )
}
