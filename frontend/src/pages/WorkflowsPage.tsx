import { Fragment, useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, NO_PROJECT } from '../api/client'
import type { Project, SharePermission, Workflow, WorkflowShare } from '../api/types'

const EMPTY_NEW = {
  name: '',
  description: '',
  triggerType: 'manual' as 'manual' | 'webhook' | 'schedule',
  projectId: '', // '' = unassigned
}

const iconProps = {
  viewBox: '0 0 24 24',
  'aria-hidden': true as const,
  width: 13,
  height: 13,
  style: { flexShrink: 0 },
}
const IconCode = () => (
  <svg {...iconProps} fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="m8 6-6 6 6 6M16 6l6 6-6 6" />
  </svg>
)
const IconPlay = () => (
  <svg {...iconProps} fill="currentColor" stroke="none">
    <polygon points="6 3 21 12 6 21" />
  </svg>
)
const IconPause = () => (
  <svg {...iconProps} fill="currentColor" stroke="none">
    <rect x="5" y="3" width="5" height="18" rx={1} />
    <rect x="14" y="3" width="5" height="18" rx={1} />
  </svg>
)
const IconTrash = () => (
  <svg {...iconProps} fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
  </svg>
)
const IconShare = () => (
  <svg {...iconProps} fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
    <circle cx="18" cy="5" r="3" />
    <circle cx="6" cy="12" r="3" />
    <circle cx="18" cy="19" r="3" />
    <path d="m8.6 13.5 6.8 4M15.4 6.5l-6.8 4" />
  </svg>
)

