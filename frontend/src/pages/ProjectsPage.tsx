import { useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Project } from '../api/types'

export default function ProjectsPage() {
  const [projects, setProjects] = useState<Project[]>([])
  const [error, setError] = useState<string>()
  const [busyId, setBusyId] = useState<string>()
  const [formOpen, setFormOpen] = useState(false)
  const [editing, setEditing] = useState<Project>()
  const [form, setForm] = useState({ name: '', description: '' })
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    try {
      setProjects(await api.listProjects())
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const openCreate = () => {
    setEditing(undefined)
    setForm({ name: '', description: '' })
    setFormOpen(true)
    setError(undefined)
  }

  const openEdit = (project: Project) => {
    setEditing(project)
    setForm({ name: project.name, description: project.description ?? '' })
    setFormOpen(true)
    setError(undefined)
  }

  const save = async () => {
    setError(undefined)
    if (!form.name.trim()) {
      setError('Project name is required.')
      return
    }
    setSaving(true)
    try {
      if (editing) await api.updateProject(editing.id, { name: form.name.trim(), description: form.description.trim() || undefined })
      else await api.createProject({ name: form.name.trim(), description: form.description.trim() || undefined })
      setFormOpen(false)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setSaving(false)
    }
  }

  const remove = async (project: Project) => {
    setError(undefined)
    const hasWorkflows = project.workflowCount > 0
    const message = hasWorkflows
      ? `"${project.name}" still contains ${project.workflowCount} workflow(s) — the backend will refuse this. Move or delete them first. Delete anyway?`
      : `Delete project "${project.name}"?`
    if (!window.confirm(message)) return
    setBusyId(project.id)
    try {
      await api.deleteProject(project.id)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusyId(undefined)
    }
  }

  return (
    <div className="page">
      <h1>Projects</h1>
      <p style={{ marginTop: -6, color: '#64748b', fontSize: 13 }}>
        Projects group workflows for segregation (filters on the Workflows and Dashboard pages).
        They are organizational only — who can see or edit a workflow still follows the
        owner / sharing / admin rules.
      </p>
      {error && !formOpen && <div className="error-text">{error}</div>}

      <div className="toolbar-row">
        <button type="button" className="primary" onClick={openCreate}>
          + New project
        </button>
      </div>

      <table className="data">
        <thead>
          <tr>
            <th>Name</th>
            <th>Description</th>
            <th>Workflows</th>
            <th>Updated</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {projects.map((p) => (
            <tr key={p.id}>
              <td>{p.name}</td>
              <td>{p.description}</td>
              <td>{p.workflowCount}</td>
              <td>{new Date(p.updatedAt).toLocaleString()}</td>
              <td>
                <button type="button" disabled={busyId === p.id} onClick={() => openEdit(p)}>
                  Edit
                </button>
                <button type="button" className="danger" disabled={busyId === p.id} onClick={() => void remove(p)}>
                  Delete
                </button>
              </td>
            </tr>
          ))}
          {projects.length === 0 && (
            <tr>
              <td colSpan={5}>No projects yet — create one and assign workflows to it.</td>
            </tr>
          )}
        </tbody>
      </table>

      {formOpen && (
        <div className="modal-backdrop">
          {/* Deliberately NOT closing on backdrop click: typed form data should not be lost by a stray click. */}
          <div className="modal modal-form">
            <h3>{editing ? 'Edit project' : 'New project'}</h3>
            <label>Name</label>
            <input
              value={form.name}
              placeholder="e.g. Facilities Integrations"
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
            />
            <label>Description</label>
            <input
              value={form.description}
              placeholder="What belongs in this project?"
              onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))}
            />
            {error && <div className="error-text">{error}</div>}
            <div className="modal-actions">
              <button type="button" onClick={() => setFormOpen(false)}>
                Cancel
              </button>
              <button type="button" className="primary" disabled={saving} onClick={() => void save()}>
                {editing ? 'Save changes' : 'Create project'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
