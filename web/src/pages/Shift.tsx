import { useState } from 'react'
import { api } from '../api'
import { useSession } from '../session'
import { Modal, ErrorBox, Loading, useLoad, useToast, errText } from '../ui'
import { money, dt } from '../format'
import type { ShiftSummary } from '../types'

const DENOMS = [2000, 1000, 500, 200, 100, 50, 25, 10, 5, 1]

export default function Shift() {
  const { boot, registerId, myShift, refreshBoot, me, can } = useSession(); const { push } = useToast()
  const reg = boot?.registers.find(r => r.id === registerId)
  const shift = myShift()
  const { data, error, loading, reload } = useLoad<{ s: ShiftSummary; moves: { id: string; createdAt: string; type: string; amount: number; reason?: string }[] } | null>(async () => {
    if (!shift) return null
    const [s, moves] = await Promise.all([api.get<ShiftSummary>(`/api/shifts/${shift.id}/summary`), api.get<any[]>(`/api/shifts/${shift.id}/movements`)])
    return { s, moves }
  }, [shift?.id])
  const [open, setOpen] = useState(false); const [floatAmt, setFloat] = useState('')
  const [move, setMove] = useState<null | 'in' | 'out'>(null); const [mAmt, setMAmt] = useState(''); const [mWhy, setMWhy] = useState('')
  const [closing, setClosing] = useState(false)
  const [closed, setClosed] = useState<ShiftSummary | null>(null)
  const [busy, setBusy] = useState(false)

  if (!reg) return <div className="page"><div className="empty">Elija la caja de esta computadora en el menú lateral.</div></div>

  const doOpen = async () => {
    setBusy(true)
    try { await api.post('/api/shifts/open', { registerId: reg.id, openingFloat: parseFloat(floatAmt.replace(',', '.')) || 0 }); await refreshBoot(); setOpen(false); push('ok', `${reg.name} abierta.`) }
    catch (e) { push('err', errText(e)) } finally { setBusy(false) }
  }
  const doMove = async () => {
    if (!shift) return
    setBusy(true)
    try { await api.post('/api/shifts/movement', { shiftId: shift.id, amount: parseFloat(mAmt.replace(',', '.')) || 0, reason: mWhy, isOut: move === 'out' }); setMove(null); setMAmt(''); setMWhy(''); await reload(); push('ok', 'Movimiento registrado.') }
    catch (e) { push('err', errText(e)) } finally { setBusy(false) }
  }

  return <div className="page">
    <h2>Caja y turno — {reg.name}</h2>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    {!shift && !closed && <div className="card center">
      <p>Esta caja no tiene un turno abierto.</p>
      <button className="primary big" onClick={() => setOpen(true)}>Abrir caja</button>
    </div>}
    {closed && <ClosedReport s={closed} onNew={() => { setClosed(null); setOpen(true) }} />}
    {data && shift && <>
      <div className="cards">
        <div className="card"><small>Abierta por</small><b>{data.s.openedBy}</b><small>{dt(data.s.openedAt)}</small></div>
        <div className="card"><small>Fondo inicial</small><b>{money(data.s.openingFloat)}</b></div>
        <div className="card"><small>Ventas ({data.s.salesCount})</small><b>{money(data.s.salesTotal)}</b></div>
        <div className="card hl"><small>Efectivo esperado en gaveta</small><b>{money(data.s.expectedCash)}</b></div>
      </div>
      <div className="two">
        <div className="card">
          <h3>Cuadre por medio de pago</h3>
          <table className="t"><tbody>
            <tr><td>Fondo inicial</td><td className="r">{money(data.s.openingFloat)}</td></tr>
            <tr><td>+ Ventas en efectivo (sin cambio)</td><td className="r">{money(data.s.cashSales)}</td></tr>
            <tr><td>+ Abonos de clientes en efectivo</td><td className="r">{money(data.s.cashPayments)}</td></tr>
            <tr><td>+ Entradas manuales</td><td className="r">{money(data.s.cashIn)}</td></tr>
            <tr><td>− Salidas manuales</td><td className="r">{money(data.s.cashOut)}</td></tr>
            <tr><td>− Devoluciones/anulaciones en efectivo</td><td className="r">{money(data.s.cashRefunds)}</td></tr>
            <tr className="sum"><td>Efectivo esperado</td><td className="r">{money(data.s.expectedCash)}</td></tr>
            <tr><td colSpan={2}><hr /></td></tr>
            <tr><td>Tarjeta (no entra a la gaveta)</td><td className="r">{money(data.s.cardSales)}</td></tr>
            <tr><td>Transferencia (no entra a la gaveta)</td><td className="r">{money(data.s.transferSales)}</td></tr>
            <tr><td>Crédito otorgado</td><td className="r">{money(data.s.creditSales)}</td></tr>
          </tbody></table>
          <div className="actions">
            <button onClick={() => setMove('in')}>＋ Entrada de efectivo</button>
            <button onClick={() => setMove('out')}>− Salida de efectivo</button>
            <button className="primary" onClick={() => setClosing(true)} disabled={me?.role === 'Cashier' && shift.openedByUserId !== me.id && !can('shifts.close_any')}>Arqueo y cierre</button>
          </div>
        </div>
        <div className="card">
          <h3>Movimientos de efectivo</h3>
          <table className="t"><thead><tr><th>Hora</th><th>Tipo</th><th>Motivo</th><th className="r">Monto</th></tr></thead><tbody>
            {data.moves.map(m => <tr key={m.id}><td>{dt(m.createdAt)}</td><td>{({ Opening: 'Apertura', CashIn: 'Entrada', CashOut: 'Salida', SaleCash: 'Venta', RefundCash: 'Devolución', CustomerPaymentCash: 'Abono' } as any)[m.type] ?? m.type}</td><td>{m.reason}</td><td className="r">{money(m.amount)}</td></tr>)}
          </tbody></table>
        </div>
      </div>
    </>}

    {open && <Modal title={`Abrir ${reg.name}`} onClose={() => setOpen(false)} footer={<><button onClick={() => setOpen(false)}>Cancelar</button><button className="primary" disabled={busy} onClick={doOpen}>Abrir caja</button></>}>
      <label>Fondo inicial (efectivo en gaveta)<input value={floatAmt} onChange={e => setFloat(e.target.value)} inputMode="decimal" autoFocus placeholder="0.00" onKeyDown={e => { if (e.key === 'Enter') doOpen() }} /></label>
    </Modal>}
    {move && <Modal title={move === 'in' ? 'Entrada de efectivo' : 'Salida de efectivo'} onClose={() => setMove(null)} footer={<><button onClick={() => setMove(null)}>Cancelar</button><button className="primary" disabled={busy || !mWhy.trim() || !(parseFloat(mAmt) > 0)} onClick={doMove}>Registrar</button></>}>
      <label>Monto<input value={mAmt} onChange={e => setMAmt(e.target.value)} inputMode="decimal" autoFocus /></label>
      <label>Motivo (obligatorio)<input value={mWhy} onChange={e => setMWhy(e.target.value)} /></label>
    </Modal>}
    {closing && data && shift && <CloseModal s={data.s} shiftId={shift.id} tolerance={boot!.company.cashDifferenceToleranceAmount} onClose={() => setClosing(false)}
      onClosed={async r => { setClosing(false); setClosed(r); await refreshBoot(); push('ok', 'Turno cerrado.') }} />}
  </div>
}

