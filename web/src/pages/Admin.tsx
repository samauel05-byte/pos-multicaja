import { useState } from 'react'
import { api } from '../api'
import { useSession } from '../session'
import { ErrorBox, Loading, Modal, Pending, errText, useLoad, useToast } from '../ui'
import { dt } from '../format'

interface U { id: string; username: string; fullName: string; role: string; active: boolean; hasPin: boolean }
interface BackupList { autoEnabled: boolean; intervalHours: number; keepLast: number; directory: string; passphraseConfigured: boolean; items: { id: string; fileName: string; sizeBytes: number; sha256: string; kind: string; success: boolean; error?: string; createdAt: string }[] }
const RN: Record<string, string> = { Admin: 'Administrador', Supervisor: 'Supervisor', Cashier: 'Cajero' }

export default function Admin() {
  const { can } = useSession()
  const [tab, setTab] = useState(can('users.manage') ? 'users' : 'audit')
  const tabs: [string, string, boolean][] = [['users', 'Usuarios', can('users.manage')], ['audit', 'Auditoría', can('audit.view')], ['backup', 'Respaldos', can('backup.manage')], ['settings', 'Empresa y ajustes', can('settings.manage')], ['fiscal', 'Fiscal (RD)', true]]
  return <div className="page"><h2>Administración</h2>
    <div className="tabs">{tabs.filter(t => t[2]).map(([k, l]) => <button key={k} className={tab === k ? 'active' : ''} onClick={() => setTab(k)}>{l}</button>)}</div>
    {tab === 'users' && <Users />}{tab === 'audit' && <Audit />}{tab === 'backup' && <Backups />}{tab === 'settings' && <Settings />}{tab === 'fiscal' && <Fiscal />}
  </div>
}

function Users() {
  const { push } = useToast(); const { data, error, loading, reload } = useLoad<U[]>(() => api.get('/api/users'), [])
  const [e, setE] = useState<any>(null)
  const save = async () => {
    try { const b = { username: e.username, fullName: e.fullName, role: e.role ?? 'Cashier', password: e.password || null, pin: e.pin || null, active: e.active ?? true }
      if (e.id) await api.put(`/api/users/${e.id}`, b); else await api.post('/api/users', b); setE(null); push('ok', 'Usuario guardado.'); reload() } catch (x) { push('err', errText(x)) }
  }
  return <>
    <div className="toolbar"><button className="primary" onClick={() => setE({ role: 'Cashier', active: true })}>＋ Nuevo usuario</button></div>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    <table className="t hover"><thead><tr><th>Usuario</th><th>Nombre</th><th>Rol</th><th>PIN</th><th>Estado</th></tr></thead><tbody>
      {data?.map(u => <tr key={u.id} onClick={() => setE(u)}><td>{u.username}</td><td>{u.fullName}</td><td>{RN[u.role]}</td><td>{u.hasPin ? 'Sí' : '—'}</td><td>{u.active ? 'Activo' : <span className="chip bad">Inactivo</span>}</td></tr>)}</tbody></table>
    {e && <Modal title={e.id ? 'Editar usuario' : 'Nuevo usuario'} onClose={() => setE(null)} footer={<><button onClick={() => setE(null)}>Cancelar</button><button className="primary" onClick={save}>Guardar</button></>}>
      <label>Usuario<input value={e.username ?? ''} disabled={!!e.id} onChange={x => setE({ ...e, username: x.target.value })} /></label>
      <label>Nombre completo<input value={e.fullName ?? ''} onChange={x => setE({ ...e, fullName: x.target.value })} /></label>
      <label>Rol<select value={e.role} onChange={x => setE({ ...e, role: x.target.value })}><option value="Cashier">Cajero</option><option value="Supervisor">Supervisor</option><option value="Admin">Administrador</option></select></label>
      <label>{e.id ? 'Nueva contraseña (vacío = no cambiar)' : 'Contraseña (mín. 8)'}<input type="password" value={e.password ?? ''} onChange={x => setE({ ...e, password: x.target.value })} autoComplete="new-password" /></label>
      <label>PIN de autorización (4+ dígitos, supervisores/admin){e.id ? ' — vacío = no cambiar' : ''}<input type="password" inputMode="numeric" value={e.pin ?? ''} onChange={x => setE({ ...e, pin: x.target.value })} autoComplete="new-password" /></label>
      <label className="check"><input type="checkbox" checked={e.active ?? true} onChange={x => setE({ ...e, active: x.target.checked })} /> Activo</label>
    </Modal>}
  </>
}

function Audit() {
  const [a, setA] = useState('')
  const { data, error, loading, reload } = useLoad<any[]>(() => api.get(`/api/audit?limit=300&action=${encodeURIComponent(a)}`), [])
  return <>
    <div className="toolbar"><label>Acción empieza con<input value={a} onChange={e => setA(e.target.value)} placeholder="sale, shift, authorization, login…" onKeyDown={e => { if (e.key === 'Enter') reload() }} /></label><button onClick={reload}>Filtrar</button></div>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    <table className="t"><thead><tr><th>Fecha</th><th>Usuario</th><th>Acción</th><th>Detalle</th><th>IP</th></tr></thead><tbody>
      {data?.map(r => <tr key={r.id}><td>{dt(r.at)}</td><td>{r.username}</td><td><code>{r.action}</code></td><td>{r.detail}</td><td>{r.ip}</td></tr>)}</tbody></table>
  </>
}

