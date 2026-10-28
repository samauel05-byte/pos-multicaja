# Estado del proyecto

**Madurez actual: PROTOTIPO funcional.** Fecha de verificación: 2026-10-28 (entorno Linux, PostgreSQL 16.15, .NET 8.0.427, Node 22, Chromium/Playwright).

| Nivel | Significado | ¿Alcanzado? |
|---|---|---|
| **Prototipo** | El recorrido completo funciona con persistencia real y pruebas automáticas; datos ficticios | **Sí** |
| **Piloto** | Uso real, limitado y vigilado, en un negocio, con respaldo manual paralelo | **No.** Falta lo de la sección “Antes de un piloto” |
| **Producción** | Operación diaria confiable, con soporte y cumplimiento | **No** |

## Terminado y verificado (35 pruebas automáticas + verificación en navegador)
Las pruebas son de integración: API real + PostgreSQL real + migraciones reales. Ejecutarlas: `scripts/test.sh` (35/35 pasan).

| # | Módulo | Qué funciona | Verificación |
|---|---|---|---|
| 1 | Usuarios y permisos | Admin / Supervisor / Cajero; permisos por endpoint en el servidor; PIN de supervisor; bloqueo por intentos; auditoría | `PermissionsTests` (6) |
| 2 | Productos | Alta/edición, código de barras, SKU, categoría, unidad (con/sin decimales), costo, precio, impuesto configurable, stock mínimo, activo/inactivo, existencia inicial; el costo se oculta al cajero | `PermissionsTests`, `SalesFlowTests` |
| 3 | Ventas | Búsqueda por código/nombre/SKU, `3*código`, carrito, cantidades, descuento por línea con autorización, efectivo/tarjeta*/transferencia/crédito y combinados, cambio | `SalesFlowTests`, `ConcurrencyAndIdempotencyTests` + navegador |
| 4 | Tickets | Texto 32/42/48 col. y ESC/POS generados en servidor; reimpresión como COPIA sin duplicar venta; registro de fallos de impresión | `PurchasesTicketsTests` (contenido). **Impresora real: no probada** |
| 5 | Caja | Apertura con fondo, entradas/salidas con motivo, arqueo por denominaciones, cierre, diferencia esperado vs. contado, nota obligatoria sobre tolerancia, cuadre separado por medio | `ReturnsAndShiftTests` + navegador |
| 6 | Inventario | Existencias por ubicación, kardex, ajustes con justificación, sin stock negativo (configurable), bajo mínimo | `PurchasesTicketsTests`, `TransactionTests` |
| 7 | Clientes y crédito | Límite, saldo, vencimiento por días de crédito, abonos FIFO (efectivo entra a la caja), estado de cuenta, vencidas en reporte | `SalesFlowTests` |
| 8 | Compras y proveedores | Recepción (actualiza existencias y costo), cuenta por pagar, pagos, idempotencia | `PurchasesTicketsTests` |
| 9 | Cotizaciones | Crear, vigencia, cancelar, convertir en venta (se cobra en la pantalla de Venta) una sola vez | `SalesFlowTests` |
| 10 | Devoluciones y anulaciones | Parcial/total, motivo obligatorio, autorización, reingreso opcional, efectivo y crédito compensados, venta conservada | `ReturnsAndShiftTests` (4) |
| 11 | Reportes | Ventas por fecha/caja/usuario/medio (+devoluciones/anulaciones), cierres de caja, inventario bajo, por cobrar, por pagar; CSV e impresión | `PurchasesTicketsTests` (parcial), navegador. **Reporte de ventas: sin prueba numérica dedicada** |
| 12 | Respaldo y restauración | Automático (programado) y manual, cifrado AES-256-GCM, verificación del volcado, SHA-256, retención; restauración por CLI/script en base nueva o reemplazo | `BackupRestoreTests` (3), `AutoBackupTests`, restauración CLI manual |
| — | Consistencia | Transacción única, clave de operación única, última unidad entre 2 cajas (25 rondas), cierre vs. venta simultánea (12 rondas), un turno por caja | `ConcurrencyAndIdempotencyTests`, `TransactionTests` |
| — | UI | Pantalla de venta orientada a cajero, atajos (F2/F4/F9/F10/Supr/↑↓), estados de carga/error/confirmación, banner y bloqueo con servidor caído | Playwright: venta completa, bloqueo de cobro, cierre |
| — | HTTPS local | Kestrel con `.pfx`, CA propia, SAN | Verificado con `curl`/`openssl` en Linux |
| — | Empaquetado Windows | `dotnet publish -r win-x64 --self-contained` compila (347 archivos con la interfaz) | Solo compilación |

