import { Pending } from '../ui'

export default function Integrations() {
  const items: [string, string][] = [
    ['Facturación electrónica / comprobantes fiscales (DGII)', 'Sin implementar. Requiere validar normativa y proveedor autorizado antes de emitir comprobantes reales.'],
    ['Impresión térmica directa (ESC/POS por USB/red/serie)', 'El servidor genera bytes ESC/POS y el navegador imprime por el diálogo del sistema. La impresión directa a la impresora NO está validada con hardware real.'],
    ['Procesamiento de pagos con tarjeta', 'Fuera de alcance: “Tarjeta” solo registra un cobro hecho en un terminal bancario externo. No hay integración con ningún banco/adquirente.'],
    ['Tienda en línea (WooCommerce / Shopify)', 'Sin implementar. Diseño previsto: sincronizar catálogo y existencias a través de la API, con claves propias del negocio.'],
    ['Múltiples sucursales', 'El modelo de datos ya separa empresa → sucursal → ubicación → caja. Falta la interfaz y el traspaso entre sucursales.'],
    ['Venta autónoma sin servidor (offline) con sincronización posterior', 'Fuera de la primera versión por decisión de diseño: si el servidor no responde, se detienen los cobros.'],
    ['Venta móvil desde celular/tablet', 'La interfaz es web y se abre desde cualquier navegador de la LAN, pero el diseño táctil/móvil no está validado.'],
  ]
  return <div className="page"><h2>Integraciones y alcance pendiente</h2>
    <p className="muted">Estas funciones NO están terminadas. Se listan aquí para que nadie las dé por hechas.</p>
    {items.map(([t, d]) => <div className="card" key={t}><Pending>{t}</Pending><p>{d}</p></div>)}
  </div>
}
