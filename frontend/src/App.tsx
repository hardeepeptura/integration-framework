import { useEffect, useState } from 'react'
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom'
import { auth, type SessionState } from './api/client'
import BuilderPage from './pages/BuilderPage'
import ConnectionsPage from './pages/ConnectionsPage'
import RunsPage from './pages/RunsPage'
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
          <Link to="/workflows">Workflows</Link>
          <Link to="/connections">Connections</Link>
          <Link to="/runs">Runs</Link>
          <Link to="/webhooks">Webhooks</Link>
          {session?.ssoEnabled &&
            (session.authenticated ? (
              <span className="nav-user">
                {session.name ?? session.email}
                <button type="button" onClick={() => void auth.signOut()}>
                  Sign out
                </button>
              </span>
            ) : (
              <button
                type="button"
                className="primary"
                onClick={() => auth.signIn(window.location.pathname)}
              >
                Sign in with corporate account
              </button>
            ))}
        </nav>
        <main className="app-main">
          <Routes>
            <Route path="/" element={<Navigate to="/workflows" replace />} />
            <Route path="/workflows" element={<WorkflowsPage />} />
            <Route path="/workflows/:id/builder" element={<BuilderPage />} />
            <Route path="/runs" element={<RunsPage />} />
            <Route path="/webhooks" element={<WebhooksPage />} />
            <Route path="/connections" element={<ConnectionsPage />} />
          </Routes>
        </main>
      </div>
    </BrowserRouter>
  )
}
