import { useState } from 'react'
import { NavLink, Navigate, Route, Routes } from 'react-router-dom'
import { useSession } from './session'
import { errText, useToast } from './ui'
import Pos from './pages/Pos'
import Shift from './pages/Shift'
import Products from './pages/Products'
import Inventory from './pages/Inventory'
import Customers from './pages/Customers'
import Purchases from './pages/Purchases'
import Quotes from './pages/Quotes'
import Sales from './pages/Sales'
import Reports from './pages/Reports'
import Admin from './pages/Admin'
import Integrations from './pages/Integrations'

function Login() {
  const { login } = useSession(); const { push } = useToast()
  const [u, setU] = useState(''); const [p, setP] = useState(''); const [busy, setBusy] = useState(false)
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setBusy(true)
    try { await login(u.trim(), p) } catch (err) { push('err', errText(err)) } finally { setBusy(false) }
  }
  return <div className="login-wrap">
    <form className="login-card" onSubmit={submit}>
      <h1>Punto de Venta</h1>
      <p className="muted">Inicie sesión para continuar</p>
      <label>Usuario<input value={u} onChange={e => setU(e.target.value)} autoFocus autoComplete="username" /></label>
      <label>Contraseña<input type="password" value={p} onChange={e => setP(e.target.value)} autoComplete="current-password" /></label>
      <button className="primary big" disabled={busy || !u || !p}>{busy ? 'Entrando…' : 'Entrar'}</button>
    </form>
  </div>
}

export default function App() {
  const { me, boot, ready, logout, can, serverUp, registerId, setRegisterId } = useSession()
  if (!ready) return <div className="login-wrap"><div className="loading"><span className="spinner" />Conectando con el servidor…</div></div>
  if (!me || !boot) return <><ServerBanner /><Login /></>

  const reg = boot.registers.find(r => r.id === registerId)
  const nav: [string, string, boolean][] = [
    ['/', 'Venta', true], ['/caja', 'Caja y turno', true], ['/ventas', 'Ventas / Devoluciones', true],
    ['/cotizaciones', 'Cotizaciones', true], ['/clientes', 'Clientes y crédito', true],
    ['/productos', 'Productos', can('products.manage')], ['/inventario', 'Inventario', can('inventory.view')],
    ['/compras', 'Compras y proveedores', can('purchases.manage')], ['/reportes', 'Reportes', can('reports.view')],
    ['/admin', 'Administración', can('users.manage') || can('audit.view')], ['/integraciones', 'Integraciones', true],
  ]
  return <div className="shell">
    <aside className="sidebar">
      <div className="brand">Punto de Venta<small>{boot.company.name}</small></div>
      <nav>{nav.filter(n => n[2]).map(([to, label]) => <NavLink key={to} to={to} end={to === '/'} className={({ isActive }) => isActive ? 'active' : ''}>{label}</NavLink>)}</nav>
      <div className="side-foot">
        <label className="reg">Esta computadora es
          <select value={registerId ?? ''} onChange={e => setRegisterId(e.target.value)}>
            <option value="">— elegir caja —</option>
            {boot.registers.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
        </label>
        <div className="who">{me.fullName}<small>{({ Admin: 'Administrador', Supervisor: 'Supervisor', Cashier: 'Cajero' } as const)[me.role]}</small></div>
        <button onClick={logout}>Cerrar sesión</button>
      </div>
    </aside>
    <main>
      <ServerBanner />
      {!reg && <div className="warnbar">Elija en el menú lateral a qué caja corresponde esta computadora.</div>}
      <Routes>
        <Route path="/" element={<Pos />} />
        <Route path="/caja" element={<Shift />} />
        <Route path="/ventas" element={<Sales />} />
        <Route path="/cotizaciones" element={<Quotes />} />
        <Route path="/clientes" element={<Customers />} />
        <Route path="/productos" element={<Products />} />
        <Route path="/inventario" element={<Inventory />} />
        <Route path="/compras" element={<Purchases />} />
        <Route path="/reportes" element={<Reports />} />
        <Route path="/admin" element={<Admin />} />
        <Route path="/integraciones" element={<Integrations />} />
        <Route path="*" element={<Navigate to="/" />} />
      </Routes>
    </main>
    {!serverUp && <div className="blocker-hint" />}
  </div>
}

export function ServerBanner() {
  const { serverUp } = useSession()
  if (serverUp) return null
  return <div className="downbar" role="alert">
    ⛔ Sin conexión con el servidor del negocio. Los cobros están DETENIDOS hasta que vuelva la conexión. Verifique la red local o que el servidor esté encendido.
  </div>
}
