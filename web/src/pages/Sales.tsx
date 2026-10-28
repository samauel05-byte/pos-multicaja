import { useState } from 'react'
import { api, uuid } from '../api'
import { useSession } from '../session'
import { AuthModal, ErrorBox, Loading, Modal, errText, useLoad, useToast, type Auth } from '../ui'
import { money, dt, methodName, num } from '../format'
import type { Sale } from '../types'
import { printTicket } from '../print'

interface Row { id: string; number: string; createdAt: string; status: string; total: number; customer?: string; cashier: string; printCount: number }

export default function Sales() {
  const [num_, setNum] = useState('')
  const { data, error, loading, reload } = useLoad<Row[]>(() => api.get(`/api/sales?limit=100${num_ ? `&number=${encodeURIComponent(num_)}` : ''}`), [])
  const [sel, setSel] = useState<string | null>(null)
  return <div className="page">
    <h2>Ventas, reimpresión, devoluciones y anulaciones</h2>
    <div className="toolbar"><input placeholder="Buscar por número (ej. T-000012)" value={num_} onChange={e => setNum(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') reload() }} /><button onClick={reload}>Buscar</button></div>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    <table className="t hover"><thead><tr><th>Ticket</th><th>Fecha</th><th>Cajero</th><th>Cliente</th><th>Estado</th><th className="r">Total</th></tr></thead><tbody>
      {data?.map(s => <tr key={s.id} onClick={() => setSel(s.id)}><td>{s.number}</td><td>{dt(s.createdAt)}</td><td>{s.cashier}</td><td>{s.customer ?? '—'}</td>
        <td><span className={`chip ${s.status === 'Voided' ? 'bad' : ''}`}>{s.status === 'Voided' ? 'Anulada' : 'Completada'}</span></td><td className="r">{money(s.total)}</td></tr>)}
    </tbody></table>
    {!loading && !data?.length && <div className="empty">Sin ventas</div>}
    {sel && <SaleDetail id={sel} onClose={() => setSel(null)} onChanged={reload} />}
  </div>
}

function SaleDetail({ id, onClose, onChanged }: { id: string; onClose: () => void; onChanged: () => void }) {
  const { can, registerId, myShift, refreshBoot } = useSession(); const { push } = useToast()
  const { data: s, error, reload } = useLoad<Sale>(() => api.get(`/api/sales/${id}`), [id])
  const [mode, setMode] = useState<null | 'return' | 'void'>(null)
  const [qty, setQty] = useState<Record<string, string>>({}); const [reason, setReason] = useState(''); const [method, setMethod] = useState('Cash'); const [ref, setRef] = useState('')
  const [askAuth, setAskAuth] = useState(false); const [busy, setBusy] = useState(false)
  const opKey = useState(uuid())[0]
  const needAuth = mode === 'return' ? !can('returns.create') : !can('sales.void')

  const run = async (auth?: Auth) => {
    if (!s) return
    if (!registerId || !myShift()) { push('err', 'Abra un turno en esta caja para registrar devoluciones (el reembolso en efectivo sale de su gaveta).'); return }
    setBusy(true)
    try {
      if (mode === 'return') {
        const lines = s.lines.map(l => ({ saleLineId: l.id, quantity: parseFloat((qty[l.id] ?? '').replace(',', '.')) || 0 })).filter(l => l.quantity > 0)
        const r = await api.post<any>('/api/returns', { operationKey: opKey, saleId: s.id, registerId, reason, restock: true, lines, refundMethod: method, refundReference: ref || null, ...(auth ?? {}) })
        push('ok', `Devolución ${r.number} por ${money(r.total)} registrada.`)
      } else {
        const r = await api.post<any>(`/api/sales/${s.id}/void`, { operationKey: opKey, registerId, reason, ...(auth ?? {}) })
        push('ok', `Venta anulada (${r.number}). Reembolso total ${money(r.total)}.`)
      }
      setMode(null); setAskAuth(false); await reload(); await refreshBoot(); onChanged()
    } catch (e) { push('err', errText(e)); setAskAuth(false) } finally { setBusy(false) }
  }
  const submit = () => needAuth ? setAskAuth(true) : run()
  const reprint = async () => { const r = await printTicket(id, true); push(r.ok ? 'ok' : 'err', r.ok ? 'Copia enviada a impresión.' : `No se imprimió: ${r.error}`); reload() }
  const retTotal = s ? s.lines.reduce((a, l) => { const q = parseFloat((qty[l.id] ?? '').replace(',', '.')) || 0; return a + (q > 0 ? l.lineTotal * q / l.quantity : 0) }, 0) : 0

  return <Modal title={s ? `Venta ${s.number}` : 'Venta'} onClose={onClose} wide footer={s && !mode && <>
    <button onClick={reprint}>🖨 Reimprimir (copia)</button>
    {s.status === 'Completed' && <><button onClick={() => { setMode('return'); setQty({}) }}>↩ Devolución parcial</button><button className="danger" onClick={() => setMode('void')}>⛔ Anular venta</button></>}</>}>
    {error ? <ErrorBox error={error} /> : null}
    {!s ? <Loading /> : <>
      <div className="meta"><span>{dt(s.createdAt)}</span><span>{s.registerName}</span><span>{s.cashierName}</span><span>{s.customerName ?? 'Sin cliente'}</span>
        <span className={`chip ${s.status === 'Voided' ? 'bad' : ''}`}>{s.status === 'Voided' ? 'ANULADA' : 'Completada'}</span><span className="muted">impresiones: {s.printCount}</span></div>
      <table className="t"><thead><tr><th>Producto</th><th className="r">Cant.</th><th className="r">Precio</th><th className="r">Desc.</th><th className="r">Total</th><th className="r">Devuelto</th>{mode === 'return' && <th>Devolver</th>}</tr></thead><tbody>
        {s.lines.map(l => <tr key={l.id}><td>{l.productName}</td><td className="r">{num(l.quantity, 4)}</td><td className="r">{money(l.unitPrice)}</td><td className="r">{l.discountPercent ? `${l.discountPercent}%` : ''}</td><td className="r">{money(l.lineTotal)}</td><td className="r">{l.returnedQuantity ? num(l.returnedQuantity, 4) : ''}</td>
          {mode === 'return' && <td><input style={{ width: 80 }} value={qty[l.id] ?? ''} placeholder={`máx ${num(l.quantity - l.returnedQuantity, 4)}`} onChange={e => setQty({ ...qty, [l.id]: e.target.value })} /></td>}</tr>)}
      </tbody></table>
      <div className="two"><div className="muted">{s.payments.map(p => <div key={p.method + p.amount}>{methodName(p.method)}: {money(p.amount)}{p.reference ? ` (${p.reference})` : ''}</div>)}{s.changeGiven > 0 && <div>Cambio: {money(s.changeGiven)}</div>}{s.creditBalance > 0 && <div>Saldo a crédito pendiente: {money(s.creditBalance)}</div>}{s.voidReason && <div className="bad-t">Motivo de anulación: {s.voidReason}</div>}</div>
        <div className="r"><div>Subtotal {money(s.subtotal)}</div><div>Impuestos {money(s.taxTotal)}</div><b>Total {money(s.total)}</b></div></div>
      {mode && <div className="card">
        <h4>{mode === 'return' ? 'Devolución' : 'Anulación total'}</h4>
        <label>Motivo (obligatorio)<input value={reason} onChange={e => setReason(e.target.value)} autoFocus /></label>
        {mode === 'return' && <div className="payrow"><label>Reembolso por<select value={method} onChange={e => setMethod(e.target.value)}><option value="Cash">Efectivo (sale de la gaveta)</option><option value="Card">Tarjeta (registro manual)</option><option value="Transfer">Transferencia (registro manual)</option></select></label>
          {method !== 'Cash' && <label>Referencia<input value={ref} onChange={e => setRef(e.target.value)} /></label>}</div>}
        {mode === 'return' && <p>Total a devolver: <b>{money(retTotal)}</b>. Si la venta tiene saldo a crédito pendiente, primero se reduce ese saldo.</p>}
        {mode === 'void' && <p>Se devolverá todo el inventario y el dinero por los mismos medios del cobro. La venta se conserva marcada como anulada.</p>}
        {needAuth && <small className="muted">Requiere autorización de supervisor (PIN).</small>}
        <div className="actions"><button onClick={() => setMode(null)}>Cancelar</button><button className="primary" disabled={busy || !reason.trim() || (mode === 'return' && retTotal <= 0)} onClick={submit}>{busy ? 'Procesando…' : 'Confirmar'}</button></div>
      </div>}
    </>}
    {askAuth && <AuthModal reason={mode === 'return' ? 'Las devoluciones las autoriza un supervisor.' : 'Las anulaciones las autoriza un supervisor.'} onClose={() => setAskAuth(false)} onOk={a => run(a)} />}
  </Modal>
}
