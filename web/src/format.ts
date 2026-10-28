let symbol = 'RD$'
export const setCurrency = (s: string) => { symbol = s }
export const money = (v: number | null | undefined) => `${symbol} ${(v ?? 0).toLocaleString('es-DO', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
export const num = (v: number | null | undefined, d = 2) => (v ?? 0).toLocaleString('es-DO', { minimumFractionDigits: 0, maximumFractionDigits: d })
export const dt = (s?: string | null) => s ? new Date(s).toLocaleString('es-DO', { dateStyle: 'short', timeStyle: 'short' }) : '—'
export const d = (s?: string | null) => s ? new Date(s).toLocaleDateString('es-DO') : '—'
export const methodName = (m: string) => ({ Cash: 'Efectivo', Card: 'Tarjeta', Transfer: 'Transferencia', Credit: 'Crédito' } as Record<string, string>)[m] ?? m
export const ymd = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
export function toCsv(rows: Record<string, unknown>[]): string {
  if (!rows.length) return ''
  const cols = Object.keys(rows[0])
  const esc = (v: unknown) => { const s = v == null ? '' : String(v); return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s }
  return [cols.join(','), ...rows.map(r => cols.map(c => esc(r[c])).join(','))].join('\n')
}
export function download(name: string, content: string, type = 'text/csv;charset=utf-8') {
  const url = URL.createObjectURL(new Blob(['﻿' + content], { type }))
  const a = document.createElement('a'); a.href = url; a.download = name; a.click(); URL.revokeObjectURL(url)
}
