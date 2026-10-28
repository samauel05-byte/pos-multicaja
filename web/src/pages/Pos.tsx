import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, ApiError, NetworkError, uuid } from '../api'
import { useSession } from '../session'
import { AuthModal, Modal, errText, useToast, type Auth } from '../ui'
import { money, num, methodName } from '../format'
import type { Customer, Product, Sale } from '../types'
import { printTicket } from '../print'

interface CartLine { product: Product; qty: number; discountPercent: number }
interface Preview { lines: { lineTotal: number; discountAmount: number; discountPercent: number; unitPrice: number }[]; subtotal: number; discountTotal: number; taxTotal: number; total: number }
interface PendingOp { key: string; registerId: string; body: any; at: number }

const PENDING_KEY = 'pos.pendingSale'
const readPending = (): PendingOp | null => { try { const s = localStorage.getItem(PENDING_KEY); return s ? JSON.parse(s) : null } catch { return null } }
const writePending = (p: PendingOp | null) => { try { p ? localStorage.setItem(PENDING_KEY, JSON.stringify(p)) : localStorage.removeItem(PENDING_KEY) } catch { /* noop */ } }

export default function Pos() {
  const { boot, me, registerId, serverUp, myShift, refreshBoot } = useSession()
  const { push } = useToast()
  const searchRef = useRef<HTMLInputElement>(null)
  const [q, setQ] = useState('')
  const [results, setResults] = useState<Product[]>([])
  const [searching, setSearching] = useState(false)
  const [cart, setCart] = useState<CartLine[]>([])
  const [selected, setSelected] = useState(0)
  const [customer, setCustomer] = useState<Customer | null>(null)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [previewErr, setPreviewErr] = useState<string | null>(null)
  const [showPay, setShowPay] = useState(false)
  const [showCustomer, setShowCustomer] = useState(false)
  const [discountFor, setDiscountFor] = useState<number | null>(null)
  const [done, setDone] = useState<Sale | null>(null)
  const [pending, setPending] = useState<PendingOp | null>(readPending())
  const [quoteId, setQuoteId] = useState<string | null>(null)

  const reg = boot?.registers.find(r => r.id === registerId)
  const shift = myShift()
  const canSell = !!reg && !!shift && serverUp && (me?.role !== 'Cashier' || shift.openedByUserId === me.id)
  const company = boot!.company

  // ---- Búsqueda (por código de barras, SKU o nombre) ----
  useEffect(() => {
    const term = q.includes('*') ? q.split('*').slice(1).join('*') : q
    if (!term.trim()) { api.get<Product[]>(`/api/products?active=true&limit=24${reg ? `&locationId=${reg.locationId}` : ''}`).then(setResults).catch(() => {}); return }
    setSearching(true)
    const t = setTimeout(async () => {
      try { setResults(await api.get<Product[]>(`/api/products?active=true&limit=40&q=${encodeURIComponent(term.trim())}${reg ? `&locationId=${reg.locationId}` : ''}`)) }
      catch { /* el aviso de servidor caído ya es visible */ } finally { setSearching(false) }
    }, 180)
    return () => clearTimeout(t)
  }, [q, reg])

  const addProduct = useCallback((p: Product, qty = 1) => {
    if (!p.active) return
    setCart(c => {
      const i = c.findIndex(l => l.product.id === p.id)
      if (i >= 0) { const n = [...c]; n[i] = { ...n[i], qty: +(n[i].qty + qty).toFixed(4) }; setSelected(i); return n }
      setSelected(c.length); return [...c, { product: p, qty, discountPercent: 0 }]
    })
    if (p.tracksStock && p.stock <= 0) push('info', `"${p.name}" figura sin existencia; el servidor confirmará al cobrar.`)
  }, [push])

  const submitSearch = async () => {
    const m = q.match(/^(\d+(?:[.,]\d+)?)\*(.+)$/)
    const qty = m ? parseFloat(m[1].replace(',', '.')) : 1
    const term = (m ? m[2] : q).trim()
    if (!term) return
    try {
      const list = await api.get<Product[]>(`/api/products?active=true&limit=10&q=${encodeURIComponent(term)}${reg ? `&locationId=${reg.locationId}` : ''}`)
      const exact = list.find(p => p.barcode === term || p.sku.toLowerCase() === term.toLowerCase())
      const pick = exact ?? (list.length === 1 ? list[0] : null)
      if (pick) { addProduct(pick, qty); setQ('') }
      else if (list.length === 0) push('err', `No se encontró "${term}".`)
      else { setResults(list); push('info', 'Varios resultados: elija uno de la lista.') }
    } catch (e) { push('err', errText(e)) }
  }

  // ---- Totales: SIEMPRE calculados por el servidor ----
  useEffect(() => {
    if (!cart.length) { setPreview(null); setPreviewErr(null); return }
    const t = setTimeout(async () => {
      try {
        setPreview(await api.post<Preview>('/api/sales/preview', { lines: cart.map(l => ({ productId: l.product.id, quantity: l.qty, discountPercent: l.discountPercent || null })) }))
        setPreviewErr(null)
      } catch (e) { setPreviewErr(errText(e)) }
    }, 120)
    return () => clearTimeout(t)
  }, [cart])

  // Cotización enviada desde la pantalla de cotizaciones
  useEffect(() => {
    let raw: string | null = null
    try { raw = sessionStorage.getItem('pos.loadQuote'); sessionStorage.removeItem('pos.loadQuote') } catch { /* noop */ }
    if (!raw) return
    ;(async () => {
      try {
        const qt = JSON.parse(raw!) as { id: string; customerId?: string; lines: { productId: string; quantity: number; discountPercent: number }[] }
        const all = await Promise.all(qt.lines.map(async l => (await api.get<Product[]>(`/api/products?limit=500`)).find(p => p.id === l.productId)))
        const lines: CartLine[] = []
        qt.lines.forEach((l, i) => { const p = all[i]; if (p) lines.push({ product: p, qty: l.quantity, discountPercent: l.discountPercent }) })
        setCart(lines); setQuoteId(qt.id)
        if (qt.customerId) { const cs = await api.get<Customer[]>('/api/customers'); setCustomer(cs.find(c => c.id === qt.customerId) ?? null) }
        push('info', 'Cotización cargada. Los precios se recalculan con la lista vigente.')
      } catch (e) { push('err', errText(e)) }
    })()
  }, []) // eslint-disable-line

  const clear = useCallback(() => { setCart([]); setCustomer(null); setQuoteId(null); setDone(null); setQ(''); setTimeout(() => searchRef.current?.focus(), 50) }, [])

  // ---- Atajos de teclado ----
  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if (showPay || showCustomer || discountFor !== null || done) return
      if (e.key === 'F2') { e.preventDefault(); searchRef.current?.focus() }
      else if (e.key === 'F4') { e.preventDefault(); setShowCustomer(true) }
      else if (e.key === 'F9') { e.preventDefault(); if (cart.length && canSell) setShowPay(true) }
      else if (e.key === 'F10') { e.preventDefault(); if (cart.length && confirm('¿Descartar la venta actual?')) clear() }
      else if (e.key === 'ArrowDown' && document.activeElement !== searchRef.current) setSelected(s => Math.min(cart.length - 1, s + 1))
      else if (e.key === 'ArrowUp' && document.activeElement !== searchRef.current) setSelected(s => Math.max(0, s - 1))
      else if (e.key === 'Delete' && document.activeElement !== searchRef.current && cart[selected]) { setCart(c => c.filter((_, i) => i !== selected)); setSelected(0) }
    }
    window.addEventListener('keydown', h); return () => window.removeEventListener('keydown', h)
  }, [cart, selected, canSell, showPay, showCustomer, discountFor, done, clear])

  // ---- Cobro sin confirmar tras corte de red: se consulta el estado ANTES de repetir ----
  const resolvePending = useCallback(async (p: PendingOp) => {
    try {
      const r = await api.get<{ exists: boolean; sale?: Sale }>(`/api/sales/by-operation/${p.key}`)
      if (r.exists && r.sale) { writePending(null); setPending(null); setDone(r.sale); push('ok', `La venta ${r.sale.number} SÍ se había registrado. No se cobró dos veces.`) }
      else push('info', 'El servidor confirma que esa venta NO se registró. Puede reintentar el cobro con seguridad.')
      return r.exists
    } catch (e) { push('err', 'No se pudo consultar el estado: ' + errText(e)); return null }
  }, [push])
  useEffect(() => { if (pending && serverUp) resolvePending(pending) }, [serverUp]) // eslint-disable-line

  const total = preview?.total ?? 0

  if (!reg) return <div className="page"><div className="empty">Elija la caja de esta computadora en el menú lateral.</div></div>

  return <div className="pos">
    <section className="pos-left">
      <div className="searchbar">
        <input ref={searchRef} value={q} onChange={e => setQ(e.target.value)} autoFocus
          placeholder="Escanee el código o escriba el nombre…  (F2)   Ej: 3*7501000000011"
          onKeyDown={e => { if (e.key === 'Enter') submitSearch() }} aria-label="Buscar producto" />
        {searching && <span className="spinner small" />}
      </div>
      {!shift && <div className="warnbar">La caja <b>{reg.name}</b> no tiene turno abierto. <Link to="/caja">Abrir caja</Link> para poder cobrar.</div>}
      {shift && me?.role === 'Cashier' && shift.openedByUserId !== me.id && <div className="warnbar">El turno de esta caja pertenece a otro cajero.</div>}
      {pending && <div className="warnbar">
        ⚠ Hay un cobro sin confirmar (corte de red). <button onClick={() => resolvePending(pending)}>Consultar estado</button>
        <button onClick={() => { if (confirm('Solo descarte si ya consultó y el servidor indica que NO se registró. ¿Descartar?')) { writePending(null); setPending(null) } }}>Descartar</button>
      </div>}
      <div className="grid">
        {results.map(p => <button key={p.id} className={`tile ${p.tracksStock && p.stock <= 0 ? 'out' : ''}`} onClick={() => { addProduct(p); searchRef.current?.focus() }}>
          <span className="tname">{p.name}</span>
          <span className="tprice">{money(p.price)}</span>
          <span className="tmeta">{p.sku} · {p.tracksStock ? `${num(p.stock)} ${p.unitAbbr}` : 'sin control'}</span>
        </button>)}
        {!results.length && !searching && <div className="empty">Sin resultados</div>}
      </div>
    </section>

    <section className="pos-right">
      <div className="cart-head">
        <b>Venta actual</b>
        <button onClick={() => setShowCustomer(true)}>{customer ? `👤 ${customer.name}` : '👤 Cliente (F4)'}</button>
        {quoteId && <span className="chip">desde cotización</span>}
      </div>
      <div className="cart">
        {!cart.length && <div className="empty">El carrito está vacío</div>}
        {cart.map((l, i) => {
          const pl = preview?.lines[i]
          return <div key={l.product.id} className={`cline ${i === selected ? 'sel' : ''}`} onClick={() => setSelected(i)}>
            <div className="cname">{l.product.name}<small>{money(l.product.price)} c/u{l.discountPercent > 0 ? ` · desc. ${l.discountPercent}%` : ''}</small></div>
            <div className="qty">
              <button aria-label="menos" onClick={() => setCart(c => c.map((x, k) => k === i ? { ...x, qty: Math.max(l.product.allowsDecimals ? 0.25 : 1, +(x.qty - (l.product.allowsDecimals ? 0.25 : 1)).toFixed(4)) } : x))}>−</button>
              <input value={l.qty} inputMode="decimal" onChange={e => { const v = parseFloat(e.target.value.replace(',', '.')); if (v > 0) setCart(c => c.map((x, k) => k === i ? { ...x, qty: l.product.allowsDecimals ? v : Math.floor(v) || 1 } : x)) }} aria-label="Cantidad" />
              <button aria-label="más" onClick={() => setCart(c => c.map((x, k) => k === i ? { ...x, qty: +(x.qty + (l.product.allowsDecimals ? 0.25 : 1)).toFixed(4) } : x))}>+</button>
            </div>
            <div className="ctotal">{pl ? money(pl.lineTotal) : '…'}</div>
            <button className="icon" title="Descuento" onClick={e => { e.stopPropagation(); setDiscountFor(i) }}>%</button>
            <button className="icon" title="Quitar" onClick={e => { e.stopPropagation(); setCart(c => c.filter((_, k) => k !== i)) }}>✕</button>
          </div>
        })}
      </div>
      <div className="totals">
        {previewErr && <div className="errbox">{previewErr}</div>}
        <div><span>Subtotal</span><span>{money(preview?.subtotal)}</span></div>
        {!!preview?.discountTotal && <div><span>Descuentos</span><span>−{money(preview.discountTotal)}</span></div>}
        <div><span>Impuestos</span><span>{money(preview?.taxTotal)}</span></div>
        <div className="grand"><span>TOTAL</span><span>{money(total)}</span></div>
        <button className="primary huge" disabled={!cart.length || !canSell || !preview || !!previewErr} onClick={() => setShowPay(true)}>
          {!serverUp ? 'Sin conexión con el servidor' : !shift ? 'Abra la caja para cobrar' : 'Cobrar (F9)'}
        </button>
        <div className="keys muted">F2 buscar · F4 cliente · F9 cobrar · F10 descartar · Supr quitar línea · ↑↓ elegir</div>
      </div>
    </section>

    {discountFor !== null && cart[discountFor] && <DiscountModal line={cart[discountFor]} limit={me!.role === 'Cashier' ? company.cashierMaxDiscountPercent : company.supervisorMaxDiscountPercent}
      onClose={() => setDiscountFor(null)} onSet={v => { setCart(c => c.map((x, k) => k === discountFor ? { ...x, discountPercent: v } : x)); setDiscountFor(null) }} />}
    {showCustomer && <CustomerPicker onClose={() => setShowCustomer(false)} onPick={c => { setCustomer(c); setShowCustomer(false); searchRef.current?.focus() }} />}
    {showPay && preview && <PayModal total={total} customer={customer} registerId={reg.id} cart={cart} quoteId={quoteId} onClose={() => setShowPay(false)}
      onPending={p => { writePending(p); setPending(p) }} onDone={s => { writePending(null); setPending(null); setShowPay(false); setDone(s); refreshBoot() }} />}
    {done && <DoneModal sale={done} onNew={clear} />}
  </div>
}

