import { Background, ReactFlow, type Edge, type Node, type NodeChange } from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { Run, ValidationResult, Workflow, WorkflowGraph } from '../api/types'
import { defaultPositions, graphEdges, makeNodeId, NODE_TYPES, TYPE_LABELS } from '../builder/nodeDefs'
import NodeConfigPanel from '../components/NodeConfigPanel'

function toFlowNodes(graph: WorkflowGraph, run?: Run | null): Node[] {
  const positions = graph.positions ?? defaultPositions(graph.nodes)
  const lastStatus = new Map<string, string>()
  if (run) for (const step of run.steps) lastStatus.set(step.nodeId, step.status)
  return graph.nodes.map((def) => {
    const status = lastStatus.get(def.id)
    return {
      id: def.id,
      position: positions[def.id] ?? { x: 60, y: 40 },
      className: `flow-node type-${def.type}${status ? ` status-${status}` : ''}`,
      data: { label: def.id },
    }
  })
}

export default function BuilderPage() {
  const { id } = useParams<{ id: string }>()
  const [workflow, setWorkflow] = useState<Workflow>()
  const [graph, setGraph] = useState<WorkflowGraph>({ nodes: [] })
  const [rfNodes, setRfNodes] = useState<Node[]>([])
  const [selectedId, setSelectedId] = useState<string>()
  const [error, setError] = useState<string>()
  const [status, setStatus] = useState<string>()
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!id) return
    void api
      .getWorkflow(id)
      .then((wf) => {
        setWorkflow(wf)
        const g = wf.graph ?? { nodes: [] }
        setGraph(g)
        setRfNodes(toFlowNodes(g))
      })
      .catch((e) => setError(String(e)))
  }, [id])

  const onNodesChange = useCallback((changes: NodeChange<Node>[]) => {
    setRfNodes((current) =>
      current.map((n) => {
        const change = changes.find((c) => c.type === 'position' && 'id' in c && c.id === n.id)
        return change && change.type === 'position' && change.position
          ? { ...n, position: change.position }
          : n
      }),
    )
  }, [])

  const selected = graph.nodes.find((n) => n.id === selectedId)

  const updateConfig = (nodeId: string, config: Record<string, unknown>) => {
    setGraph((g) => ({
      ...g,
      nodes: g.nodes.map((n) => (n.id === nodeId ? { ...n, config } : n)),
    }))
  }

  const addNode = (type: (typeof NODE_TYPES)[number]) => {
    const nodeId = makeNodeId(type, new Set(graph.nodes.map((n) => n.id)))
    setGraph((g) => ({ ...g, nodes: [...g.nodes, { id: nodeId, type, config: {} }] }))
    setRfNodes((current) => [
      ...current,
      {
        id: nodeId,
        position: { x: 60 + current.length * 30, y: 40 + current.length * 110 },
        className: `flow-node type-${type}`,
        data: { label: nodeId },
      },
    ])
    setSelectedId(nodeId)
  }

  const removeNode = (nodeId: string) => {
    setGraph((g) => ({
      ...g,
      nodes: g.nodes
        .filter((n) => n.id !== nodeId)
        .map((n) => {
          const config = { ...n.config }
          for (const field of ['on_true', 'on_false', 'body']) {
            const list = config[field]
            if (Array.isArray(list)) config[field] = list.filter((x) => String(x) !== nodeId)
          }
          return { ...n, config }
        }),
    }))
    setRfNodes((current) => current.filter((n) => n.id !== nodeId))
    setSelectedId(undefined)
  }

  const save = async () => {
    if (!id || !workflow) return
    setBusy(true)
    setError(undefined)
    try {
      const positions: Record<string, { x: number; y: number }> = {}
      for (const n of rfNodes) positions[n.id] = n.position
      const updated = await api.updateWorkflow(id, { graph: { ...graph, positions } })
      setWorkflow(updated)
      setStatus(`Saved at ${new Date().toLocaleTimeString()}`)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const validate = async () => {
    if (!id) return
    setBusy(true)
    setError(undefined)
    try {
      const result: ValidationResult = await api.validateWorkflow(id, graph)
      setStatus(result.valid ? 'Graph is valid.' : `Validation errors:\n${result.errors.join('\n')}`)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const testRun = async () => {
    if (!id) return
    setBusy(true)
    setError(undefined)
    try {
      await save()
      const run = await api.runWorkflow(id, {})
      const byId = new Map(run.steps.map((s) => [s.nodeId, s.status]))
      setRfNodes((current) =>
        current.map((n) => ({
          ...n,
          className: `${(n.className ?? '').split(' status-')[0]} status-${byId.get(n.id) ?? ''}`.trimEnd(),
        })),
      )
      setStatus(`Run ${run.status}${run.error ? ` — ${run.error}` : ''}`)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const edges: Edge[] = useMemo(
    () =>
      graphEdges(graph).map((e) => ({
        id: e.id,
        source: e.source,
        target: e.target,
        label: e.label,
        style: e.style,
        animated: false,
      })),
    [graph],
  )

  if (!workflow) {
    return (
      <div className="page">
        {error ?? 'Loading…'}
        <div>
          <Link to="/workflows">← Back to workflows</Link>
        </div>
      </div>
    )
  }

  return (
    <div className="builder">
      <aside className="palette">
        <h3>Add node</h3>
        {NODE_TYPES.filter((t) => t !== 'trigger' || !graph.nodes.some((n) => n.type === 'trigger')).map((type) => (
          <button key={type} type="button" onClick={() => addNode(type)}>
            + {TYPE_LABELS[type]}
          </button>
        ))}
      </aside>

      <div className="builder-canvas">
        <ReactFlow
          nodes={rfNodes}
          edges={edges}
          onNodesChange={onNodesChange}
          onNodeClick={(_, node) => setSelectedId(node.id)}
          fitView
        >
          <Background />
        </ReactFlow>
        <div className="builder-toolbar">
          <button type="button" className="primary" disabled={busy} onClick={() => void save()}>
            Save
          </button>
          <button type="button" disabled={busy} onClick={() => void validate()}>
            Validate
          </button>
          <button type="button" className="primary" disabled={busy} onClick={() => void testRun()}>
            Test run
          </button>
        </div>
        <div className="builder-status">
          {error && <div className="error-text">{error}</div>}
          {status && <div className="ok-text">{status}</div>}
        </div>
      </div>

      <aside className="config-panel">
        {selected ? (
          <NodeConfigPanel
            key={selected.id}
            node={selected}
            graph={graph}
            onChange={(config) => updateConfig(selected.id, config)}
            onDelete={() => removeNode(selected.id)}
          />
        ) : (
          <div>
            <h3>{workflow.name}</h3>
            <p style={{ fontSize: 13, color: '#64748b' }}>
              Select a node to configure it. Nodes execute top-to-bottom in sequence; condition nodes
              route to their true/false branches; loop nodes iterate their body over an array.
            </p>
          </div>
        )}
      </aside>
    </div>
  )
}
