import { useState } from 'react'
import { api } from '../api'
import { useSession } from '../session'
import { ErrorBox, Loading, Modal, errText, useLoad, useToast } from '../ui'
import { money, num, dt } from '../format'

interface Row { id: string; sku: string; name: string; minStock: number; locationId: string; location: string; quantity: number; low: boolean }
interface K { id: string; createdAt: string; type: string; quantity: number; balanceAfter: number; unitCost: number; reference?: string; reason?: string }
const TYPES: Record<string, string> = { Purchase: 'Compra', Sale: 'Venta', Return: 'Devolución', Void: 'Anulación', AdjustmentIn: 'Ajuste (+)', AdjustmentOut: 'Ajuste (−)', Initial: 'Inicial', TransferIn: 'Traspaso +', TransferOut: 'Traspaso −' }

export default function Inventory() {
  const { can } = useSession(); const { push } = useToast()
  const [q, setQ] = useState('')
  const { data, error, loading, reload } = useLoad<Row[]>(() => api.get(`/api/inventory?q=${encodeURIComponent(q)}`), [])
  const [kardex, setKardex] = useState<Row | null>(null); const [adjust, setAdjust] = useState<Row | null>(null)
  const [aQty, setAQty] = useState(''); const [aOut, setAOut] = useState(true); const [aWhy, setAWhy] = useState('')
  const k = useLoad<K[]>(() => kardex ? api.get(`/api/inventory/kardex/${kardex.id}`) : Promise.resolve([]), [kardex?.id])
  return <div className="page">
    <h2>Inventario por ubicación</h2>
    <div className="toolbar"><input placeholder="Buscar" value={q} onChange={e => setQ(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') reload() }} /><button onClick={reload}>Buscar</button></div>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    <table className="t"><thead><tr><th>SKU</th><th>Producto</th><th>Ubicación</th><th className="r">Existencia</th><th className="r">Mínimo</th><th></th></tr></thead><tbody>
      {data?.map(r => <tr key={r.id + r.locationId}><td>{r.sku}</td><td>{r.name}</td><td>{r.location}</td><td className={`r ${r.low ? 'bad-t' : ''}`}>{num(r.quantity, 4)} {r.low && '⚠'}</td><td className="r">{num(r.minStock)}</td>
        <td className="r"><button onClick={() => setKardex(r)}>Kardex</button> {can('inventory.adjust') && <button onClick={() => { setAdjust(r); setAQty(''); setAWhy(''); setAOut(true) }}>Ajustar</button>}</td></tr>)}
    </tbody></table>
    {kardex && <Modal title={`Kardex — ${kardex.name}`} onClose={() => setKardex(null)} wide>
      {k.loading ? <Loading /> : <table className="t"><thead><tr><th>Fecha</th><th>Movimiento</th><th>Ref.</th><th>Motivo</th><th className="r">Cantidad</th><th className="r">Saldo</th><th className="r">Costo</th></tr></thead><tbody>
        {k.data?.map(m => <tr key={m.id}><td>{dt(m.createdAt)}</td><td>{TYPES[m.type] ?? m.type}</td><td>{m.reference}</td><td>{m.reason}</td><td className={`r ${m.quantity < 0 ? 'bad-t' : ''}`}>{m.quantity > 0 ? '+' : ''}{num(m.quantity, 4)}</td><td className="r">{num(m.balanceAfter, 4)}</td><td className="r">{money(m.unitCost)}</td></tr>)}
      </tbody></table>}
    </Modal>}
    {adjust && <Modal title={`Ajuste — ${adjust.name}`} onClose={() => setAdjust(null)} footer={<><button onClick={() => setAdjust(null)}>Cancelar</button>
      <button className="primary" disabled={!aWhy.trim() || !(parseFloat(aQty) > 0)} onClick={async () => {
        try { await api.post('/api/inventory/adjust', { productId: adjust.id, locationId: adjust.locationId, quantity: parseFloat(aQty.replace(',', '.')), isOut: aOut, reason: aWhy }); setAdjust(null); push('ok', 'Ajuste registrado en el kardex.'); reload() } catch (e) { push('err', errText(e)) } }}>Registrar ajuste</button></>}>
      <p>Existencia actual: <b>{num(adjust.quantity, 4)}</b></p>
      <div className="payrow"><label><input type="radio" checked={aOut} onChange={() => setAOut(true)} /> Salida (merma, daño…)</label><label><input type="radio" checked={!aOut} onChange={() => setAOut(false)} /> Entrada (sobrante)</label></div>
      <label>Cantidad<input value={aQty} onChange={e => setAQty(e.target.value)} inputMode="decimal" autoFocus /></label>
      <label>Justificación (obligatoria)<input value={aWhy} onChange={e => setAWhy(e.target.value)} /></label>
    </Modal>}
  </div>
}