function DiscountModal({ line, limit, onSet, onClose }: { line: CartLine; limit: number; onSet: (v: number) => void; onClose: () => void }) {
  const [v, setV] = useState(String(line.discountPercent || ''))
  const n = parseFloat(v.replace(',', '.')) || 0
  return <Modal title={`Descuento: ${line.product.name}`} onClose={onClose}
    footer={<><button onClick={() => onSet(0)}>Quitar descuento</button><button className="primary" disabled={n < 0 || n > 100} onClick={() => onSet(n)}>Aplicar</button></>}>
    <label>Porcentaje de descuento<input value={v} onChange={e => setV(e.target.value)} autoFocus inputMode="decimal" onKeyDown={e => { if (e.key === 'Enter' && n >= 0 && n <= 100) onSet(n) }} /></label>
    <small className="muted">Su tope sin autorización es {limit}%. Por encima se pedirá el PIN de un supervisor al cobrar.</small>
  </Modal>
}

function CustomerPicker({ onPick, onClose }: { onPick: (c: Customer | null) => void; onClose: () => void }) {
  const [q, setQ] = useState(''); const [list, setList] = useState<Customer[]>([])
  useEffect(() => { const t = setTimeout(() => api.get<Customer[]>(`/api/customers?q=${encodeURIComponent(q)}`).then(setList).catch(() => {}), 150); return () => clearTimeout(t) }, [q])
  return <Modal title="Cliente" onClose={onClose} footer={<button onClick={() => onPick(null)}>Venta sin cliente</button>}>
    <input value={q} onChange={e => setQ(e.target.value)} placeholder="Nombre, RNC o teléfono" autoFocus />
    <div className="list">{list.filter(c => c.active).map(c => <button key={c.id} className="row" onClick={() => onPick(c)}>
      <span>{c.name}</span><small>Disponible {money(c.available)} · Debe {money(c.balance)}</small></button>)}</div>
  </Modal>
}

