import type { GraphNodeDef, NodeType, WorkflowGraph } from '../api/types'

export const NODE_TYPES: NodeType[] = [
  'trigger',
  'http_request',
  'db_query',
  'transform',
  'condition',
  'loop',
  'delay',
]

export const TYPE_LABELS: Record<NodeType, string> = {
  trigger: 'Trigger',
  http_request: 'HTTP Request',
  db_query: 'DB Query',
  transform: 'Transform',
  condition: 'Condition',
  loop: 'Loop',
  delay: 'Delay',
}

export function defaultConfig(type: NodeType): Record<string, unknown> {
  switch (type) {
    case 'trigger':
      return { trigger: 'manual' }
    case 'http_request':
      return { url: '', method: 'GET', timeoutSeconds: 30 }
    case 'db_query':
      return { mode: 'query', timeoutSeconds: 30 }
    case 'transform':
      return { mapping: {} }
    case 'condition':
      return { operator: 'eq', on_true: [], on_false: [] }
    case 'loop':
      return { body: [] }
    case 'delay':
      return { seconds: 1 }
  }
}

/** Branch/body membership: which node ids are nested under condition/loop nodes. */
export function nestedIds(graph: WorkflowGraph): Set<string> {
  const nested = new Set<string>()
  for (const node of graph.nodes) {
    for (const field of ['on_true', 'on_false', 'body']) {
      const list = node.config[field]
      if (Array.isArray(list)) for (const id of list) nested.add(String(id))
    }
  }
  return nested
}

/** Sequence edges: consecutive main-line nodes + branch/body edges. */
export function graphEdges(graph: WorkflowGraph): { id: string; source: string; target: string; label?: string; style: Record<string, unknown> }[] {
  const edges: { id: string; source: string; target: string; label?: string; style: Record<string, unknown> }[] = []
  const nested = nestedIds(graph)
  const main = graph.nodes.filter((n) => !nested.has(n.id))
  for (let i = 0; i < main.length - 1; i++) {
    edges.push({
      id: `seq-${main[i].id}-${main[i + 1].id}`,
      source: main[i].id,
      target: main[i + 1].id,
      style: { stroke: '#94a3b8' },
    })
  }
  for (const node of graph.nodes) {
    if (node.type === 'condition') {
      for (const id of asStringArray(node.config['on_true'])) {
        edges.push({ id: `cond-t-${node.id}-${id}`, source: node.id, target: id, label: 'true', style: { stroke: '#16a34a' } })
      }
      for (const id of asStringArray(node.config['on_false'])) {
        edges.push({ id: `cond-f-${node.id}-${id}`, target: id, source: node.id, label: 'false', style: { stroke: '#dc2626' } })
      }
    }
    if (node.type === 'loop') {
      for (const id of asStringArray(node.config['body'])) {
        edges.push({ id: `loop-${node.id}-${id}`, source: node.id, target: id, label: 'body', style: { stroke: '#d97706', strokeDasharray: '4 3' } })
      }
    }
  }
  return edges
}

export function asStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.map(String) : []
}

export function makeNodeId(type: NodeType, existing: Set<string>): string {
  let i = 1
  while (existing.has(`${type}-${i}`)) i++
  return `${type}-${i}`
}

/** Node ids are used inside $.steps.<id> path references: keep them path-safe. */
export function isValidNodeId(id: string): boolean {
  return /^[A-Za-z0-9_-]+$/.test(id)
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

/** Rewrites "$.steps.<oldId>..." references (any tail) inside a config value tree. */
function rewriteRefs(value: unknown, oldId: string, newId: string): unknown {
  if (typeof value === 'string') {
    // Token boundary: not followed by another id character, so 'transform-1'
    // never matches inside 'transform-10'.
    return value.replace(new RegExp(`\\$\\.steps\\.${escapeRegExp(oldId)}(?![\\w-])`, 'g'), `$.steps.${newId}`)
  }
  if (Array.isArray(value)) {
    return value.map((item) => rewriteRefs(item, oldId, newId))
  }
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {}
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      out[key] = rewriteRefs(child, oldId, newId)
    }
    return out
  }
  return value
}

/** Replaces exact node-id list entries (condition branches, loop bodies). */
function rewriteIdLists(value: unknown, oldId: string, newId: string): unknown {
  if (Array.isArray(value)) return value.map((item) => (item === oldId ? newId : item))
  return value
}

/**
 * Renames a step across the whole graph: the node id, every $.steps.<oldId> reference
 * inside every node's config (mapping templates, URLs, operands, bodies...), the
 * condition on_true/on_false and loop body id lists, and the saved canvas positions.
 */
export function renameNodeInGraph(graph: WorkflowGraph, oldId: string, newId: string): WorkflowGraph {
  const nodes = graph.nodes.map((n) => {
    // Every node's config gets its references rewritten (branch/body id lists +
    // $.steps paths) — including the renamed node's own config.
    const config: Record<string, unknown> = {}
    for (const [key, value] of Object.entries(n.config)) {
      const rewritten = key === 'on_true' || key === 'on_false' || key === 'body'
        ? rewriteIdLists(value, oldId, newId)
        : value
      config[key] = rewriteRefs(rewritten, oldId, newId)
    }
    return { ...n, id: n.id === oldId ? newId : n.id, config }
  })
  const positions = graph.positions
    ? Object.fromEntries(
        Object.entries(graph.positions).map(([key, pos]) => [key === oldId ? newId : key, pos]),
      )
    : undefined
  return { nodes, positions }
}

/** Default vertical layout for a freshly loaded graph without saved positions. */
export function defaultPositions(nodes: GraphNodeDef[]): Record<string, { x: number; y: number }> {
  const positions: Record<string, { x: number; y: number }> = {}
  const nested = nestedIds({ nodes, positions: {} })
  const main = nodes.filter((n) => !nested.has(n.id))
  main.forEach((node, i) => {
    positions[node.id] = { x: 60, y: 40 + i * 110 }
  })
  // Place nested nodes beside their parent.
  for (const node of nodes) {
    if (nested.has(node.id)) {
      const fields = node.type === 'condition' ? ['on_true', 'on_false'] : ['body']
      let offset = 260
      for (const field of fields) {
        for (const id of asStringArray(node.config[field])) {
          const parent = positions[node.id] ?? { x: 60, y: 40 }
          positions[id] = { x: parent.x + offset, y: parent.y + 30 }
          offset += 220
        }
      }
      if (!positions[node.id]) positions[node.id] = { x: 60, y: 40 }
    }
  }
  return positions
}
