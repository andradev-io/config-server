/** Alert presentation for the API error kinds (`ConfigApiError` / `FilesApiError`). */

export const alertType = (kind?: string): 'warning' | 'error' =>
  kind === 'not-found' || kind === 'forbidden' || kind === 'read-only' ? 'warning' : 'error'

export function alertTitle(kind?: string): string {
  switch (kind) {
    case 'not-found':
      return 'Not found (404)'
    case 'bad-request':
      return 'Bad request (400)'
    case 'unauthorized':
      return 'Sign in required (401)'
    case 'forbidden':
      return 'Forbidden (403)'
    case 'read-only':
      return 'Read-only source (501)'
    case 'network':
      return 'Network error'
    case 'invalid-response':
      return 'Invalid response'
    default:
      return 'Server error'
  }
}
