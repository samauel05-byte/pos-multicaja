import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api'
import { ErrorBox, Loading, Modal, errText, useLoad, useToast } from '../ui'
import { money, d } from '../format'
import type { Customer, Product } from '../types'

interface Row { id: string; number: string; createdAt: string; validUntil: string; customer?: string; total: number; status: string }
interface QLine { product: Product; qty: number; disc: number }
const ST: Record<string, string> = { Open: 'Abierta', Converted: 'Convertida', Expired: 'Vencida', Cancelled: 'Cancelada' }

export default function Quotes() {
  const { push } = useToast(); const nav = useNavigate()
  const { data, error, loading, reload } = useLoad<Row[]>(() => api.get('/api/quotes'), [])
  const [create, setCreate] = useState(false)
  const [lines, setLines] = useState<QLine[]>([]); const [q, setQ] = useState(''); const [found, setFound] = useState<Product[]>([])
  const [custQ, setCustQ] = useState(''); const [custs, setCusts] = useState<Customer[]>([]); const [cust, setCust] = useState<Customer | null>(null)
  const [days, setDays] = useState('7'); const [notes, setNotes] = useState('')

  const search = async (t: string) => { setQ(t); setFound(t.length >= 2 ? await api.get<Product[]>(`/api/products?active=true&limit=8&q=${encodeURIComponent(t)}`) : []) }
  const toPos = async (id: string) => {
    try {
      const qt = await api.get<any>(`/api/quotes/${id}`)
      if (qt.status !== 'Open') { push('err', `La cotización está ${ST[qt.status]?.toLowerCase()}.`); return }
      sessionStorage.setItem('pos.loadQuote', JSON.stringify({ id: qt.id, customerId: qt.customerId, lines: qt.lines.map((l: any) => ({ productId: l.productId, quantity: l.quantity, discountPercent: l.discountPercent })) }))
      nav('/')
    } catch (e) { push('err', errText(e)) }
  }
  return <div className="page">
    <h2>Cotizaciones</h2>
    <div className="toolbar"><button className="primary" onClick={() => { setCreate(true); setLines([]); setCust(null) }}>＋ Nueva cotización</button></div>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    <table className="t"><thead><tr><th>Número</th><th>Fecha</th><th>Cliente</th><th>Válida hasta</th><th>Estado</th><th className="r">Total</th><th></th></tr></thead><tbody>
      {data?.map(r => <tr key={r.id}><td>{r.number}</td><td>{d(r.createdAt)}</td><td>{r.customer ?? '—'}</td><td>{d(r.validUntil)}</td><td><span className="chip">{ST[r.status] ?? r.status}</span></td><td className="r">{money(r.total)}</td>
        <td className="r">{r.status === 'Open' && <><button className="primary" onClick={() => toPos(r.id)}>Convertir en venta</button> <button onClick={async () => { try { await api.post(`/api/quotes/${r.id}/cancel`); reload() } catch (e) { push('err', errText(e)) } }}>Cancelar</button></>}</td></tr>)}
    </tbody></table>
    {create && <Modal title="Nueva cotización" wide onClose={() => setCreate(false)} footer={<><button onClick={() => setCreate(false)}>Cancelar</button><button className="primary" disabled={!lines.length} onClick={async () => {
      try { const r = await api.post<any>('/api/quotes', { customerId: cust?.id ?? null, validDays: parseInt(days) || 7, notes, lines: lines.map(l => ({ productId: l.product.id, quantity: l.qty, discountPercent: l.disc || null })) })
        push('ok', `Cotización ${r.number} por ${money(r.total)} creada.`); setCreate(false); reload() } catch (e) { push('err', errText(e)) } }}>Crear cotización</button></>}>
      <div className="form2"><label>Cliente (opcional)<input value={cust ? cust.name : custQ} onChange={async e => { setCust(null); setCustQ(e.target.value); setCusts(e.target.value.length >= 2 ? await api.get<Customer[]>(`/api/customers?q=${encodeURIComponent(e.target.value)}`) : []) }} /></label>
        <label>Vigencia (días)<input value={days} onChange={e => setDays(e.target.value)} /></label></div>
      {!cust && custs.length > 0 && <div className="list">{custs.map(c => <button key={c.id} className="row" onClick={() => { setCust(c); setCusts([]) }}>{c.name}</button>)}</div>}
      <label>Agregar producto<input value={q} onChange={e => search(e.target.value)} placeholder="Buscar" /></label>
      {found.length > 0 && <div className="list">{found.map(p => <button key={p.id} className="row" onClick={() => { if (!lines.find(l => l.product.id === p.id)) setLines([...lines, { product: p, qty: 1, disc: 0 }]); setFound([]); setQ('') }}><span>{p.name}</span><small>{money(p.price)}</small></button>)}</div>}
      <table className="t"><thead><tr><th>Producto</th><th>Cant.</th><th>Desc. %</th><th className="r">Precio</th><th></th></tr></thead><tbody>
        {lines.map((l, i) => <tr key={l.product.id}><td>{l.product.name}</td>
          <td><input style={{ width: 80 }} value={l.qty} onChange={e => setLines(lines.map((x, k) => k === i ? { ...x, qty: parseFloat(e.target.value) || 0 } : x))} /></td>
          <td><input style={{ width: 70 }} value={l.disc} onChange={e => setLines(lines.map((x, k) => k === i ? { ...x, disc: parseFloat(e.target.value) || 0 } : x))} /></td>
          <td className="r">{money(l.product.price)}</td><td><button className="icon" onClick={() => setLines(lines.filter((_, k) => k !== i))}>✕</button></td></tr>)}</tbody></table>
      <label>Notas<input value={notes} onChange={e => setNotes(e.target.value)} /></label>
      <small className="muted">El total lo calcula el servidor al crear la cotización. Al convertirla, los precios se recalculan con la lista vigente y se cobra en la pantalla de Venta.</small>
    </Modal>}
  </div>
}