export default function WorkflowsPage() {
  const [workflows, setWorkflows] = useState<Workflow[]>([])
  const [error, setError] = useState<string>()
  const [busyId, setBusyId] = useState<string>()
  const [showCreate, setShowCreate] = useState(false)
  const [newWf, setNewWf] = useState(EMPTY_NEW)
  const [creating, setCreating] = useState(false)
  const [shareWf, setShareWf] = useState<Workflow>()
  const [shares, setShares] = useState<WorkflowShare[]>([])
  const [shareEmail, setShareEmail] = useState('')
  const [sharePerm, setSharePerm] = useState<SharePermission>('view')
  const [projects, setProjects] = useState<Project[]>([])
  const [projectFilter, setProjectFilter] = useState('') // '' = all, NO_PROJECT = unassigned
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({})
  const navigate = useNavigate()

  const load = useCallback(async () => {
    try {
      setWorkflows(await api.listWorkflows(projectFilter || undefined))
    } catch (e) {
      setError(String(e))
    }
  }, [projectFilter])

  useEffect(() => {
    void api
      .listProjects()
      .then(setProjects)
      .catch((e) => setError(String(e)))
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  // Group the visible workflows by project (assigned projects first, then
  // unassigned) for the expand/collapse view.
  const groups = useMemo(() => {
    const byProject = new Map<string, Workflow[]>()
    for (const wf of workflows) {
      const key = wf.projectId ?? 'none'
      if (!byProject.has(key)) byProject.set(key, [])
      byProject.get(key)!.push(wf)
    }
    const result: { key: string; label: string; workflows: Workflow[] }[] = []
    for (const p of projects) {
      if (byProject.has(p.id)) result.push({ key: p.id, label: p.name, workflows: byProject.get(p.id)! })
    }
    if (byProject.has('none')) result.push({ key: 'none', label: 'No project', workflows: byProject.get('none')! })
    return result
  }, [workflows, projects])

  const toggleGroup = (key: string) => setCollapsed((c) => ({ ...c, [key]: !c[key] }))

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

  const openShare = async (wf: Workflow) => {
    setError(undefined)
    setShareWf(wf)
    setShareEmail('')
    setSharePerm('view')
    try {
      setShares(await api.listShares(wf.id))
    } catch (e) {
      setError(String(e))
    }
  }

  const addShare = async () => {
    if (!shareWf || !shareEmail.trim()) return
    setError(undefined)
    try {
      await api.upsertShare(shareWf.id, shareEmail.trim(), sharePerm)
      setShares(await api.listShares(shareWf.id))
      setShareEmail('')
    } catch (e) {
      setError(String(e))
    }
  }

  const changeShare = async (share: WorkflowShare, permission: SharePermission) => {
    if (!shareWf) return
    setError(undefined)
    try {
      await api.upsertShare(shareWf.id, share.email, permission)
      setShares(await api.listShares(shareWf.id))
    } catch (e) {
      setError(String(e))
    }
  }

  const removeShare = async (share: WorkflowShare) => {
    if (!shareWf) return
    setError(undefined)
    try {
      await api.removeShare(shareWf.id, share.id)
      setShares(await api.listShares(shareWf.id))
    } catch (e) {
      setError(String(e))
    }
  }

  const assignProject = async (wf: Workflow, projectId: string) => {
    setError(undefined)
    try {
      // NO_PROJECT (the all-zeros guid) explicitly unassigns on the backend.
      await api.updateWorkflow(wf.id, { projectId })
      await load()
      // Keep the project-card counts fresh after a move.
      void api.listProjects().then(setProjects).catch(() => undefined)
    } catch (e) {
      setError(String(e))
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
        projectId: newWf.projectId || undefined,
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
        <select
          value={projectFilter}
          onChange={(e) => setProjectFilter(e.target.value)}
          title="Filter workflows by project"
        >
          <option value="">All projects</option>
          <option value={NO_PROJECT}>No project</option>
          {projects.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name} ({p.workflowCount})
            </option>
          ))}
        </select>
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
          <label>Project</label>
          <select
            value={newWf.projectId}
            onChange={(e) => setNewWf((f) => ({ ...f, projectId: e.target.value }))}
          >
            <option value="">No project</option>
            {projects.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
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
            <th>Project</th>
            <th>Status</th>
            <th>Owner</th>
            <th>Updated</th>
            <th style={{ width: 440 }}></th>
          </tr>
        </thead>
        <tbody>
          {groups.map((g) => (
            <Fragment key={g.key}>
              <tr className="group-row" onClick={() => toggleGroup(g.key)} title="Click to expand/collapse">
                <td colSpan={7}>
                  <span className={`chevron${collapsed[g.key] ? ' collapsed' : ''}`}>▾</span>
                  {g.label}
                  <span className="group-count">{g.workflows.length}</span>
                </td>
              </tr>
              {!collapsed[g.key] &&
                g.workflows.map((wf) => {
                  // myPermission null = SSO off (unrestricted, as before roles existed).
                  const canManage = wf.myPermission == null || wf.myPermission === 'manage'
                  const canEdit = canManage || wf.myPermission === 'edit'
                  return (
                    <tr key={wf.id}>
                      <td>{wf.name}</td>
                      <td>{wf.description}</td>
                      <td>
                        {/* Inline reassignment; grouping only, still gated by the edit permission. */}
                        <select
                          style={{ width: 150 }}
                          disabled={!canEdit}
                          value={wf.projectId ?? NO_PROJECT}
                          title={canEdit ? 'Move this workflow to a project' : 'No edit permission'}
                          onChange={(e) => void assignProject(wf, e.target.value)}
                        >
                          <option value={NO_PROJECT}>No project</option>
                          {projects.map((p) => (
                            <option key={p.id} value={p.id}>
                              {p.name}
                            </option>
                          ))}
                        </select>
                      </td>
                      <td>
                        <span className={`badge ${wf.enabled ? 'enabled' : 'disabled'}`}>
                          {wf.enabled ? 'enabled' : 'disabled'}
                        </span>
                      </td>
                      <td className="owner-chip">{wf.ownerEmail ?? '—'}</td>
                      <td>{new Date(wf.updatedAt).toLocaleString()}</td>
                      <td>
                        <div className="row-actions">
                          <button type="button" onClick={() => navigate(`/workflows/${wf.id}/builder`)}>
                            <IconCode /> Open builder
                          </button>
                          {canEdit && (
                            <button type="button" className="primary" disabled={busyId === wf.id || !wf.enabled} onClick={() => void runNow(wf)}>
                              <IconPlay /> Run now
                            </button>
                          )}
                          {canEdit && (
                            <button type="button" disabled={busyId === wf.id} onClick={() => void toggle(wf)}>
                              {wf.enabled ? <IconPause /> : <IconPlay />} {wf.enabled ? 'Disable' : 'Enable'}
                            </button>
                          )}
                          {canManage && (
                            <button type="button" disabled={busyId === wf.id} onClick={() => void openShare(wf)}>
                              <IconShare /> Share
                            </button>
                          )}
                          {canManage && (
                            <button type="button" className="danger" disabled={busyId === wf.id} onClick={() => void remove(wf)}>
                              <IconTrash /> Delete
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  )
                })}
            </Fragment>
          ))}
          {workflows.length === 0 && (
            <tr>
              <td colSpan={7}>No workflows{projectFilter ? ' in this project' : ' yet'}.</td>
            </tr>
          )}
        </tbody>
      </table>

      {shareWf && (
        <div className="modal-backdrop" onClick={() => setShareWf(undefined)}>
          <div className="modal" onClick={(e) => e.stopPropagation()}>
            <h3>Share “{shareWf.name}”</h3>
            {error && <div className="error-text">{error}</div>}
            <div className="share-row">
              <input
                style={{ flex: 1 }}
                placeholder="colleague@eptura.com"
                value={shareEmail}
                onChange={(e) => setShareEmail(e.target.value)}
              />
              <select value={sharePerm} onChange={(e) => setSharePerm(e.target.value as SharePermission)}>
                <option value="view">Can view</option>
                <option value="edit">Can edit</option>
              </select>
              <button type="button" className="primary" onClick={() => void addShare()}>
                Add
              </button>
            </div>
            {shares.map((s) => (
              <div className="share-row" key={s.id}>
                <span className="share-email">{s.email}</span>
                <select value={s.permission} onChange={(e) => void changeShare(s, e.target.value as SharePermission)}>
                  <option value="view">Can view</option>
                  <option value="edit">Can edit</option>
                </select>
                <button type="button" className="danger" onClick={() => void removeShare(s)}>
                  Remove
                </button>
              </div>
            ))}
            {shares.length === 0 && <p style={{ color: '#64748b', fontSize: 13 }}>Not shared with anyone yet.</p>}
            <div className="modal-actions">
              <button type="button" onClick={() => setShareWf(undefined)}>
                Done
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
