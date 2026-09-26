import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { Workflow } from '../api/types'

export default function WorkflowsPage() {
  const [workflows, setWorkflows] = useState<Workflow[]>([])
  const [error, setError] = useState<string>()
  const [busyId, setBusyId] = useState<string>()
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

  return (
    <div className="page">
      <h1>Workflows</h1>
      {error && <div className="error-text">{error}</div>}
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
