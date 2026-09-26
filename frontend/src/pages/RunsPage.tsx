import { Fragment, useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Run, Workflow } from '../api/types'

function JsonView({ value }: { value: unknown }) {
  if (value === undefined || value === null) return <div className="json-view">(empty)</div>
  return <div className="json-view">{JSON.stringify(value, null, 2)}</div>
}

export default function RunsPage() {
  const [workflows, setWorkflows] = useState<Workflow[]>([])
  const [workflowFilter, setWorkflowFilter] = useState<string>('')
  const [runs, setRuns] = useState<Run[]>([])
  const [expanded, setExpanded] = useState<Run>()
  const [error, setError] = useState<string>()

  const load = useCallback(async () => {
    try {
      setRuns(await api.listRuns(workflowFilter || undefined))
    } catch (e) {
      setError(String(e))
    }
  }, [workflowFilter])

  useEffect(() => {
    void api
      .listWorkflows()
      .then(setWorkflows)
      .catch((e) => setError(String(e)))
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const rerun = async (run: Run) => {
    setError(undefined)
    try {
      await api.rerunRun(run.id)
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  const toggleExpand = async (run: Run) => {
    if (expanded?.id === run.id) {
      setExpanded(undefined)
      return
    }
    try {
      setExpanded(await api.getRun(run.id))
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <div className="page">
      <h1>Runs</h1>
      <div className="toolbar-row">
        <select value={workflowFilter} onChange={(e) => setWorkflowFilter(e.target.value)}>
          <option value="">All workflows</option>
          {workflows.map((w) => (
            <option key={w.id} value={w.id}>
              {w.name}
            </option>
          ))}
        </select>
        <button type="button" onClick={() => void load()}>
          Refresh
        </button>
      </div>
      {error && <div className="error-text">{error}</div>}

      <table className="data">
        <thead>
          <tr>
            <th>Run ID</th>
            <th>Workflow</th>
            <th>Status</th>
            <th>Started</th>
            <th>Duration</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {runs.map((run) => {
            const workflow = workflows.find((w) => w.id === run.workflowId)
            const durationMs = run.finishedAt
              ? new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime()
              : undefined
            const isExpanded = expanded?.id === run.id
            return (
              <Fragment key={run.id}>
                <tr>
                  <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{run.id.slice(0, 8)}</td>
                  <td>{workflow?.name ?? run.workflowId.slice(0, 8)}</td>
                  <td>
                    <span className={`badge ${run.status}`}>{run.status}</span>
                  </td>
                  <td>{new Date(run.startedAt).toLocaleString()}</td>
                  <td>{durationMs !== undefined ? `${durationMs} ms` : '—'}</td>
                  <td>
                    <button type="button" onClick={() => void toggleExpand(run)}>
                      {isExpanded ? 'Hide' : 'Details'}
                    </button>
                    <button type="button" onClick={() => void rerun(run)}>
                      Rerun
                    </button>
                  </td>
                </tr>
                {isExpanded && (
                  <tr>
                    <td colSpan={6}>
                      {run.error && <div className="error-text">{run.error}</div>}
                      {expanded?.steps.map((step) => (
                        <div className="step-row" key={step.id}>
                          <h4>
                            {step.nodeId} <span className={`badge ${step.status}`}>{step.status}</span>
                            <span style={{ color: '#94a3b8', fontSize: 12 }}>{step.durationMs} ms</span>
                          </h4>
                          <div style={{ display: 'flex', gap: 12 }}>
                            <div style={{ flex: 1 }}>
                              <label>Input</label>
                              <JsonView value={step.input} />
                            </div>
                            <div style={{ flex: 1 }}>
                              <label>Output</label>
                              <JsonView value={step.status === 'failed' ? step.error : step.output} />
                            </div>
                          </div>
                        </div>
                      ))}
                    </td>
                  </tr>
                )}
              </Fragment>
            )
          })}
          {runs.length === 0 && (
            <tr>
              <td colSpan={6}>No runs yet — trigger a workflow to see history.</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}