function Backups() {
  const { push } = useToast(); const { data, error, loading, reload } = useLoad<BackupList>(() => api.get('/api/backups'), [])
  const [busy, setBusy] = useState(false)
  return <>
    {loading && <Loading />}{error ? <ErrorBox error={error} retry={reload} /> : null}
    {data && <>
      {!data.passphraseConfigured && <div className="errbox">No hay frase de paso configurada (Backup__Passphrase). Los respaldos no pueden crearse.</div>}
      <div className="card"><p>Respaldo automático: <b>{data.autoEnabled ? `cada ${data.intervalHours} h` : 'desactivado'}</b> · se conservan los últimos {data.keepLast} automáticos · carpeta del servidor: <code>{data.directory}</code></p>
        <p className="muted">Los archivos .posbak están cifrados (AES-256-GCM). <b>Sin la frase de paso no se pueden recuperar.</b> Guarde una copia del archivo fuera de este equipo. La restauración se hace desde el servidor con el comando documentado (no desde esta pantalla), con el sistema detenido.</p>
        <button className="primary" disabled={busy || !data.passphraseConfigured} onClick={async () => { setBusy(true); try { await api.post('/api/backups'); push('ok', 'Respaldo creado y verificado.'); reload() } catch (e) { push('err', errText(e)) } finally { setBusy(false) } }}>{busy ? 'Creando respaldo…' : 'Crear respaldo ahora'}</button></div>
      <table className="t"><thead><tr><th>Fecha</th><th>Archivo</th><th>Tipo</th><th className="r">Tamaño</th><th>SHA-256</th><th>Resultado</th></tr></thead><tbody>
        {data.items.map(b => <tr key={b.id}><td>{dt(b.createdAt)}</td><td>{b.fileName}</td><td>{b.kind === 'auto' ? 'Automático' : 'Manual'}</td><td className="r">{(b.sizeBytes / 1024).toFixed(0)} KB</td><td><code>{b.sha256.slice(0, 12)}…</code></td><td>{b.success ? '✓ OK' : <span className="bad-t">Falló: {b.error}</span>}</td></tr>)}</tbody></table>
    </>}
  </>
}

function Settings() {
  const { boot, refreshBoot } = useSession(); const { push } = useToast()
  const [c, setC] = useState({ ...boot!.company })
  const set = (k: string, v: any) => setC({ ...c, [k]: v })
  return <div className="card">
    <div className="form2">
      <label>Nombre de la empresa<input value={c.name} onChange={e => set('name', e.target.value)} /></label>
      <label>RNC<input value={c.taxId ?? ''} onChange={e => set('taxId', e.target.value)} /></label>
      <label>Código de moneda<input value={c.currencyCode} onChange={e => set('currencyCode', e.target.value)} /></label>
      <label>Símbolo<input value={c.currencySymbol} onChange={e => set('currencySymbol', e.target.value)} /></label>
      <label>Descuento máx. del cajero (%)<input value={c.cashierMaxDiscountPercent} onChange={e => set('cashierMaxDiscountPercent', parseFloat(e.target.value) || 0)} /></label>
      <label>Descuento máx. del supervisor (%)<input value={c.supervisorMaxDiscountPercent} onChange={e => set('supervisorMaxDiscountPercent', parseFloat(e.target.value) || 0)} /></label>
      <label>Tolerancia de diferencia de caja (sin nota)<input value={c.cashDifferenceToleranceAmount} onChange={e => set('cashDifferenceToleranceAmount', parseFloat(e.target.value) || 0)} /></label>
      <label className="span2">Pie del ticket<input value={c.ticketFooter ?? ''} onChange={e => set('ticketFooter', e.target.value)} /></label>
      <label className="check"><input type="checkbox" checked={c.pricesIncludeTax} onChange={e => set('pricesIncludeTax', e.target.checked)} /> Los precios incluyen impuesto</label>
      <label className="check"><input type="checkbox" checked={c.allowNegativeStock} onChange={e => set('allowNegativeStock', e.target.checked)} /> Permitir vender sin existencia (stock negativo)</label>
    </div>
    <small className="muted">Cambiar “precios incluyen impuesto” afecta ventas futuras; las ya registradas conservan su cálculo.</small>
    <div className="actions"><button className="primary" onClick={async () => { try { await api.put('/api/settings/company', c); await refreshBoot(); push('ok', 'Configuración guardada.') } catch (e) { push('err', errText(e)) } }}>Guardar</button></div>
  </div>
}

function Fiscal() {
  return <div className="card">
    <Pending>Módulo fiscal dominicano (comprobantes fiscales / e-CF de la DGII).</Pending>
    <p>Este sistema <b>no emite comprobantes fiscales</b>. Los tickets impresos dicen “DOCUMENTO NO FISCAL”. Los impuestos son tasas configurables de ejemplo (p. ej. “ITBIS 18% (por validar)”) y <b>no están validados</b> contra la normativa vigente.</p>
    <p>Antes de emitir comprobantes reales hay que validar con la DGII y/o un proveedor autorizado: tipos de comprobante, secuencias (NCF/e-CF), firma digital, formatos, envío y contingencia. La estructura de datos deja espacio (impuesto por línea histórico, cliente con RNC), pero la integración está sin implementar.</p>
  </div>
}
