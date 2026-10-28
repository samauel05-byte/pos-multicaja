import { useState } from 'react'
import { api } from '../api'
import { useSession } from '../session'
import { ErrorBox, Loading, useLoad } from '../ui'
import { money, num, d, dt, ymd, toCsv, download, methodName } from '../format'

type Tab = 'sales' | 'shifts' | 'low' | 'ar' | 'ap'
const TABS: [Tab, string][] = [['sales', 'Ventas'], ['shifts', 'Cierres de caja'], ['low', 'Inventario bajo'], ['ar', 'Por cobrar'], ['ap', 'Por pagar']]

export default function Reports() {
  const { boot } = useSession()
  const [tab, setTab] = useState<Tab>('sales')
  const today = new Date(); const monthAgo = new Date(today.getTime() - 29 * 864e5)
  const [from, setFrom] = useState(ymd(monthAgo)); const [to, setTo] = useState(ymd(today)); const [groupBy, setGroupBy] = useState('day')
  const sales = useLoad<any>(() => tab === 'sales' ? api.get(`/api/reports/sales?from=${from}&to=${to}&groupBy=${groupBy}`) : Promise.resolve(null), [tab, groupBy, from, to])
  const shifts = useLoad<any[]>(() => tab === 'shifts' ? api.get(`/api/shifts?from=${from}&to=${to}`) : Promise.resolve([]), [tab, from, to])
  const low = useLoad<any[]>(() => tab === 'low' ? api.get('/api/reports/low-stock') : Promise.resolve([]), [tab])
  const ar = useLoad<any[]>(() => tab === 'ar' ? api.get('/api/reports/receivables') : Promise.resolve([]), [tab])
  const ap = useLoad<any[]>(() => tab === 'ap' ? api.get('/api/reports/payables') : Promise.resolve([]), [tab])
  const cur = { sales, shifts, low, ar, ap }[tab] as ReturnType<typeof useLoad<any>>
  const rows: any[] = tab === 'sales' ? sales.data?.rows ?? [] : (cur.data ?? [])
  const max = Math.max(1, ...(sales.data?.rows ?? []).map((r: any) => r.total))
  const exportCsv = () => download(`reporte-${tab}-${from}_${to}.csv`, toCsv(rows))
  return <div className="page">
    <h2>Reportes</h2>
    <div className="tabs">{TABS.map(([k, l]) => <button key={k} className={tab === k ? 'active' : ''} onClick={() => setTab(k)}>{l}</button>)}</div>
    {(tab === 'sales' || tab === 'shifts') && <div className="toolbar"><label>Desde<input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label><label>Hasta<input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
      {tab === 'sales' && <label>Agrupar por<select value={groupBy} onChange={e => setGroupBy(e.target.value)}><option value="day">Fecha</option><option value="register">Caja</option><option value="user">Usuario</option><option value="method">Medio de pago</option></select></label>}</div>}
    <div className="toolbar"><button onClick={exportCsv} disabled={!rows.length}>⬇ Exportar CSV</button><button onClick={() => window.print()}>🖨 Imprimir</button></div>
    {cur.loading && <Loading />}{cur.error ? <ErrorBox error={cur.error} retry={cur.reload} /> : null}
    {tab === 'sales' && sales.data && <>
      <div className="cards"><div className="card"><small>Ventas</small><b>{money(sales.data.totals.sales)}</b><small>{sales.data.totals.count} tickets</small></div>
        <div className="card"><small>Impuestos</small><b>{money(sales.data.totals.tax)}</b></div><div className="card"><small>Descuentos</small><b>{money(sales.data.totals.discount)}</b></div>
        <div className="card"><small>Devoluciones</small><b>{money(sales.data.totals.returns)}</b></div><div className="card"><small>Anulaciones</small><b>{money(sales.data.totals.voids)}</b></div></div>
      <table className="t"><thead><tr><th>{({ day: 'Fecha', register: 'Caja', user: 'Usuario', method: 'Medio' } as any)[groupBy]}</th><th className="r">Tickets</th><th className="r">Total</th><th style={{ width: '35%' }}></th></tr></thead><tbody>
        {sales.data.rows.map((r: any) => <tr key={r.key}><td>{groupBy === 'method' ? methodName(r.key) : r.key}</td><td className="r">{r.count}</td><td className="r">{money(r.total)}</td><td><div className="bar"><i style={{ width: `${(r.total / max) * 100}%` }} /></div></td></tr>)}</tbody></table>
      <small className="muted">Las ventas anuladas no suman; las devoluciones parciales se muestran aparte. Fechas en la hora local del servidor.</small>
    </>}
    {tab === 'shifts' && <table className="t"><thead><tr><th>Caja</th><th>Cajero</th><th>Apertura</th><th>Cierre</th><th className="r">Fondo</th><th className="r">Esperado</th><th className="r">Contado</th><th className="r">Diferencia</th><th className="r">Tarjeta</th><th className="r">Transf.</th><th className="r">Crédito</th><th>Notas</th></tr></thead><tbody>
      {shifts.data?.map(s => <tr key={s.id}><td>{s.register}</td><td>{s.openedBy}</td><td>{dt(s.openedAt)}</td><td>{s.status === 'Open' ? <span className="chip">abierto</span> : dt(s.closedAt)}</td><td className="r">{money(s.openingFloat)}</td><td className="r">{s.expectedCash != null ? money(s.expectedCash) : ''}</td><td className="r">{s.countedCash != null ? money(s.countedCash) : ''}</td>
        <td className={`r ${s.cashDifference ? 'bad-t' : ''}`}>{s.cashDifference != null ? money(s.cashDifference) : ''}</td><td className="r">{s.cardTotal != null ? money(s.cardTotal) : ''}</td><td className="r">{s.transferTotal != null ? money(s.transferTotal) : ''}</td><td className="r">{s.creditTotal != null ? money(s.creditTotal) : ''}</td><td>{s.closeNotes}</td></tr>)}</tbody></table>}
    {tab === 'low' && <table className="t"><thead><tr><th>SKU</th><th>Producto</th><th className="r">Existencia</th><th className="r">Mínimo</th></tr></thead><tbody>{low.data?.map(r => <tr key={r.id}><td>{r.sku}</td><td>{r.name}</td><td className="r bad-t">{num(r.stock, 4)}</td><td className="r">{num(r.minStock)}</td></tr>)}</tbody></table>}
    {tab === 'ar' && <table className="t"><thead><tr><th>Ticket</th><th>Cliente</th><th>Fecha</th><th>Vence</th><th className="r">Días vencida</th><th className="r">Saldo</th></tr></thead><tbody>{ar.data?.map((r, i) => <tr key={i}><td>{r.number}</td><td>{r.customer}</td><td>{d(r.createdAt)}</td><td>{d(r.dueDate)}</td><td className={`r ${r.daysOverdue ? 'bad-t' : ''}`}>{r.daysOverdue || ''}</td><td className="r">{money(r.creditBalance)}</td></tr>)}</tbody></table>}
    {tab === 'ap' && <table className="t"><thead><tr><th>Compra</th><th>Proveedor</th><th>Recibida</th><th>Vence</th><th className="r">Días vencida</th><th className="r">Saldo</th></tr></thead><tbody>{ap.data?.map((r, i) => <tr key={i}><td>{r.number}</td><td>{r.supplier}</td><td>{d(r.receivedAt)}</td><td>{d(r.dueDate)}</td><td className={`r ${r.daysOverdue ? 'bad-t' : ''}`}>{r.daysOverdue || ''}</td><td className="r">{money(r.balance)}</td></tr>)}</tbody></table>}
    {!cur.loading && !cur.error && tab !== 'sales' && !rows.length && <div className="empty">Sin datos para mostrar</div>}
    <span hidden>{boot?.company.name}</span>
  </div>
}
