import { Fragment, useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Run, Workflow } from '../api/types'

const PAGE_SIZES = [50, 100, 200, 500]

function JsonView({ value }: { value: unknown }) {
  if (value === undefined || value === null) return <div className="json-view">(empty)</div>
  return <div className="json-view">{JSON.stringify(value, null, 2)}</div>
}

export default function RunsPage() {
  const [workflows, setWorkflows] = useState<Workflow[]>([])
  const [workflowFilter, setWorkflowFilter] = useState<string>('')
  const [runs, setRuns] = useState<Run[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(100)
  const [expanded, setExpanded] = useState<Run>()
  const [error, setError] = useState<string>()

  // Search-as-you-type (debounced) over workflow name, status, error text or an
  // exact run id — all matched server-side.
  const [searchText, setSearchText] = useState('')
  const [search, setSearch] = useState('')
  useEffect(() => {
    const timer = setTimeout(() => {
      setSearch(searchText.trim())
      setPage(1)
    }, 400)
    return () => clearTimeout(timer)
  }, [searchText])

  const load = useCallback(async () => {
    try {
      const result = await api.listRuns({
        workflowId: workflowFilter || undefined,
        search: search || undefined,
        page,
        pageSize,
      })
      setRuns(result.items)
      setTotal(result.total)
    } catch (e) {
      setError(String(e))
    }
  }, [workflowFilter, search, page, pageSize])

  useEffect(() => {
    void api
      .listWorkflows()
      .then(setWorkflows)
      .catch((e) => setError(String(e)))
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  // Runs execute asynchronously on the worker dispatcher — auto-refresh so
  // newly queued runs appear and running ones flip to success/failed on their own.
  useEffect(() => {
    const interval = setInterval(() => {
      void load()
    }, 5000)
    return () => clearInterval(interval)
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

  const totalPages = Math.max(1, Math.ceil(total / pageSize))
  const showingFrom = total === 0 ? 0 : (page - 1) * pageSize + 1
  const showingTo = Math.min(page * pageSize, total)

  return (
    <div className="page">
      <h1>Runs</h1>
      <div className="toolbar-row">
        <input
          style={{ width: 260 }}
          value={searchText}
          placeholder="Search: workflow name, status, error, run id"
          onChange={(e) => setSearchText(e.target.value)}
        />
        <select value={workflowFilter} onChange={(e) => { setWorkflowFilter(e.target.value); setPage(1) }}>
          <option value="">All workflows</option>
          {workflows.map((w) => (
            <option key={w.id} value={w.id}>
              {w.name}
            </option>
          ))}
        </select>
        <select
          value={pageSize}
          title="Rows per page"
          onChange={(e) => { setPageSize(Number(e.target.value)); setPage(1) }}
        >
          {PAGE_SIZES.map((s) => (
            <option key={s} value={s}>
              {s} / page
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
              <td colSpan={6}>No runs match{search ? ` “${search}”` : ''} — trigger a workflow to see history.</td>
            </tr>
          )}
        </tbody>
      </table>

      <div className="toolbar-row" style={{ marginTop: 12 }}>
        <span style={{ fontSize: 13, color: '#64748b' }}>
          Showing {showingFrom}–{showingTo} of {total}
        </span>
        <button type="button" disabled={page <= 1} onClick={() => setPage((p) => Math.max(1, p - 1))}>
          ← Prev
        </button>
        <span style={{ fontSize: 13 }}>
          Page {page} / {totalPages}
        </span>
        <button type="button" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>
          Next →
        </button>
      </div>
    </div>
  )
}