function PayModal({ total, customer, registerId, cart, quoteId, onClose, onDone, onPending }: {
  total: number; customer: Customer | null; registerId: string; cart: CartLine[]; quoteId: string | null
  onClose: () => void; onDone: (s: Sale) => void; onPending: (p: PendingOp | null) => void
}) {
  const { serverUp } = useSession(); const { push } = useToast()
  const [cash, setCash] = useState(''); const [card, setCard] = useState(''); const [cardRef, setCardRef] = useState('')
  const [tr, setTr] = useState(''); const [trRef, setTrRef] = useState(''); const [credit, setCredit] = useState('')
  const [busy, setBusy] = useState(false); const [auth, setAuth] = useState<Auth | null>(null); const [askAuth, setAskAuth] = useState(false)
  const [status, setStatus] = useState<string | null>(null)
  const keyRef = useRef(uuid())   // misma clave para todos los reintentos de ESTA venta
  const n = (s: string) => Math.round((parseFloat(s.replace(',', '.')) || 0) * 100) / 100
  const nonCash = n(card) + n(tr) + n(credit)
  const cashDue = Math.round((total - nonCash) * 100) / 100
  const tendered = n(cash)
  const change = tendered > cashDue && cashDue > 0 ? Math.round((tendered - cashDue) * 100) / 100 : 0
  const missing = cashDue > 0 ? Math.max(0, Math.round((cashDue - tendered) * 100) / 100) : 0
  const valid = nonCash <= total && missing === 0 && !(cashDue === 0 && tendered > 0) && (n(credit) === 0 || !!customer) && !busy && serverUp
  const creditOver = customer && n(credit) > customer.available

  const body = (a: Auth | null) => ({
    operationKey: keyRef.current, registerId, customerId: customer?.id ?? null, quoteId,
    lines: cart.map(l => ({ productId: l.product.id, quantity: l.qty, discountPercent: l.discountPercent || null })),
    payments: [
      ...(cashDue > 0 ? [{ method: 'Cash', amount: cashDue, tendered }] : []),
      ...(n(card) > 0 ? [{ method: 'Card', amount: n(card), reference: cardRef || null }] : []),
      ...(n(tr) > 0 ? [{ method: 'Transfer', amount: n(tr), reference: trRef || null }] : []),
      ...(n(credit) > 0 ? [{ method: 'Credit', amount: n(credit) }] : []),
    ],
    ...(a ?? {}),
  })

  const confirm = async (a: Auth | null = auth) => {
    if (!valid) return
    setBusy(true); setStatus('Registrando venta…')
    const payload = body(a)
    onPending({ key: keyRef.current, registerId, body: payload, at: Date.now() })
    try {
      const r = await api.post<{ sale: Sale; duplicate: boolean }>('/api/sales', payload)
      if (r.duplicate) push('info', 'Esta venta ya estaba registrada; no se cobró de nuevo.')
      onDone(r.sale)
    } catch (e) {
      if (e instanceof NetworkError) {
        // No sabemos si el servidor alcanzó a guardar: se consulta antes de permitir otro intento.
        setStatus('Conexión interrumpida. Verificando si la venta se registró…')
        for (let i = 0; i < 6; i++) {
          await new Promise(r => setTimeout(r, 1500))
          try {
            const r = await api.get<{ exists: boolean; sale?: Sale }>(`/api/sales/by-operation/${keyRef.current}`)
            if (r.exists && r.sale) { push('ok', 'La venta sí se había registrado.'); onDone(r.sale); return }
            setStatus(null); setBusy(false); push('info', 'El servidor confirma que la venta NO se registró. Puede reintentar.'); return
          } catch { /* sigue sin red */ }
        }
        setStatus('Sin respuesta del servidor. NO cobre de nuevo: la venta queda "sin confirmar" y se consultará al volver la conexión.')
        setBusy(false)
        return
      }
      setStatus(null); setBusy(false)
      if (e instanceof ApiError && e.code === 'authorization_required') { setAskAuth(true); writeClear(); return }
      if (e instanceof ApiError) { writeClear(); push('err', e.message); return }
      push('err', errText(e))
    }
  }
  const writeClear = () => onPending(null)

  const fill = (setter: (s: string) => void, v: number) => setter(v > 0 ? v.toFixed(2) : '')
  return <Modal title={`Cobrar ${money(total)}`} onClose={busy ? undefined : onClose} wide
    footer={<><button onClick={onClose} disabled={busy}>Cancelar</button><button className="primary big" disabled={!valid || !!creditOver} onClick={() => confirm()}>{busy ? 'Procesando…' : 'Confirmar cobro (Enter)'}</button></>}>
    <div onKeyDown={e => { if (e.key === 'Enter' && valid && !creditOver) confirm() }}>
      <div className="payrow"><label>Efectivo recibido<input value={cash} onChange={e => setCash(e.target.value)} inputMode="decimal" autoFocus placeholder="0.00" /></label>
        <button onClick={() => fill(setCash, cashDue)}>Exacto</button>
        {[100, 200, 500, 1000, 2000].map(v => <button key={v} onClick={() => setCash(String(v))}>{v}</button>)}</div>
      <div className="payrow"><label>Tarjeta<input value={card} onChange={e => setCard(e.target.value)} inputMode="decimal" placeholder="0.00" /></label>
        <label>Ref. (últimos 4)<input value={cardRef} onChange={e => setCardRef(e.target.value)} maxLength={20} /></label>
        <button onClick={() => fill(setCard, Math.max(0, total - n(tr) - n(credit) - tendered))}>Resto</button></div>
      <small className="muted">“Tarjeta” solo REGISTRA que se cobró con el terminal bancario externo. Este sistema no procesa cargos.</small>
      <div className="payrow"><label>Transferencia<input value={tr} onChange={e => setTr(e.target.value)} inputMode="decimal" placeholder="0.00" /></label>
        <label>Ref. / # transferencia<input value={trRef} onChange={e => setTrRef(e.target.value)} maxLength={40} /></label></div>
      <div className="payrow"><label>Crédito {customer ? `(${customer.name}, disp. ${money(customer.available)})` : '(elija un cliente con F4)'}
        <input value={credit} onChange={e => setCredit(e.target.value)} inputMode="decimal" placeholder="0.00" disabled={!customer} /></label>
        {customer && <button onClick={() => fill(setCredit, Math.max(0, Math.min(customer.available, total - n(card) - n(tr) - tendered)))}>Máx.</button>}</div>
      {creditOver && <div className="errbox">El crédito supera el disponible del cliente.</div>}
      <div className="paysum">
        <div><span>Total</span><b>{money(total)}</b></div>
        <div><span>Efectivo a cobrar</span><b>{money(Math.max(0, cashDue))}</b></div>
        {missing > 0 ? <div className="bad"><span>Falta</span><b>{money(missing)}</b></div> : <div className="good"><span>Cambio</span><b>{money(change)}</b></div>}
      </div>
      {status && <div className="statusbar" role="status"><span className="spinner small" /> {status}</div>}
    </div>
    {askAuth && <AuthModal reason="El descuento supera su límite. Un supervisor debe autorizarlo." onClose={() => setAskAuth(false)}
      onOk={a => { setAuth(a); setAskAuth(false); confirm(a) }} />}
  </Modal>
}

