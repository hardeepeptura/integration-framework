import { useCallback, useEffect, useState } from 'react'
import { api, NO_PROJECT } from '../api/client'
import type { DashboardRange, DashboardSummary, Project } from '../api/types'

const RANGES: { value: DashboardRange; label: string }[] = [
  { value: 'hour', label: 'Last hour' },
  { value: '24h', label: 'Last 24 hours' },
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: '6m', label: 'Last 6 months' },
]

function bucketLabel(iso: string, range: DashboardRange): string {
  const d = new Date(iso)
  switch (range) {
    case 'hour':
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    case '24h':
      return d.toLocaleTimeString([], { hour: '2-digit' })
    case '7d':
    case '30d':
      return d.toLocaleDateString([], { day: 'numeric', month: 'short' })
    default:
      return d.toLocaleDateString([], { month: 'short' })
  }
}

export default function DashboardPage() {
  const [range, setRange] = useState<DashboardRange>('6m')
  const [projects, setProjects] = useState<Project[]>([])
  const [projectFilter, setProjectFilter] = useState('') // '' = all, NO_PROJECT = unassigned
  const [summary, setSummary] = useState<DashboardSummary>()
  const [error, setError] = useState<string>()

  const load = useCallback(async () => {
    try {
      setSummary(await api.dashboardSummary(range, projectFilter || undefined))
      setError(undefined)
    } catch (e) {
      setError(String(e))
    }
  }, [range, projectFilter])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    void api
      .listProjects()
      .then(setProjects)
      .catch(() => undefined)
  }, [])

  const max = Math.max(1, ...(summary?.buckets.map((b) => b.total) ?? [1]))

  return (
    <div className="page">
      <h1>Dashboard</h1>
      {error && <div className="error-text">{error}</div>}
      <div className="toolbar-row">
        <select value={range} onChange={(e) => setRange(e.target.value as DashboardRange)}>
          {RANGES.map((r) => (
            <option key={r.value} value={r.value}>
              {r.label}
            </option>
          ))}
        </select>
        <select
          value={projectFilter}
          onChange={(e) => setProjectFilter(e.target.value)}
          title="Scope the dashboard to a project"
        >
          <option value="">All projects</option>
          <option value={NO_PROJECT}>No project</option>
          {projects.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
        <button type="button" onClick={() => void load()}>
          Refresh
        </button>
      </div>

      <div className="kpi-row">
        <div className="kpi-card">
          <div className="kpi-value">{summary?.workflowCount ?? '—'}</div>
          <div className="kpi-label">Workflows</div>
        </div>
        <div className="kpi-card">
          <div className="kpi-value">{summary?.totalRuns ?? '—'}</div>
          <div className="kpi-label">Total runs</div>
        </div>
        <div className="kpi-card ok">
          <div className="kpi-value">{summary ? `${summary.successRate}%` : '—'}</div>
          <div className="kpi-label">Pass rate</div>
        </div>
        <div className="kpi-card bad">
          <div className="kpi-value">{summary ? `${summary.failureRate}%` : '—'}</div>
          <div className="kpi-label">Fail rate</div>
        </div>
      </div>

      <div className="chart">
        <h3>Runs over time (success / failed)</h3>
        <div className="bars">
          {summary?.buckets.map((b, i) => (
            <div className="bar-col" key={i} title={`${b.total} runs (${b.success} passed, ${b.failed} failed)`}>
              <div className="bar-stack">
                <div className="bar-seg bar-seg-success" style={{ height: `${(b.success / max) * 100}%` }} />
                <div className="bar-seg bar-seg-failed" style={{ height: `${(b.failed / max) * 100}%` }} />
              </div>
              <div className="bar-label">{bucketLabel(b.end, summary.range)}</div>
            </div>
          ))}
          {summary && summary.buckets.length === 0 && <div>No data in this window.</div>}
        </div>
        <div className="chart-legend">
          <span>
            <span className="legend-dot" style={{ background: 'var(--ok)' }} /> Passed
          </span>
          <span>
            <span className="legend-dot" style={{ background: 'var(--danger)' }} /> Failed
          </span>
        </div>
      </div>

      <h3 style={{ margin: '4px 0 10px', fontSize: 15 }}>Busiest workflows</h3>
      <table className="data">
        <thead>
          <tr>
            <th>Workflow</th>
            <th>Runs</th>
            <th>Passed</th>
            <th>Failed</th>
          </tr>
        </thead>
        <tbody>
          {summary?.topWorkflows.map((w) => (
            <tr key={w.workflowId}>
              <td>{w.name}</td>
              <td>{w.total}</td>
              <td>{w.success}</td>
              <td>{w.failed}</td>
            </tr>
          ))}
          {(!summary || summary.topWorkflows.length === 0) && (
            <tr>
              <td colSpan={4}>No runs in this window.</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}
