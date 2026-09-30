import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { Workflow } from '../api/types'

const EMPTY_NEW = { name: '', description: '', triggerType: 'manual' as 'manual' | 'webhook' | 'schedule' }

export default function WorkflowsPage() {
  const [workflows, setWorkflows] = useState<Workflow[]>([])
  const [error, setError] = useState<string>()
  const [busyId, setBusyId] = useState<string>()
  const [showCreate, setShowCreate] = useState(false)
  const [newWf, setNewWf] = useState(EMPTY_NEW)
  const [creating, setCreating] = useState(false)
  const navigate = useNavigate()

  const load = useCallback(async () => {
    try {
      setWorkflows(await api.listWorkflows())
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const runNow = async (wf: Workflow) => {
    setBusyId(wf.id)
    setError(undefined)
    try {
      await api.runWorkflow(wf.id, {})
      navigate(`/runs`)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusyId(undefined)
    }
  }

  const toggle = async (wf: Workflow) => {
    setBusyId(wf.id)
    try {
      await api.updateWorkflow(wf.id, { enabled: !wf.enabled })
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusyId(undefined)
    }
  }

  const remove = async (wf: Workflow) => {
    if (!window.confirm(`Delete workflow "${wf.name}" and its run history?`)) return
    setBusyId(wf.id)
    try {
      await api.deleteWorkflow(wf.id)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusyId(undefined)
    }
  }

  const create = async () => {
    setError(undefined)
    if (!newWf.name.trim()) {
      setError('Workflow name is required.')
      return
    }
    setCreating(true)
    try {
      const created = await api.createWorkflow({
        name: newWf.name.trim(),
        description: newWf.description.trim() || undefined,
        graph: {
          nodes: [
            { id: 'trigger', type: 'trigger', config: { trigger: newWf.triggerType } },
          ],
        },
      })
      navigate(`/workflows/${created.id}/builder`)
    } catch (e) {
      setError(String(e))
      setCreating(false)
    }
  }

  return (
    <div className="page">
      <h1>Workflows</h1>
      {error && <div className="error-text">{error}</div>}
      <div className="toolbar-row">
        <button type="button" className="primary" onClick={() => setShowCreate((s) => !s)}>
          {showCreate ? 'Close' : '+ New workflow'}
        </button>
      </div>
      {showCreate && (
        <div style={{ maxWidth: 520, background: 'var(--panel)', padding: 20, borderRadius: 8, boxShadow: '0 1px 3px rgb(0 0 0 / 8%)', marginBottom: 20 }}>
          <label>Name</label>
          <input
            value={newWf.name}
            placeholder="e.g. CRM lead to Inventory reserve"
            onChange={(e) => setNewWf((f) => ({ ...f, name: e.target.value }))}
          />
          <label>Description</label>
          <input
            value={newWf.description}
            placeholder="What does this workflow do?"
            onChange={(e) => setNewWf((f) => ({ ...f, description: e.target.value }))}
          />
          <label>Trigger type</label>
          <select
            value={newWf.triggerType}
            onChange={(e) => setNewWf((f) => ({ ...f, triggerType: e.target.value as typeof newWf.triggerType }))}
          >
            <option value="manual">Manual</option>
            <option value="webhook">Webhook</option>
            <option value="schedule">Schedule</option>
          </select>
          <div style={{ marginTop: 16 }}>
            <button type="button" className="primary" disabled={creating} onClick={() => void create()}>
              {creating ? 'Creating…' : 'Create and open builder'}
            </button>
          </div>
        </div>
      )}
      <table className="data">
        <thead>
          <tr>
            <th>Name</th>
            <th>Description</th>
            <th>Status</th>
            <th>Updated</th>
            <th style={{ width: 340 }}></th>
          </tr>
        </thead>
        <tbody>
          {workflows.map((wf) => (
            <tr key={wf.id}>
              <td>{wf.name}</td>
              <td>{wf.description}</td>
              <td>
                <span className={`badge ${wf.enabled ? 'enabled' : 'disabled'}`}>
                  {wf.enabled ? 'enabled' : 'disabled'}
                </span>
              </td>
              <td>{new Date(wf.updatedAt).toLocaleString()}</td>
              <td>
                <Link to={`/workflows/${wf.id}/builder`}>
                  <button type="button">Open builder</button>
                </Link>
                <button type="button" className="primary" disabled={busyId === wf.id || !wf.enabled} onClick={() => void runNow(wf)}>
                  Run now
                </button>
                <button type="button" disabled={busyId === wf.id} onClick={() => void toggle(wf)}>
                  {wf.enabled ? 'Disable' : 'Enable'}
                </button>
                <button type="button" className="danger" disabled={busyId === wf.id} onClick={() => void remove(wf)}>
                  Delete
                </button>
              </td>
            </tr>
          ))}
          {workflows.length === 0 && (
            <tr>
              <td colSpan={5}>No workflows yet.</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}