function DoneModal({ sale, onNew }: { sale: Sale; onNew: () => void }) {
  const { push } = useToast()
  const [printState, setPrintState] = useState<'idle' | 'busy' | 'sent' | 'fail'>('idle')
  const print = useCallback(async (reprint: boolean) => {
    setPrintState('busy')
    const r = await printTicket(sale.id, reprint)
    setPrintState(r.ok ? 'sent' : 'fail')
    if (!r.ok) push('err', `La venta ${sale.number} está registrada, pero el ticket no se imprimió: ${r.error}. Puede reintentar.`)
  }, [sale, push])
  useEffect(() => { print(false) }, []) // eslint-disable-line
  useEffect(() => {
    const h = (e: KeyboardEvent) => { if (e.key === 'F10' || (e.key === 'Enter' && printState !== 'busy' && (e.target as HTMLElement).tagName !== 'BUTTON')) onNew() }
    window.addEventListener('keydown', h); return () => window.removeEventListener('keydown', h)
  }, [onNew, printState])
  return <Modal title="Venta registrada" footer={<><button onClick={() => print(true)} disabled={printState === 'busy'}>🖨 Reimprimir ticket</button><button className="primary big" onClick={onNew}>Nueva venta (Enter)</button></>}>
    <div className="done">
      <div className="check">✓</div>
      <h2>{sale.number}</h2>
      <div className="big-total">{money(sale.total)}</div>
      {sale.changeGiven > 0 && <div className="change">Cambio: <b>{money(sale.changeGiven)}</b></div>}
      <div className="muted">{sale.payments.map(p => `${methodName(p.method)} ${money(p.amount)}`).join(' · ')}</div>
      <div className={`printstate ${printState}`}>
        {printState === 'busy' && 'Preparando ticket…'}
        {printState === 'sent' && 'Ticket enviado al diálogo de impresión (el navegador no confirma que la impresora haya impreso).'}
        {printState === 'fail' && 'El ticket no se pudo imprimir. La venta ya está registrada; use “Reimprimir”.'}
      </div>
    </div>
  </Modal>
}
