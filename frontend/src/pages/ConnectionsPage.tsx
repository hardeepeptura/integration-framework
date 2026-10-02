import { useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Connection, ConnectionTestResult, DbType } from '../api/types'

const EMPTY_FORM = {
  name: '',
  kind: 'http' as Connection['kind'],
  baseUrl: '',
  authType: 'none' as Connection['authType'],
  authConfigJson: '{}',
  dbType: 'mssql' as DbType,
  host: '',
  port: '',
  database: '',
  user: '',
  passwordEnv: '',
  sslMode: '',
}

export default function ConnectionsPage() {
  const [connections, setConnections] = useState<Connection[]>([])
  const [form, setForm] = useState(EMPTY_FORM)
  const [formOpen, setFormOpen] = useState(false)
  const [editingId, setEditingId] = useState<string>()
  const [error, setError] = useState<string>()
  const [testResults, setTestResults] = useState<Record<string, ConnectionTestResult | 'testing'>>({})

  const load = useCallback(async () => {
    try {
      setConnections(await api.listConnections())
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const resetForm = () => {
    setForm(EMPTY_FORM)
    setEditingId(undefined)
    setFormOpen(false)
    setError(undefined)
  }

  const openCreate = () => {
    setForm(EMPTY_FORM)
    setEditingId(undefined)
    setFormOpen(true)
    setError(undefined)
  }

  const submit = async () => {
    setError(undefined)
    let authConfig: unknown
    try {
      authConfig = JSON.parse(form.authConfigJson)
    } catch {
      setError('Auth config is not valid JSON.')
      return
    }

    const isDb = form.kind === 'db'
    const body: Partial<Connection> = {
      name: form.name,
      kind: form.kind,
      baseUrl: isDb ? undefined : form.baseUrl || undefined,
      authType: isDb ? 'none' : form.authType,
      authConfig: isDb || form.authType === 'none' ? undefined : (authConfig as Record<string, unknown>),
      dbType: isDb ? form.dbType : undefined,
      dbConfig: isDb
        ? {
            db_type: form.dbType,
            host: form.host,
            ...(form.port ? { port: Number(form.port) } : {}),
            database: form.database,
            user: form.user,
            password_env: form.passwordEnv,
            ...(form.sslMode ? { ssl_mode: form.sslMode } : {}),
          }
        : undefined,
    }

    try {
      if (editingId) await api.updateConnection(editingId, body)
      else await api.createConnection(body)
      resetForm()
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  const edit = (connection: Connection) => {
    setEditingId(connection.id)
    setError(undefined)
    const dbConfig = (connection.dbConfig ?? {}) as Record<string, unknown>
    setForm({
      name: connection.name,
      kind: connection.kind,
      baseUrl: connection.baseUrl ?? '',
      authType: connection.authType,
      authConfigJson: JSON.stringify(connection.authConfig ?? {}, null, 2),
      dbType: connection.dbType ?? 'mssql',
      host: typeof dbConfig['host'] === 'string' ? dbConfig['host'] : '',
      port: dbConfig['port'] !== undefined ? String(dbConfig['port']) : '',
      database: typeof dbConfig['database'] === 'string' ? dbConfig['database'] : '',
      user: typeof dbConfig['user'] === 'string' ? dbConfig['user'] : '',
      passwordEnv: typeof dbConfig['password_env'] === 'string' ? dbConfig['password_env'] : '',
      sslMode: typeof dbConfig['ssl_mode'] === 'string' ? dbConfig['ssl_mode'] : '',
    })
    setFormOpen(true)
  }

  const remove = async (connection: Connection) => {
    if (!window.confirm(`Delete connection "${connection.name}"?`)) return
    try {
      await api.deleteConnection(connection.id)
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  const test = async (connection: Connection) => {
    setTestResults((r) => ({ ...r, [connection.id]: 'testing' }))
    try {
      const result = await api.testConnection(connection.id)
      setTestResults((r) => ({ ...r, [connection.id]: result }))
    } catch (e) {
      setTestResults((r) => ({ ...r, [connection.id]: { success: false, detail: String(e) } }))
    }
  }

  const setField = (patch: Partial<typeof EMPTY_FORM>) => setForm((f) => ({ ...f, ...patch }))
  const isDb = form.kind === 'db'

  return (
    <div className="page">
      <h1>Connections</h1>
      {error && !formOpen && <div className="error-text">{error}</div>}

      <div className="toolbar-row">
        <button type="button" className="primary" onClick={openCreate}>
          + New connection
        </button>
      </div>

      <table className="data">
        <thead>
          <tr>
            <th>Name</th>
            <th>Kind</th>
            <th>Target</th>
            <th>Auth / Type</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {connections.map((c) => (
            <tr key={c.id}>
              <td>{c.name}</td>
              <td>{c.kind}</td>
              <td style={{ maxWidth: 300, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {c.kind === 'http' ? c.baseUrl : `${c.dbType} • ${(c.dbConfig as { host?: string })?.host ?? ''}`}
              </td>
              <td>
                {c.kind === 'http' ? c.authType : c.dbType}
                {testResults[c.id] && testResults[c.id] !== 'testing' && (
                  <div style={{ marginTop: 4 }}>
                    <span className={`ok-text`} style={{ color: (testResults[c.id] as ConnectionTestResult).success ? '#16a34a' : '#dc2626' }}>
                      {(testResults[c.id] as ConnectionTestResult).detail}
                    </span>
                  </div>
                )}
              </td>
              <td>
                <button type="button" onClick={() => edit(c)}>Edit</button>
                <button type="button" className="primary" onClick={() => void test(c)}>
                  {testResults[c.id] === 'testing' ? 'Testing…' : 'Test connection'}
                </button>
                <button type="button" className="danger" onClick={() => void remove(c)}>Delete</button>
              </td>
            </tr>
          ))}
          {connections.length === 0 && (
            <tr>
              <td colSpan={5}>No connections yet — add one with “New connection”.</td>
            </tr>
          )}
        </tbody>
      </table>

      {formOpen && (
        <div className="modal-backdrop">
          {/* Deliberately NOT closing on backdrop click: typed form data should not be lost by a stray click. */}
          <div className="modal modal-form">
            <h3>{editingId ? 'Edit connection' : 'New connection'}</h3>
            <label>Name</label>
            <input value={form.name} onChange={(e) => setField({ name: e.target.value })} />
            <label>Kind</label>
            <select value={form.kind} onChange={(e) => setField({ kind: e.target.value as Connection['kind'] })}>
              <option value="http">HTTP</option>
              <option value="db">Database</option>
            </select>

            {isDb ? (
              <>
                <label>Database type</label>
                <select value={form.dbType} onChange={(e) => setField({ dbType: e.target.value as DbType })}>
                  <option value="mssql">SQL Server / Azure SQL</option>
                  <option value="postgres">PostgreSQL</option>
                  <option value="mysql">MySQL</option>
                </select>
                <label>Host</label>
                <input value={form.host} placeholder="myserver.database.windows.net" onChange={(e) => setField({ host: e.target.value })} />
                <label>Port (blank = default)</label>
                <input value={form.port} placeholder="1433 / 5432 / 3306" onChange={(e) => setField({ port: e.target.value })} />
                <label>Database</label>
                <input value={form.database} onChange={(e) => setField({ database: e.target.value })} />
                <label>User</label>
                <input value={form.user} onChange={(e) => setField({ user: e.target.value })} />
                <label>Password environment variable name (never the password itself)</label>
                <input value={form.passwordEnv} placeholder="MY_DB_PASSWORD" onChange={(e) => setField({ passwordEnv: e.target.value })} />
                <label>SSL mode (PostgreSQL/MySQL, optional)</label>
                <input value={form.sslMode} placeholder="Require" onChange={(e) => setField({ sslMode: e.target.value })} />
              </>
            ) : (
              <>
                <label>Base URL</label>
                <input value={form.baseUrl} placeholder="https://api.example.com" onChange={(e) => setField({ baseUrl: e.target.value })} />
                <label>Auth type</label>
                <select value={form.authType} onChange={(e) => setField({ authType: e.target.value as Connection['authType'] })}>
                  <option value="none">None</option>
                  <option value="api_key">API key header</option>
                  <option value="bearer">Bearer token</option>
                  <option value="basic">Basic</option>
                </select>
                {form.authType !== 'none' && (
                  <>
                    <label>
                      Auth config (JSON; secrets via *_env variable NAMES, e.g.{' '}
                      {'{"token_env": "MY_TOKEN"}'})
                    </label>
                    <textarea value={form.authConfigJson} onChange={(e) => setField({ authConfigJson: e.target.value })} />
                  </>
                )}
              </>
            )}

            {error && <div className="error-text">{error}</div>}
            <div className="modal-actions">
              <button type="button" onClick={resetForm}>Cancel</button>
              <button type="button" className="primary" onClick={() => void submit()}>
                {editingId ? 'Save changes' : 'Create connection'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