function CloseModal({ s, shiftId, tolerance, onClose, onClosed }: { s: ShiftSummary; shiftId: string; tolerance: number; onClose: () => void; onClosed: (r: ShiftSummary) => void }) {
  const [counts, setCounts] = useState<Record<string, string>>({}); const [manual, setManual] = useState(''); const [notes, setNotes] = useState(''); const [busy, setBusy] = useState(false)
  const { push } = useToast()
  const byDen = DENOMS.reduce((a, d) => a + d * (parseInt(counts[d] ?? '') || 0), 0)
  const counted = manual !== '' ? parseFloat(manual.replace(',', '.')) || 0 : byDen
  const diff = Math.round((counted - s.expectedCash) * 100) / 100
  const needNotes = Math.abs(diff) > tolerance
  const submit = async () => {
    setBusy(true)
    try {
      const den: Record<string, number> = {}; DENOMS.forEach(d => { const v = parseInt(counts[d] ?? ''); if (v > 0) den[String(d)] = v })
      onClosed(await api.post<ShiftSummary>('/api/shifts/close', { shiftId, countedCash: counted, notes: notes || null, denominations: manual === '' ? den : null }))
    } catch (e) { push('err', errText(e)) } finally { setBusy(false) }
  }
  return <Modal title="Arqueo y cierre de caja" onClose={onClose} wide footer={<><button onClick={onClose}>Cancelar</button>
    <button className="primary" disabled={busy || (needNotes && !notes.trim())} onClick={submit}>{busy ? 'Cerrando…' : 'Cerrar turno'}</button></>}>
    <div className="two">
      <div>
        <h4>Conteo por denominación</h4>
        {DENOMS.map(d => <div className="denom" key={d}><span>{money(d)}</span><span>×</span><input value={counts[d] ?? ''} inputMode="numeric" onChange={e => setCounts({ ...counts, [d]: e.target.value })} /><span className="r">{money(d * (parseInt(counts[d] ?? '') || 0))}</span></div>)}
        <label>…o total contado a mano<input value={manual} onChange={e => setManual(e.target.value)} inputMode="decimal" placeholder="opcional" /></label>
      </div>
      <div>
        <div className="paysum col">
          <div><span>Efectivo esperado</span><b>{money(s.expectedCash)}</b></div>
          <div><span>Efectivo contado</span><b>{money(counted)}</b></div>
          <div className={diff === 0 ? 'good' : 'bad'}><span>Diferencia</span><b>{diff > 0 ? '+' : ''}{money(diff)}</b></div>
        </div>
        <small className="muted">{diff < 0 ? 'Faltante' : diff > 0 ? 'Sobrante' : 'Cuadra'} · tarjeta {money(s.cardSales)} · transferencia {money(s.transferSales)} · crédito {money(s.creditSales)} (no forman parte del efectivo)</small>
        <label>Notas {needNotes && <b className="bad-t">(obligatorias: la diferencia supera {money(tolerance)})</b>}<textarea value={notes} onChange={e => setNotes(e.target.value)} rows={3} /></label>
      </div>
    </div>
  </Modal>
}

function ClosedReport({ s, onNew }: { s: ShiftSummary; onNew: () => void }) {
  return <div className="card">
    <h3>Turno cerrado — {s.registerName}</h3>
    <table className="t"><tbody>
      <tr><td>Efectivo esperado</td><td className="r">{money(s.expectedCash)}</td></tr>
      <tr><td>Efectivo contado</td><td className="r">{money(s.countedCash)}</td></tr>
      <tr className="sum"><td>Diferencia</td><td className={`r ${s.cashDifference ? 'bad-t' : ''}`}>{money(s.cashDifference)}</td></tr>
      <tr><td>Tarjeta</td><td className="r">{money(s.cardSales)}</td></tr><tr><td>Transferencia</td><td className="r">{money(s.transferSales)}</td></tr>
      <tr><td>Crédito</td><td className="r">{money(s.creditSales)}</td></tr><tr><td>Ventas</td><td className="r">{s.salesCount} · {money(s.salesTotal)}</td></tr>
    </tbody></table>
    <div className="actions"><button onClick={() => window.print()}>🖨 Imprimir resumen</button><button className="primary" onClick={onNew}>Abrir nuevo turno</button></div>
  </div>
}