\* “Tarjeta” **solo registra** el medio de pago; no hay integración bancaria ni se simula una.

## Pendiente (no implementado)
- **Módulo fiscal dominicano**: sin comprobantes fiscales/e-CF/NCF, sin firma ni envío a la DGII. Los impuestos son tasas configurables de ejemplo marcadas “por validar”. Tickets = “DOCUMENTO NO FISCAL”. **Prohibido emitir comprobantes reales hasta validarlo.**
- Impresión ESC/POS directa (agente local) y validación con impresoras reales.
- Integraciones comerciales (WooCommerce/Shopify) y consumidor de la bandeja `OutboxEvent`.
- Interfaz de varias sucursales y traspasos (el modelo de datos ya lo contempla).
- Precios de mayoreo/promociones, importación/exportación masiva de productos, PDF/Excel nativos, impresión del cierre en formato ticket.
- Permisos por caja, reabrir turnos, edición de ventas, cambio de contraseña por el propio usuario, recuperación de contraseña, cierre de sesión revocable en el servidor.
- Venta autónoma sin servidor con sincronización posterior (fuera de la primera versión por diseño).
- Interfaz táctil/móvil (el diseño es de escritorio; no validado en tablet).
- Pruebas de interfaz automatizadas en CI: solo existe un guion Playwright (`web/e2e/venta.e2e.cjs`, requiere app en marcha con datos demo y credenciales cajero1); no hace aserciones finas ni se ejecuta con `scripts/test.sh`.

## NO verificado (no lo dé por hecho)
- **Ejecución en Windows** (servicio, PowerShell, certificados de Windows, firewall): los scripts `.ps1` no se ejecutaron nunca.
- **Hardware**: impresoras térmicas, lector de códigos de barras USB (en teoría emula teclado + Enter; no se probó uno real), gaveta de dinero, pantallas.
- **Compatibilidad de navegadores** distinta de Chromium; Safari/Firefox no probados.
- **Rendimiento y carga** (catálogos de miles de productos, muchas cajas), recuperación tras corte eléctrico durante un cobro (la consistencia depende de PostgreSQL; no se simuló un corte real).
- **Seguridad**: sin auditoría ni pruebas de penetración; sin cumplimiento de ninguna norma.
- **Cumplimiento fiscal y legal**: ninguno declarado.
- Carrera “cierre vs. venta” probada por repetición (12 rondas), no por mutación; la prueba de la última unidad sí se validó por mutación.

## Antes de un piloto (mínimo)
1. Prueba completa de instalación en una máquina Windows de ensayo (servicio, HTTPS, cajas reales).
2. Validar impresoras y lector de códigos reales (lista en docs/IMPRESORAS.md).
3. Definir con contador/DGII el tratamiento fiscal; mientras tanto operar en paralelo con el sistema fiscal vigente.
4. Importar el catálogo real y conciliar existencias iniciales; capacitar cajeros y supervisores.
5. Respaldos copiados fuera del servidor y una **restauración de verificación** programada.
6. Cambiar todas las credenciales demo (o no cargar `Seed__Demo`), activar BitLocker, revisar roles.
7. Plan de contingencia en papel para cuando el servidor esté caído (los cobros se detienen por diseño).

## Datos de demostración
Empresa **ficticia** (“Comercial Demo, S.R.L.”), 22 productos con códigos de barras de ejemplo, 3 clientes y 2 proveedores ficticios, moneda RD$. Solo con `Seed__Demo=true`. Contraseñas conocidas (ver README): no usar fuera de pruebas.

## Cómo se probó (resumen reproducible)
```bash
scripts/test.sh                        # 35 pruebas, ~25 s
scripts/run-dev.sh                     # app en http://127.0.0.1:5080 con datos demo
```
Casos pedidos: dos cajas con la última unidad ✔ · reenvío del mismo cobro ✔ · pago combinado y crédito ✔ · devoluciones con efecto en inventario y caja ✔ · restricciones de permisos ✔ · cierre de caja y diferencias ✔ · restauración de respaldo ✔.
