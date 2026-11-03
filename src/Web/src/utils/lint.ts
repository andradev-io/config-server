import { parseDocument } from 'yaml'

export type EditorLanguage = 'json' | 'yaml' | 'env'

/** 1-based line and column. */
export interface LintIssue {
  line: number
  column: number
  message: string
}

export function offsetToLineColumn(text: string, offset: number): { line: number; column: number } {
  const clamped = Math.max(0, Math.min(offset, text.length))
  let line = 1
  let lastBreak = -1
  for (let i = 0; i < clamped; i++) {
    if (text[i] === '\n') {
      line++
      lastBreak = i
    }
  }
  return { line, column: clamped - lastBreak }
}

export function lintJson(text: string): LintIssue[] {
  if (!text.trim()) return []
  try {
    JSON.parse(text)
    return []
  } catch (e) {
    const message = e instanceof Error ? e.message : 'Invalid JSON'
    const lc = /line (\d+) column (\d+)/i.exec(message)
    if (lc) return [{ line: Number(lc[1]), column: Number(lc[2]), message }]
    const pos = /position (\d+)/i.exec(message)
    if (pos) return [{ ...offsetToLineColumn(text, Number(pos[1])), message }]
    return [{ ...offsetToLineColumn(text, text.length), message }]
  }
}

export function lintYaml(text: string): LintIssue[] {
  if (!text.trim()) return []
  const doc = parseDocument(text)
  return doc.errors.map((err) => ({
    line: err.linePos?.[0]?.line ?? 1,
    column: err.linePos?.[0]?.col ?? 1,
    message: err.message.split('\n')[0] ?? err.message,
  }))
}

export function lintEnv(text: string): LintIssue[] {
  const issues: LintIssue[] = []
  text.split(/\r?\n/).forEach((raw, i) => {
    const line = raw.trim()
    if (!line || line.startsWith('#')) return
    const eq = line.indexOf('=')
    if (eq === -1) issues.push({ line: i + 1, column: 1, message: 'Expected KEY=value (missing "=")' })
    else if (eq === 0) issues.push({ line: i + 1, column: 1, message: 'Missing key before "="' })
  })
  return issues
}

export function lintText(language: EditorLanguage, text: string): LintIssue[] {
  if (language === 'json') return lintJson(text)
  if (language === 'yaml') return lintYaml(text)
  return lintEnv(text)
}
