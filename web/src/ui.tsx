import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { ApiError, NetworkError } from './api'

// ---------- Toasts (estados visibles de éxito / error) ----------
type Toast = { id: number; kind: 'ok' | 'err' | 'info'; text: string }
const ToastCtx = createContext<{ push: (kind: Toast['kind'], text: string) => void }>({ push: () => {} })
export const useToast = () => useContext(ToastCtx)
export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<Toast[]>([])
  const push = useCallback((kind: Toast['kind'], text: string) => {
    const id = Date.now() + Math.random()
    setItems(x => [...x, { id, kind, text }])
    setTimeout(() => setItems(x => x.filter(t => t.id !== id)), kind === 'err' ? 7000 : 3500)
  }, [])
  return <ToastCtx.Provider value={{ push }}>{children}
    <div className="toasts" role="status" aria-live="polite">{items.map(t => <div key={t.id} className={`toast ${t.kind}`}>{t.text}</div>)}</div>
  </ToastCtx.Provider>
}
export const errText = (e: unknown) => e instanceof ApiError ? e.message : e instanceof NetworkError ? e.message : (e as Error)?.message ?? 'Error inesperado'

// ---------- Modal ----------
export function Modal({ title, onClose, children, wide, footer }: { title: string; onClose?: () => void; children: ReactNode; wide?: boolean; footer?: ReactNode }) {
  useEffect(() => {
    if (!onClose) return
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); onClose() } }
    window.addEventListener('keydown', h, true); return () => window.removeEventListener('keydown', h, true)
  }, [onClose])
  return <div className="overlay" onMouseDown={e => { if (e.target === e.currentTarget) onClose?.() }}>
    <div className={`modal ${wide ? 'wide' : ''}`} role="dialog" aria-modal="true" aria-label={title}>
      <div className="modal-head"><h3>{title}</h3>{onClose && <button className="icon" onClick={onClose} aria-label="Cerrar">✕</button>}</div>
      <div className="modal-body">{children}</div>
      {footer && <div className="modal-foot">{footer}</div>}
    </div>
  </div>
}

export function Loading({ text = 'Cargando…' }: { text?: string }) { return <div className="loading"><span className="spinner" />{text}</div> }
export function ErrorBox({ error, retry }: { error: unknown; retry?: () => void }) {
  return <div className="errbox">⚠ {errText(error)} {retry && <button className="link" onClick={retry}>Reintentar</button>}</div>
}
export function Pending({ children }: { children: ReactNode }) { return <div className="pending">⏳ PENDIENTE — {children}</div> }

/** Carga de datos con estados visibles de carga y error. */
export function useLoad<T>(fn: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [loading, setLoading] = useState(true)
  const fnRef = useRef(fn); fnRef.current = fn
  const reload = useCallback(async () => {
    setLoading(true); setError(null)
    try { setData(await fnRef.current()) } catch (e) { setError(e) } finally { setLoading(false) }
  }, [])
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { reload() }, deps)
  return { data, error, loading, reload, setData }
}

// ---------- Autorización de supervisor (usuario + PIN, verificado en el servidor) ----------
export interface Auth { authorizerUsername: string; authorizerPin: string }
export function AuthModal({ reason, onOk, onClose }: { reason: string; onOk: (a: Auth) => void; onClose: () => void }) {
  const [u, setU] = useState('supervisor'); const [p, setP] = useState('')
  return <Modal title="Autorización de supervisor" onClose={onClose}
    footer={<><button onClick={onClose}>Cancelar</button><button className="primary" disabled={!u || p.length < 4} onClick={() => onOk({ authorizerUsername: u, authorizerPin: p })}>Autorizar</button></>}>
    <p>{reason}</p>
    <label>Usuario supervisor<input value={u} onChange={e => setU(e.target.value)} autoComplete="off" /></label>
    <label>PIN<input type="password" inputMode="numeric" value={p} onChange={e => setP(e.target.value)} autoFocus autoComplete="off"
      onKeyDown={e => { if (e.key === 'Enter' && u && p.length >= 4) onOk({ authorizerUsername: u, authorizerPin: p }) }} /></label>
    <small className="muted">El PIN se valida en el servidor y la autorización queda en la auditoría.</small>
  </Modal>
}
