import { useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { AppUser, UserRole } from '../api/types'

export default function UsersPage() {
  const [users, setUsers] = useState<AppUser[]>([])
  const [error, setError] = useState<string>()
  const [busyId, setBusyId] = useState<string>()

  const load = useCallback(async () => {
    try {
      setUsers(await api.listUsers())
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const changeRole = async (user: AppUser, role: UserRole) => {
    setBusyId(user.id)
    setError(undefined)
    try {
      await api.setUserRole(user.id, role)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusyId(undefined)
    }
  }

  const remove = async (user: AppUser) => {
    if (!window.confirm(`Remove ${user.email} from the platform?`)) return
    setBusyId(user.id)
    setError(undefined)
    try {
      await api.deleteUser(user.id)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusyId(undefined)
    }
  }

  return (
    <div className="page">
      <h1>Users</h1>
      <p style={{ marginTop: -6, color: '#64748b', fontSize: 13 }}>
        Users are provisioned automatically on first corporate sign-in. Admins manage everything;
        contributors see their own and workflows shared with them.
      </p>
      {error && <div className="error-text">{error}</div>}
      <table className="data">
        <thead>
          <tr>
            <th>Email</th>
            <th>Name</th>
            <th>Role</th>
            <th>Added</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {users.map((u) => (
            <tr key={u.id}>
              <td>{u.email}</td>
              <td>{u.displayName ?? '—'}</td>
              <td>
                <select
                  value={u.role}
                  disabled={busyId === u.id}
                  onChange={(e) => void changeRole(u, e.target.value as UserRole)}
                >
                  <option value="contributor">Contributor</option>
                  <option value="admin">Admin</option>
                </select>
              </td>
              <td>{new Date(u.createdAt).toLocaleDateString()}</td>
              <td>
                <button
                  type="button"
                  className="danger"
                  disabled={busyId === u.id}
                  onClick={() => void remove(u)}
                >
                  Remove
                </button>
              </td>
            </tr>
          ))}
          {users.length === 0 && (
            <tr>
              <td colSpan={5}>No users yet — sign in to provision the first admin.</td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  )
}
