# Referencia funcional: lo documentado vs. lo propuesto

Fuentes consultadas el 2026-10-28: la página del manual y la página del producto “Sistema Punto de Venta VSG” (vanguardsoftware.tech). **No se tuvo ni se presupone acceso a su código**; este proyecto no copia texto, código, interfaz ni marca.

Importante: el **manual público solo contiene** requisitos y un resumen de 7 pasos (instalar servidor, configurar negocio, productos y caja, evaluar, conectar cajas, licencia anual, respaldo). El detalle de pantallas no está publicado; lo demás sale de la página del producto.

| Función | ¿Documentada por la referencia? | En este proyecto |
|---|---|---|
| PC servidor con base local + cajas cliente por LAN, rol servidor/cliente | Sí | Servidor único; las cajas son navegadores (**decisión propia**: sin instalador por caja) |
| Funcionar sin Internet (ventas, caja, inventario) | Sí | Sí |
| Ventas normales/rápidas, pago mixto, crédito | Sí | Sí |
| Apertura, movimientos, efectivo esperado y cierre por caja | Sí | Sí (+ arqueo por denominaciones, **propuesta**) |
| Productos, inventario, kardex | Sí | Sí |
| Compras, proveedores, cuentas por pagar | Sí | Sí |
| Clientes, crédito, abonos | Sí | Sí |
| Cotizaciones, descuentos, mayoreo | Sí (mayoreo según configuración) | Cotizaciones y descuentos con autorización. **Precio de mayoreo: pendiente** |
| Reportes con filtros, PDF/Excel | Sí | Reportes con CSV e impresión del navegador. **PDF/Excel nativos: pendientes** |
| Usuarios y permisos por usuario/caja | Sí (por usuario, caja y permisos) | 3 roles fijos; permisos por caja: **pendiente** |
| Respaldos protegidos y restauración | Sí | Sí, cifrado AES-256-GCM (**diseño propio**) |
| Licencia anual atada al hardware, prueba de 100 días | Sí (modelo comercial de ellos) | **No aplica / no implementado** |
| Venta móvil por navegador en la LAN con HTTPS | Sí | La interfaz web abre en cualquier navegador de la LAN; diseño móvil **sin validar** |
| 111 idiomas | Sí | Solo español |
| Asistente de IA, WooCommerce/Shopify | Sí (opcional) | **Pendiente** (hay bandeja de eventos de salida como preparación) |
| Facturación electrónica por adaptador externo | Sí (por país) | **Pendiente**; módulo fiscal dominicano por validar |
| Devoluciones/anulaciones con motivos y trazabilidad, doble cobro, última unidad, auditoría | No detallado públicamente | **Requisitos propios** del encargo, implementados y probados |
