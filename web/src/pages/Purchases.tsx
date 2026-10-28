import { useState } from 'react'
import { api, uuid } from '../api'
import { useSession } from '../session'
import { ErrorBox, Loading, Modal, errText, useLoad, useToast } from '../ui'
import { money, d, num } from '../format'
import type { Product } from '../types'

interface Supplier { id: string; name: string; taxId?: string; phone?: string; creditDays: number; balance: number; active: boolean }
interface Purchase { id: string; number: string; receivedAt: string; supplier: string; supplierInvoice?: string; total: number; paid: number; balance: number; dueDate: string; overdue: boolean }
interface PLine { product: Product; qty: number; cost: number }

export default function Purchases() {
  const { boot } = useSession(); const { push } = useToast()
  const sup = useLoad<Supplier[]>(() => api.get('/api/suppliers'), [])
  const pur = useLoad<Purchase[]>(() => api.get('/api/purchases'), [])
  const [tab, setTab] = useState<'pur' | 'sup'>('pur')
  const [editSup, setEditSup] = useState<Partial<Supplier> | null>(null)
  const [recv, setRecv] = useState(false)
  const [payFor, setPayFor] = useState<Supplier | null>(null); const [amt, setAmt] = useState(''); const [meth, setMeth] = useState('Transfer'); const [ref, setRef] = useState(''); const [payKey, setPayKey] = useState(uuid())
  const [supplierId, setSupplierId] = useState(''); const [inv, setInv] = useState(''); const [lines, setLines] = useState<PLine[]>([]); const [q, setQ] = useState(''); const [found, setFound] = useState<Product[]>([]); const [recvKey, setRecvKey] = useState(uuid())

  const search = async (t: string) => { setQ(t); if (t.length >= 2) setFound(await api.get<Product[]>(`/api/products?limit=8&q=${encodeURIComponent(t)}`)); else setFound([]) }
  const sub = lines.reduce((a, l) => a + l.qty * l.cost, 0)
  const tax = lines.reduce((a, l) => a + Math.round(l.qty * l.cost * l.product.taxRate * 100) / 100, 0)
  const receive = async () => {
    try {
      const r = await api.post<any>('/api/purchases', { operationKey: recvKey, supplierId, locationId: boot!.locations[0].id, supplierInvoice: inv || null, lines: lines.map(l => ({ productId: l.product.id, quantity: l.qty, unitCost: l.cost })) })
      push('ok', `Compra ${r.number} recibida: inventario actualizado y cuenta por pagar de ${money(r.total)}.`)
      setRecv(false); setLines([]); setInv(''); setRecvKey(uuid()); sup.reload(); pur.reload()
    } catch (e) { push('err', errText(e)) }
  }
  return <div className="page">
    <h2>Compras y proveedores</h2>
    <div className="tabs"><button className={tab === 'pur' ? 'active' : ''} onClick={() => setTab('pur')}>Compras</button><button className={tab === 'sup' ? 'active' : ''} onClick={() => setTab('sup')}>Proveedores y cuentas por pagar</button></div>
    {(sup.error || pur.error) ? <ErrorBox error={sup.error || pur.error} /> : null}
    {tab === 'pur' && <>
      <div className="toolbar"><button className="primary" onClick={() => { setRecv(true); setSupplierId(sup.data?.[0]?.id ?? '') }}>＋ Recibir mercancía</button></div>
      {pur.loading ? <Loading /> : <table className="t"><thead><tr><th>Compra</th><th>Fecha</th><th>Proveedor</th><th>Factura</th><th className="r">Total</th><th className="r">Pagado</th><th className="r">Saldo</th><th>Vence</th></tr></thead><tbody>
        {pur.data?.map(p => <tr key={p.id}><td>{p.number}</td><td>{d(p.receivedAt)}</td><td>{p.supplier}</td><td>{p.supplierInvoice}</td><td className="r">{money(p.total)}</td><td className="r">{money(p.paid)}</td><td className="r">{money(p.balance)}</td><td className={p.overdue ? 'bad-t' : ''}>{d(p.dueDate)}{p.overdue && ' · VENCIDA'}</td></tr>)}</tbody></table>}
    </>}
    {tab === 'sup' && <>
      <div className="toolbar"><button className="primary" onClick={() => setEditSup({ active: true, creditDays: 30 })}>＋ Nuevo proveedor</button></div>
      {sup.loading ? <Loading /> : <table className="t"><thead><tr><th>Proveedor</th><th>Teléfono</th><th>Días</th><th className="r">Deuda</th><th></th></tr></thead><tbody>
        {sup.data?.map(s => <tr key={s.id}><td>{s.name}</td><td>{s.phone}</td><td>{s.creditDays}</td><td className={`r ${s.balance > 0 ? 'bad-t' : ''}`}>{money(s.balance)}</td>
          <td className="r"><button onClick={() => setEditSup(s)}>Editar</button> <button disabled={!s.balance} onClick={() => { setPayFor(s); setAmt(String(s.balance)); setPayKey(uuid()) }}>Pagar</button></td></tr>)}</tbody></table>}
    </>}
    {editSup && <Modal title="Proveedor" onClose={() => setEditSup(null)} footer={<><button onClick={() => setEditSup(null)}>Cancelar</button><button className="primary" disabled={!editSup.name} onClick={async () => {
      try { const b = { name: editSup.name, taxId: editSup.taxId, phone: editSup.phone, creditDays: editSup.creditDays ?? 30, active: editSup.active ?? true }
        if (editSup.id) await api.put(`/api/suppliers/${editSup.id}`, b); else await api.post('/api/suppliers', b); setEditSup(null); sup.reload() } catch (e) { push('err', errText(e)) } }}>Guardar</button></>}>
      <label>Nombre<input value={editSup.name ?? ''} onChange={e => setEditSup({ ...editSup, name: e.target.value })} autoFocus /></label>
      <label>RNC<input value={editSup.taxId ?? ''} onChange={e => setEditSup({ ...editSup, taxId: e.target.value })} /></label>
      <label>Teléfono<input value={editSup.phone ?? ''} onChange={e => setEditSup({ ...editSup, phone: e.target.value })} /></label>
      <label>Días de crédito<input value={editSup.creditDays ?? 30} onChange={e => setEditSup({ ...editSup, creditDays: parseInt(e.target.value) || 0 })} /></label>
    </Modal>}
    {payFor && <Modal title={`Pago a ${payFor.name}`} onClose={() => setPayFor(null)} footer={<><button onClick={() => setPayFor(null)}>Cancelar</button><button className="primary" disabled={!(parseFloat(amt) > 0)} onClick={async () => {
      try { const r = await api.post<any>('/api/suppliers/payments', { operationKey: payKey, supplierId: payFor.id, method: meth, amount: parseFloat(amt.replace(',', '.')), reference: ref || null }); push('ok', `Pago registrado. Deuda restante ${money(r.balance)}.`); setPayFor(null); sup.reload(); pur.reload() } catch (e) { push('err', errText(e)) } }}>Registrar pago</button></>}>
      <p>Deuda: <b>{money(payFor.balance)}</b></p>
      <label>Monto<input value={amt} onChange={e => setAmt(e.target.value)} inputMode="decimal" autoFocus /></label>
      <label>Medio<select value={meth} onChange={e => setMeth(e.target.value)}><option value="Transfer">Transferencia</option><option value="Cash">Efectivo</option><option value="Card">Tarjeta</option></select></label>
      <label>Referencia<input value={ref} onChange={e => setRef(e.target.value)} /></label>
      <small className="muted">Este pago NO sale de la gaveta de ninguna caja; se registra como pago a proveedor.</small>
    </Modal>}
    {recv && <Modal title="Recepción de mercancía" wide onClose={() => setRecv(false)} footer={<><button onClick={() => setRecv(false)}>Cancelar</button><button className="primary" disabled={!supplierId || !lines.length} onClick={receive}>Recibir y actualizar inventario</button></>}>
      <div className="form2"><label>Proveedor<select value={supplierId} onChange={e => setSupplierId(e.target.value)}>{sup.data?.filter(s => s.active).map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
        <label>No. de factura del proveedor<input value={inv} onChange={e => setInv(e.target.value)} /></label></div>
      <label>Agregar producto<input value={q} onChange={e => search(e.target.value)} placeholder="Buscar por nombre / código" /></label>
      {found.length > 0 && <div className="list">{found.map(p => <button key={p.id} className="row" onClick={() => { if (!lines.find(l => l.product.id === p.id)) setLines([...lines, { product: p, qty: 1, cost: p.cost ?? 0 }]); setFound([]); setQ('') }}><span>{p.name}</span><small>{p.sku} · costo {money(p.cost)}</small></button>)}</div>}
      <table className="t"><thead><tr><th>Producto</th><th>Cantidad</th><th>Costo unit. (sin imp.)</th><th className="r">Importe</th><th></th></tr></thead><tbody>
        {lines.map((l, i) => <tr key={l.product.id}><td>{l.product.name}</td>
          <td><input style={{ width: 90 }} value={l.qty} inputMode="decimal" onChange={e => setLines(lines.map((x, k) => k === i ? { ...x, qty: parseFloat(e.target.value) || 0 } : x))} /></td>
          <td><input style={{ width: 110 }} value={l.cost} inputMode="decimal" onChange={e => setLines(lines.map((x, k) => k === i ? { ...x, cost: parseFloat(e.target.value) || 0 } : x))} /></td>
          <td className="r">{money(l.qty * l.cost)}</td><td><button className="icon" onClick={() => setLines(lines.filter((_, k) => k !== i))}>✕</button></td></tr>)}</tbody></table>
      <div className="r">Subtotal {money(sub)} · Impuestos {money(tax)} · <b>Total {money(sub + tax)}</b> <small className="muted">({num(lines.reduce((a, l) => a + l.qty, 0))} unidades)</small></div>
    </Modal>}
  </div>
}
