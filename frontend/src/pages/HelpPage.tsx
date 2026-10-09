import { useMemo } from 'react'
import { marked } from 'marked'
// Single source of truth: docs/How-To-Build-Workflows.md in the repository root.
// The prebuild/predev npm scripts copy it to src/generated-help.md (Vite cannot
// import files outside the frontend root), so the repo doc is never duplicated.
import helpMarkdown from '../generated-help.md?raw'

export default function HelpPage() {
  const html = useMemo(() => marked.parse(helpMarkdown, { async: false }) as string, [])
  return (
    <div className="page">
      {/* The content is this repository's own documentation file (not user input). */}
      <div className="md-body" dangerouslySetInnerHTML={{ __html: html }} />
    </div>
  )
}
