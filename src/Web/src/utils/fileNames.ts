export type FileFormat = 'json' | 'yaml' | 'env'

export const EXTENSIONS = ['.json', '.yaml', '.yml', '.env'] as const

export const NAME_HINT = 'Name: "default" or [A-Za-z0-9_-]+. Extensions: .json .yaml .yml .env'

/** Files owned by the server (environments / variables declarations); hidden from the file list. */
export const RESERVED_FILES = ['_environments.json', '_variables.json']
const RESERVED_BASES = ['_environments', '_variables']
export const isReservedFile = (fileName: string) => RESERVED_FILES.includes(fileName.toLowerCase())

const BASE_NAME = /^(default|[A-Za-z0-9_-]+)$/

export function formatForExtension(ext: string): FileFormat | null {
  switch (ext.toLowerCase()) {
    case '.json':
      return 'json'
    case '.yaml':
    case '.yml':
      return 'yaml'
    case '.env':
      return 'env'
    default:
      return null
  }
}

export function splitFileName(fileName: string): { base: string; ext: string } {
  const i = fileName.lastIndexOf('.')
  return i <= 0 ? { base: fileName, ext: '' } : { base: fileName.slice(0, i), ext: fileName.slice(i) }
}

export function formatForFileName(fileName: string): FileFormat | null {
  return formatForExtension(splitFileName(fileName).ext)
}

/** Error message, or null when the base name is valid. */
export function validateBaseName(base: string): string | null {
  if (!base.trim()) return 'Required'
  if (RESERVED_BASES.includes(base.toLowerCase())) return 'Reserved name'
  if (!BASE_NAME.test(base)) return 'Use letters, digits, "_" or "-" only'
  return null
}

export function validateFileName(fileName: string): string | null {
  const { base, ext } = splitFileName(fileName)
  if (!ext || formatForExtension(ext) === null) return 'Extension must be .json, .yaml, .yml or .env'
  return validateBaseName(base)
}

/** Environment a file overrides, or null for the default file. */
export function environmentForFile(fileName: string): string | null {
  const { base } = splitFileName(fileName)
  return base === 'default' ? null : base
}

export function isDefaultFile(fileName: string): boolean {
  return splitFileName(fileName).base === 'default'
}

export function emptyTemplate(format: FileFormat): string {
  if (format === 'json') return '{\n  \n}\n'
  if (format === 'yaml') return '# Overrides: only changed keys\n'
  return '# KEY=value\n'
}

export type TemplateKind = 'empty' | 'copy-default'

export function buildTemplate(kind: TemplateKind, format: FileFormat, defaultContent?: string): string {
  return kind === 'copy-default' && defaultContent !== undefined ? defaultContent : emptyTemplate(format)
}

export function formatIcon(format: FileFormat): string {
  if (format === 'json') return 'mdi-code-json'
  if (format === 'yaml') return 'mdi-file-code-outline'
  return 'mdi-key-variant'
}
