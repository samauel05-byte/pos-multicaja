import { api } from './api'
import type { Ticket } from './types'

/**
 * Impresión de tickets. El ticket SIEMPRE se genera en el servidor; la venta ya está confirmada antes de imprimir.
 * Vía navegador (diálogo de impresión del sistema, impresora térmica instalada como impresora de Windows):
 * el navegador no informa si la impresora realmente imprimió, así que "enviado" ≠ "impreso".
 */
export async function printTicket(saleId: string, reprint: boolean, columns = 42): Promise<{ ok: boolean; error?: string; ticket?: Ticket }> {
  let t: Ticket
  try { t = await api.post<Ticket>(`/api/sales/${saleId}/ticket?reprint=${reprint}&columns=${columns}`) }
  catch (e) { return { ok: false, error: (e as Error).message } }
  try {
    await printText(t.text, columns)
    await api.post(`/api/print-jobs/${t.printJobId}/result`, { ok: true })
    return { ok: true, ticket: t }
  } catch (e) {
    const msg = (e as Error).message
    try { await api.post(`/api/print-jobs/${t.printJobId}/result`, { ok: false, error: msg }) } catch { /* el registro del fallo es secundario */ }
    return { ok: false, error: msg, ticket: t }
  }
}

export function printText(text: string, columns: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const f = document.createElement('iframe')
    f.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0'
    document.body.appendChild(f)
    const doc = f.contentDocument
    if (!doc || !f.contentWindow) { f.remove(); return reject(new Error('No se pudo preparar la impresión.')) }
    const mm = columns <= 32 ? 58 : 80
    doc.open()
    doc.write(`<html><head><meta charset="utf-8"><style>
      @page { size: ${mm}mm auto; margin: 2mm }
      body { margin:0; font: 12px/1.25 "Courier New", monospace; width: ${mm - 4}mm }
      pre { margin:0; white-space: pre-wrap }
    </style></head><body><pre></pre></body></html>`)
    doc.close()
    doc.querySelector('pre')!.textContent = text
    setTimeout(() => {
      try { f.contentWindow!.focus(); f.contentWindow!.print(); resolve() } catch (e) { reject(e as Error) }
      setTimeout(() => f.remove(), 2000)
    }, 150)
  })
}
