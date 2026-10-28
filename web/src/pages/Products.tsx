import { useState } from 'react'
import { api } from '../api'
import { useSession } from '../session'
import { ErrorBox, Loading, Modal, errText, useLoad, useToast } from '../ui'
import { money, num } from '../format'
import type { Product } from '../types'

export default function Products() {
  const { boot, refreshBoot } = useSession(); const { push } = useToast()
  const [q, setQ] = useState('')
  const { data, error, loading, reload } = useLoad<Product[]>(() => api.get(`/api/products?limit=300&q=${encodeURIComponent(q)}`), [])
  const [edit, setEdit] = useState<Partial<Product> & { initialStock?: number } | null>(null)
  const [newCat, setNewCat] = useState('')

  const save = async () => {
    if (!edit) return
    try {
      const body = { sku: edit.sku, barcode: edit.barcode || null, name: edit.name, categoryId: edit.categoryId || null, unitId: edit.unitId ?? boot!.units[0].id, taxRateId: edit.taxRateId ?? boot!.taxRates[0].id,
        cost: edit.cost ?? 0, price: edit.price ?? 0, minStock: edit.minStock ?? 0, tracksStock: edit.tracksStock ?? true, active: edit.active ?? true,
        initialStock: edit.initialStock ?? null, initialLocationId: boot!.locations[0].id }
      if (edit.id) await api.put(`/api/products/${edit.id}`, body); else await api.post('/api/products', body)
      setEdit(null); push('ok', 'Producto guardado.'); reload()
    } catch (e) { push('err', errText(e)) }
  }
  const set = (k: string, v: any) => setEdit(e => ({ ...e!, [k]: v }))
  return <div className="page">
    <h2>Productos</h2>
    <div className="toolbar"><input placeholder="Buscar nombre, SKU o código de barras" value={q} onChange={e => setQ(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') reload() }} /><button onClick={reload}>Buscar</button>
      <button className="primary" onClick={() => setEdit({ tracksStock: true, active: true })}>＋ Nuevo producto</button></div>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    <table className="t hover"><thead><tr><th>SKU</th><th>Código</th><th>Nombre</th><th>Categoría</th><th className="r">Costo</th><th className="r">Precio</th><th>Imp.</th><th className="r">Existencia</th><th className="r">Mín.</th><th>Estado</th></tr></thead><tbody>
      {data?.map(p => <tr key={p.id} onClick={() => setEdit(p)}><td>{p.sku}</td><td>{p.barcode}</td><td>{p.name}</td><td>{p.categoryName}</td><td className="r">{money(p.cost)}</td><td className="r">{money(p.price)}</td>
        <td>{(p.taxRate * 100).toFixed(0)}%</td><td className={`r ${p.tracksStock && p.stock <= p.minStock ? 'bad-t' : ''}`}>{p.tracksStock ? `${num(p.stock)} ${p.unitAbbr}` : '—'}</td><td className="r">{num(p.minStock)}</td><td>{p.active ? 'Activo' : <span className="chip bad">Inactivo</span>}</td></tr>)}
    </tbody></table>
    {edit && <Modal title={edit.id ? 'Editar producto' : 'Nuevo producto'} onClose={() => setEdit(null)} wide footer={<><button onClick={() => setEdit(null)}>Cancelar</button><button className="primary" onClick={save} disabled={!edit.name || !edit.sku}>Guardar</button></>}>
      <div className="form2">
        <label>SKU / código interno<input value={edit.sku ?? ''} onChange={e => set('sku', e.target.value)} /></label>
        <label>Código de barras<input value={edit.barcode ?? ''} onChange={e => set('barcode', e.target.value)} /></label>
        <label className="span2">Nombre<input value={edit.name ?? ''} onChange={e => set('name', e.target.value)} /></label>
        <label>Categoría<select value={edit.categoryId ?? ''} onChange={e => set('categoryId', e.target.value)}><option value="">—</option>{boot!.categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
        <label>Unidad<select value={edit.unitId ?? boot!.units[0].id} onChange={e => set('unitId', e.target.value)}>{boot!.units.map(u => <option key={u.id} value={u.id}>{u.name} ({u.abbreviation}){u.allowsDecimals ? ' · decimales' : ''}</option>)}</select></label>
        <label>Impuesto<select value={edit.taxRateId ?? boot!.taxRates[0].id} onChange={e => set('taxRateId', e.target.value)}>{boot!.taxRates.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select></label>
        <label>Stock mínimo<input value={edit.minStock ?? 0} inputMode="decimal" onChange={e => set('minStock', parseFloat(e.target.value) || 0)} /></label>
        <label>Costo<input value={edit.cost ?? 0} inputMode="decimal" onChange={e => set('cost', parseFloat(e.target.value) || 0)} /></label>
        <label>Precio de venta {boot!.company.pricesIncludeTax ? '(impuesto incluido)' : '(sin impuesto)'}<input value={edit.price ?? 0} inputMode="decimal" onChange={e => set('price', parseFloat(e.target.value) || 0)} /></label>
        {!edit.id && <label>Existencia inicial<input value={edit.initialStock ?? ''} inputMode="decimal" onChange={e => set('initialStock', parseFloat(e.target.value) || 0)} /></label>}
        <label className="check"><input type="checkbox" checked={edit.tracksStock ?? true} onChange={e => set('tracksStock', e.target.checked)} /> Controlar existencias</label>
        <label className="check"><input type="checkbox" checked={edit.active ?? true} onChange={e => set('active', e.target.checked)} /> Activo</label>
      </div>
      <hr /><div className="payrow"><label>Nueva categoría<input value={newCat} onChange={e => setNewCat(e.target.value)} /></label>
        <button disabled={!newCat.trim()} onClick={async () => { try { await api.post('/api/categories', { name: newCat }); setNewCat(''); await refreshBoot(); push('ok', 'Categoría creada.') } catch (e) { push('err', errText(e)) } }}>Crear</button></div>
      <small className="muted">Cambiar precio/impuesto no altera ventas pasadas: cada documento conserva sus valores históricos.</small>
    </Modal>}
  </div>
}
