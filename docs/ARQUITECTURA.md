# Arquitectura y decisiones

## Pila (no existía un proyecto previo que impusiera otra)
React 18 + TypeScript (Vite) · ASP.NET Core 8 (minimal APIs) · PostgreSQL 16 · EF Core 8 con migraciones · JWT · xUnit con `WebApplicationFactory` contra PostgreSQL real.

La API **sirve la interfaz compilada** (`wwwroot`): un solo proceso, un solo origen (sin CORS), una URL para las cajas.

## Estructura
```
server/src/Pos.Api   Domain/ (entidades) · Data/ (AppDb, Seeder, Migrations) · Services/ (reglas de negocio) · Endpoints/ (HTTP)
server/tests/Pos.Tests   35 pruebas de integración
web/                 SPA React (compila a server/src/Pos.Api/wwwroot)
scripts/ docs/
```

## Reglas de consistencia → cómo se garantizan
| Regla | Implementación | Prueba |
|---|---|---|
| Venta, líneas, pagos, caja e inventario en una transacción | `SaleService.CreateAsync`: una transacción PostgreSQL; cualquier excepción revierte todo | `Si_una_linea_falla_no_queda_nada_a_medias` |
| Sin ventas duplicadas | `Sales.OperationKey` **único**; pre-consulta + captura de violación única → devuelve la venta existente (`duplicate:true`). Igual en devoluciones, abonos, compras y pagos a proveedores | `Reenvio_del_mismo_cobro…` (incluye ráfaga de 8 peticiones concurrentes) |
| Últimas unidades con ventas simultáneas | `UPDATE "StockLevels" SET q=q-@n WHERE q>=@n RETURNING q`: atómico y serializado por fila; no hay leer-modificar-escribir en la app. `AllowNegativeStock` configurable | `Dos_cajas_vendiendo_la_ultima_unidad…` (25 rondas). **Verificado por mutación**: sin la condición `q>=@n` la prueba falla |
| Cálculo en el servidor | `Pricing.Line`; el cliente solo envía producto, cantidad y % de descuento (el precio enviado se ignora). `/sales/preview` da los totales a la UI | `Precio_e_impuesto_los_calcula_el_servidor…`, `PricingTests` |
| Decimales y redondeo | `numeric(18,4)` / `decimal`; dinero a 2 decimales, **mitad se aleja de cero, por línea**; precio con impuesto incluido ⇒ base = total/(1+t) redondeada, impuesto = resto | `PricingTests` |
| Precios/impuestos históricos | `SaleLine` copia nombre, SKU, precio, costo, tasa, descuento e importes; `Sale` copia moneda y modo de impuesto | prueba de cambio de precio posterior |
| Efectivo, tarjeta, transferencia y crédito separados al cuadrar | `CashMovement` solo registra **efectivo** (venta neta de cambio, abonos, entradas/salidas, reembolsos); tarjeta/transferencia/crédito se suman desde `SalePayment` y reembolsos | `Cierre_de_caja_…`, `Pago_combinado_…` |
| Falla de impresión no cancela la venta | Impresión es otra llamada posterior con su `PrintJob`; la venta ya está confirmada | `Reimprimir_no_duplica_…` |
| Corte de red durante el cobro | La UI guarda la clave de operación; antes de repetir consulta `GET /sales/by-operation/{key}`; mismo `POST` es idempotente | `by-operation` en pruebas; flujo de UI verificado manualmente |
| Sin borrar ventas | No existe `DELETE`; anulación = documento `SaleReturn` + movimientos compensatorios; la venta queda `Voided`. FK `Restrict` | `Anulacion_conserva_la_venta…` |
| “Tarjeta” no es un cargo bancario | Solo `Method=Card` + referencia; la UI lo dice explícitamente | — |
| Servidor inaccesible ⇒ sin cobros | `/api/health` cada 4 s; banner rojo y botón **Cobrar deshabilitado** | verificado en navegador (Playwright) |
| Un turno abierto por caja | Índice único parcial `WHERE Status=1`; apertura concurrente → solo una gana | `Apertura_simultanea…` |
| Cierre vs. venta simultánea | Venta: `SELECT … FOR SHARE` del turno; cierre: `FOR UPDATE` | por diseño; sin prueba dedicada de carrera |

## Seguridad
Roles `Admin`, `Supervisor`, `Cashier` → permisos (`Perm`) comprobados **en cada endpoint** (`CurrentUser.Require`). La UI oculta opciones, pero no es la barrera. Descuento sobre el tope del cajero, devoluciones y anulaciones por cajero: **usuario + PIN de supervisor** validados en el servidor (fallos auditados en conexión aparte para que no los revierta la transacción). Auditoría: login, turnos, ventas, descuentos autorizados, reimpresiones, devoluciones/anulaciones, ajustes, cambios de usuarios/límites/configuración, respaldos.

## Modelo (resumen)
`Company → Branch → Location → Register` (preparado para varias sucursales; hoy 1 empresa/1 sucursal) · `AppUser` · `Product/Category/Unit/TaxRate` · `StockLevel(Product,Location)` + `InventoryMovement` (kardex) · `Shift` + `CashMovement` · `Sale/SaleLine/SalePayment` · `SaleReturn/ReturnLine/ReturnRefund` · `Customer` + `CustomerPayment(+Allocation)` · `Supplier/Purchase/SupplierPayment` · `Quote` · `PrintJob` · `AuditLog` · `DocumentCounter` (consecutivos atómicos) · `BackupRecord` · `OutboxEvent`.

## Decisiones y límites conocidos
- **Costo de producto = último costo de compra** (no promedio ponderado).
- **Consecutivos** (`T-000001`) vía `UPSERT … RETURNING` dentro de la transacción: no hay huecos por ventas fallidas, pero **serializa** las ventas (fila de contador). Suficiente para unas pocas cajas; a muy alto volumen se cambiaría por secuencias por caja.
- **Reembolsos por tarjeta/transferencia** solo se registran; el reverso en el terminal bancario es manual.
- Una **anulación** exige que la venta no tenga devoluciones previas; una venta con devoluciones parciales se completa con devoluciones.
- Crédito: no se puede exceder el límite (sin “sobregiro autorizado”). Abonos se aplican FIFO.
- `ShiftService.CloseAsync` toma el efectivo esperado de la suma de `CashMovement`; **no se ha implementado reabrir turnos**.
- Fechas de reportes: zona horaria del servidor (`TZ`).
- Pagos a proveedores no salen de la gaveta de caja.
- Traspasos entre ubicaciones, mayoreo, importación masiva, PDF/Excel nativos: pendientes.

## Preparación de integraciones (Etapa 4)
`OutboxEvent` se escribe **en la misma transacción** que cada venta/devolución/anulación (`sale.completed|returned|voided`) con un JSON estable. Un futuro adaptador (fiscal, tienda en línea) lo consumiría con reintentos e idempotencia; `GET /api/integrations/outbox` lo expone. **No hay consumidor implementado.**
