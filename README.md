# Sistema de Punto de Venta multicaja (prototipo)

Sistema POS en español para un negocio con **varias cajas conectadas a un servidor local**. Ventas, caja e inventario funcionan **sin Internet** mientras el servidor y la red local estén disponibles. Código e interfaz propios, sin marca.

> **Estado: PROTOTIPO funcional.** No es un piloto ni producción. Léase [docs/ESTADO.md](docs/ESTADO.md) antes de usarlo con dinero real. **No emite comprobantes fiscales.**

## Qué incluye
- **API** ASP.NET Core 8 + **PostgreSQL** (migraciones EF Core), JWT, permisos en el servidor, auditoría.
- **Interfaz web** React + TypeScript, servida por la propia API: las cajas solo abren `https://servidor:5443` en un navegador (no se instala nada en cada caja).
- Módulos: usuarios/permisos, productos, ventas (efectivo, tarjeta*, transferencia, crédito, pagos combinados), tickets, turnos de caja y arqueo, inventario con kardex, clientes y crédito, compras y proveedores, cotizaciones, devoluciones/anulaciones, reportes, respaldos cifrados.
- \* “Tarjeta” solo **registra** el cobro hecho en un terminal bancario externo; no procesa cargos.
- Datos de demostración **ficticios**, moneda configurable (demo: RD$).

## Arranque rápido (desarrollo, Linux/macOS)
Requisitos: .NET 8 SDK, Node 22, PostgreSQL 16 (con `pg_dump`/`pg_restore`).
```bash
scripts/run-dev.sh        # compila la interfaz, migra, carga datos demo y sirve en http://127.0.0.1:5080
scripts/test.sh           # 35 pruebas de integración contra PostgreSQL real
```
Usuarios demo (solo con `Seed__Demo=true`; **no usar en producción**): `admin / Admin1234!` (PIN 0000), `supervisor / Demo1234!` (PIN 1234), `cajero1` y `cajero2` / `Demo1234!`.

## Documentación
| Documento | Contenido |
|---|---|
| [docs/ESTADO.md](docs/ESTADO.md) | **Qué está terminado, qué está pendiente, qué se probó y qué NO se verificó** |
| [docs/INSTALACION-WINDOWS-LAN.md](docs/INSTALACION-WINDOWS-LAN.md) | Instalar el servidor en Windows, conectar cajas por LAN, HTTPS local, respaldos y restauración |
| [docs/IMPRESORAS.md](docs/IMPRESORAS.md) | Integración de impresoras térmicas y lista de validación con hardware |
| [docs/DESPLIEGUE-NUBE.md](docs/DESPLIEGUE-NUBE.md) | Probarlo en la nube: API en Render + interfaz en Vercel (sin verificar en vivo) |
| [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md) | Decisiones, reglas de consistencia y cómo se implementan, modelo de datos, API |
| [docs/REFERENCIA-VSG.md](docs/REFERENCIA-VSG.md) | Qué funciones documenta públicamente el producto de referencia y cuáles son propuestas nuestras |
