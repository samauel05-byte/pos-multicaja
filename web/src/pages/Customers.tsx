import { useState } from 'react'
import { api, uuid } from '../api'
import { useSession } from '../session'
import { ErrorBox, Loading, Modal, errText, useLoad, useToast } from '../ui'
import { money, d, dt, methodName } from '../format'
import type { Customer } from '../types'

interface Statement { customer: { id: string; name: string; creditLimit: number; balance: number; available: number; creditDays: number }
  sales: { id: string; number: string; createdAt: string; status: string; total: number; creditAmount: number; creditBalance: number; dueDate?: string; overdue: boolean }[]
  payments: { id: string; number: string; createdAt: string; method: string; amount: number; reference?: string }[] }

export default function Customers() {
  const { can, registerId, myShift, refreshBoot } = useSession(); const { push } = useToast()
  const [q, setQ] = useState(''); const [onlyDebt, setOnlyDebt] = useState(false)
  const { data, error, loading, reload } = useLoad<Customer[]>(() => api.get(`/api/customers?q=${encodeURIComponent(q)}${onlyDebt ? '&withBalance=true' : ''}`), [onlyDebt])
  const [edit, setEdit] = useState<Partial<Customer> | null>(null)
  const [stmt, setStmt] = useState<string | null>(null)
  const st = useLoad<Statement | null>(() => stmt ? api.get(`/api/customers/${stmt}/statement`) : Promise.resolve(null), [stmt])
  const [pay, setPay] = useState(false); const [amt, setAmt] = useState(''); const [method, setMethod] = useState('Cash'); const [ref, setRef] = useState(''); const [opKey, setOpKey] = useState(uuid())

  const save = async () => {
    if (!edit) return
    try {
      const body = { name: edit.name, taxId: edit.taxId, phone: edit.phone, email: edit.email, creditLimit: can('customers.credit_limit') ? (edit.creditLimit ?? 0) : null, creditDays: edit.creditDays ?? 30, active: edit.active ?? true }
      if (edit.id) await api.put(`/api/customers/${edit.id}`, body); else await api.post('/api/customers', body)
      setEdit(null); push('ok', 'Cliente guardado.'); reload()
    } catch (e) { push('err', errText(e)) }
  }
  const doPay = async () => {
    try {
      const r = await api.post<any>('/api/customers/payments', { operationKey: opKey, customerId: stmt, registerId, method, amount: parseFloat(amt.replace(',', '.')), reference: ref || null })
      push('ok', `Abono ${r.number} registrado. Nuevo saldo ${money(r.newBalance)}.`); setPay(false); setAmt(''); setOpKey(uuid()); st.reload(); reload(); refreshBoot()
    } catch (e) { push('err', errText(e)) }
  }
  const set = (k: string, v: any) => setEdit(e => ({ ...e!, [k]: v }))
  return <div className="page">
    <h2>Clientes y crédito</h2>
    <div className="toolbar"><input placeholder="Nombre, RNC o teléfono" value={q} onChange={e => setQ(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') reload() }} /><button onClick={reload}>Buscar</button>
      <label className="check"><input type="checkbox" checked={onlyDebt} onChange={e => setOnlyDebt(e.target.checked)} /> Solo con saldo</label>
      <button className="primary" onClick={() => setEdit({ active: true, creditDays: 30, creditLimit: 0 })}>＋ Nuevo cliente</button></div>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    <table className="t hover"><thead><tr><th>Cliente</th><th>Teléfono</th><th className="r">Límite</th><th className="r">Saldo</th><th className="r">Disponible</th><th>Días</th></tr></thead><tbody>
      {data?.map(c => <tr key={c.id} onClick={() => setStmt(c.id)}><td>{c.name}{!c.active && <span className="chip bad">inactivo</span>}</td><td>{c.phone}</td><td className="r">{money(c.creditLimit)}</td><td className={`r ${c.balance > 0 ? 'bad-t' : ''}`}>{money(c.balance)}</td><td className="r">{money(c.available)}</td><td>{c.creditDays}</td></tr>)}
    </tbody></table>
    {stmt && <Modal title={st.data ? `Estado de cuenta — ${st.data.customer.name}` : 'Estado de cuenta'} onClose={() => setStmt(null)} wide
      footer={st.data && <><button onClick={() => { const c = data?.find(x => x.id === stmt); if (c) { setStmt(null); setEdit(c) } }}>Editar cliente</button>
        <button className="primary" disabled={!st.data.customer.balance} onClick={() => { setPay(true); setOpKey(uuid()); setAmt(String(st.data!.customer.balance)) }}>Registrar abono</button></>}>
      {st.loading || !st.data ? <Loading /> : <>
        <div className="cards"><div className="card"><small>Límite</small><b>{money(st.data.customer.creditLimit)}</b></div><div className="card hl"><small>Saldo pendiente</small><b>{money(st.data.customer.balance)}</b></div><div className="card"><small>Disponible</small><b>{money(st.data.customer.available)}</b></div></div>
        <h4>Ventas</h4><table className="t"><thead><tr><th>Ticket</th><th>Fecha</th><th>Estado</th><th className="r">Total</th><th className="r">A crédito</th><th className="r">Saldo</th><th>Vence</th></tr></thead><tbody>
          {st.data.sales.map(s => <tr key={s.id}><td>{s.number}</td><td>{dt(s.createdAt)}</td><td>{s.status === 'Voided' ? 'Anulada' : 'OK'}</td><td className="r">{money(s.total)}</td><td className="r">{money(s.creditAmount)}</td><td className="r">{money(s.creditBalance)}</td><td className={s.overdue ? 'bad-t' : ''}>{s.creditBalance > 0 ? d(s.dueDate) + (s.overdue ? ' · VENCIDA' : '') : ''}</td></tr>)}</tbody></table>
        <h4>Abonos</h4><table className="t"><tbody>{st.data.payments.map(p => <tr key={p.id}><td>{p.number}</td><td>{dt(p.createdAt)}</td><td>{methodName(p.method)}</td><td>{p.reference}</td><td className="r">{money(p.amount)}</td></tr>)}</tbody></table>
      </>}
    </Modal>}
    {pay && <Modal title="Registrar abono" onClose={() => setPay(false)} footer={<><button onClick={() => setPay(false)}>Cancelar</button><button className="primary" disabled={!(parseFloat(amt) > 0) || (method === 'Cash' && !myShift())} onClick={doPay}>Registrar</button></>}>
      <label>Monto<input value={amt} onChange={e => setAmt(e.target.value)} inputMode="decimal" autoFocus /></label>
      <label>Medio<select value={method} onChange={e => setMethod(e.target.value)}><option value="Cash">Efectivo (entra a la caja actual)</option><option value="Card">Tarjeta</option><option value="Transfer">Transferencia</option></select></label>
      {method !== 'Cash' && <label>Referencia<input value={ref} onChange={e => setRef(e.target.value)} /></label>}
      {method === 'Cash' && !myShift() && <div className="errbox">Abra un turno en esta caja para recibir efectivo.</div>}
      <small className="muted">Se aplica a las ventas más antiguas primero.</small>
    </Modal>}
    {edit && <Modal title={edit.id ? 'Editar cliente' : 'Nuevo cliente'} onClose={() => setEdit(null)} footer={<><button onClick={() => setEdit(null)}>Cancelar</button><button className="primary" disabled={!edit.name} onClick={save}>Guardar</button></>}>
      <label>Nombre<input value={edit.name ?? ''} onChange={e => set('name', e.target.value)} autoFocus /></label>
      <label>RNC / Cédula<input value={edit.taxId ?? ''} onChange={e => set('taxId', e.target.value)} /></label>
      <label>Teléfono<input value={edit.phone ?? ''} onChange={e => set('phone', e.target.value)} /></label>
      <label>Correo<input value={edit.email ?? ''} onChange={e => set('email', e.target.value)} /></label>
      <label>Límite de crédito {!can('customers.credit_limit') && <small>(solo supervisor)</small>}<input value={edit.creditLimit ?? 0} disabled={!can('customers.credit_limit')} inputMode="decimal" onChange={e => set('creditLimit', parseFloat(e.target.value) || 0)} /></label>
      <label>Días de crédito<input value={edit.creditDays ?? 30} inputMode="numeric" onChange={e => set('creditDays', parseInt(e.target.value) || 0)} /></label>
      <label className="check"><input type="checkbox" checked={edit.active ?? true} onChange={e => set('active', e.target.checked)} /> Activo</label>
    </Modal>}
  </div>
}
