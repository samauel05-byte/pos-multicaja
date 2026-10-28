export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) { super(message) }
}
/** Error de red (servidor inalcanzable / sin respuesta). Distinto de un rechazo del servidor. */
export class NetworkError extends Error {
  constructor() { super('No hay conexión con el servidor.') }
}

const TOKEN_KEY = 'pos.token'
export const getToken = () => { try { return sessionStorage.getItem(TOKEN_KEY) } catch { return null } }
export const setToken = (t: string | null) => { try { t ? sessionStorage.setItem(TOKEN_KEY, t) : sessionStorage.removeItem(TOKEN_KEY) } catch { /* noop */ } }

let onUnauthorized: () => void = () => {}
export const setUnauthorizedHandler = (f: () => void) => { onUnauthorized = f }

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {}
  const token = getToken()
  if (token) headers['Authorization'] = `Bearer ${token}`
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  let res: Response
  try {
    res = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  } catch {
    throw new NetworkError()
  }
  const text = await res.text()
  let data: any = undefined
  if (text) { try { data = JSON.parse(text) } catch { /* html u otro */ } }
  if (!res.ok) {
    if (res.status === 401 && token && !url.includes('/auth/login')) onUnauthorized()
    if (res.status >= 502 && res.status <= 504) throw new NetworkError()
    throw new ApiError(res.status, data?.code ?? 'error', data?.message ?? `Error ${res.status}`)
  }
  return data as T
}

export const api = {
  delete: <T,>(u: string) => request<T>('DELETE', u),
  get: <T,>(u: string) => request<T>('GET', u),
  post: <T,>(u: string, b?: unknown) => request<T>('POST', u, b ?? {}),
  put: <T,>(u: string, b: unknown) => request<T>('PUT', u, b),
}

export const uuid = () => (crypto as any).randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16) })
