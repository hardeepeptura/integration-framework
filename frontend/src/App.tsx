import { useEffect, useState } from 'react'
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom'
import { auth, type SessionState } from './api/client'
import BuilderPage from './pages/BuilderPage'
import ConnectionsPage from './pages/ConnectionsPage'
import DashboardPage from './pages/DashboardPage'
import RunsPage from './pages/RunsPage'
import UsersPage from './pages/UsersPage'
import WebhooksPage from './pages/WebhooksPage'
import WorkflowsPage from './pages/WorkflowsPage'

export default function App() {
  const [session, setSession] = useState<SessionState>()

  useEffect(() => {
    void auth
      .me()
      .then(setSession)
      .catch(() => setSession({ authenticated: false, ssoEnabled: false }))
  }, [])

  return (
    <BrowserRouter>
      <div className="app-shell">
        <nav className="app-nav">
          <span className="brand">Neuro-Eptura</span>
          <Link to="/dashboard">Dashboard</Link>
          <Link to="/workflows">Workflows</Link>
          <Link to="/connections">Connections</Link>
          <Link to="/runs">Runs</Link>
          <Link to="/webhooks">Webhooks</Link>
          {session?.isAdmin && <Link to="/users">Users</Link>}
          {session?.ssoEnabled &&
            (session.authenticated ? (
              <div className="nav-right">
                <span className="nav-user">{session.name ?? session.email}</span>
                <button type="button" onClick={() => void auth.signOut()}>
                  Sign out
                </button>
              </div>
            ) : (
              <div className="nav-right">
                <button
                  type="button"
                  className="primary"
                  onClick={() => auth.signIn(window.location.pathname)}
                >
                  Sign in with corporate account
                </button>
              </div>
            ))}
        </nav>
        <main className="app-main">
          <Routes>
            <Route path="/" element={<Navigate to="/dashboard" replace />} />
            <Route path="/dashboard" element={<DashboardPage />} />
            <Route path="/workflows" element={<WorkflowsPage />} />
            <Route path="/workflows/:id/builder" element={<BuilderPage />} />
            <Route path="/runs" element={<RunsPage />} />
            <Route path="/webhooks" element={<WebhooksPage />} />
            <Route path="/connections" element={<ConnectionsPage />} />
            {session?.isAdmin && <Route path="/users" element={<UsersPage />} />}
          </Routes>
        </main>
      </div>
    </BrowserRouter>
  )
}
