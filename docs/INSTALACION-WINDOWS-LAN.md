# Instalación en Windows y conexión de cajas por LAN

> **Aviso de verificación.** El sistema se desarrolló y probó en **Linux**. Lo siguiente se verificó: compilación para `win-x64` (`dotnet publish -r win-x64 --self-contained` termina bien), la interfaz y la API, HTTPS con una CA propia (con `openssl`), respaldo y restauración con PostgreSQL 16. **No se ejecutó en Windows**: los scripts `.ps1` están escritos pero sin probar. Haga primero una prueba completa en una máquina de ensayo.

## Esquema
```
 [Caja 1: navegador] ─┐
 [Caja 2: navegador] ─┼─ LAN ─►  [PC SERVIDOR]  Pos.Api (servicio Windows, HTTPS :5443)
 [Caja N: navegador] ─┘                           └─► PostgreSQL 16 (solo localhost)
```
- Las cajas **no** acceden a la base de datos: solo hablan con la API por HTTPS. PostgreSQL escucha únicamente en `localhost` del servidor.
- Si el servidor o la red caen, las cajas muestran “Sin conexión con el servidor” y **detienen los cobros** (no hay venta autónoma).

## 1. Servidor
Requisitos: Windows 10/11 o Server 2019+, IP fija en la LAN (reserva en el router), disco con espacio para respaldos (idealmente otro disco).

### 1.1 PostgreSQL 16
Instale PostgreSQL 16 (instalador oficial) con las herramientas de línea de comandos. Deje `listen_addresses = 'localhost'`. Luego:
```powershell
& "C:\Program Files\PostgreSQL\16\bin\psql.exe" -U postgres -c "CREATE ROLE pos_app LOGIN PASSWORD 'CAMBIE-ESTA-CLAVE' CREATEDB;"
& "C:\Program Files\PostgreSQL\16\bin\psql.exe" -U postgres -c "CREATE DATABASE pos OWNER pos_app;"
```
(`CREATEDB` permite al comando de restauración crear bases de verificación.)

### 1.2 Aplicación
En una PC de desarrollo: `scripts\windows\publicar.ps1` (genera `dist\pos-servidor`). Copie la carpeta al servidor como `C:\POS\app`. Al arrancar, la aplicación **aplica las migraciones** y crea empresa, sucursal, 2 cajas, impuestos y el usuario `admin` si la base está vacía.

### 1.3 HTTPS local
Los navegadores exigen un certificado confiable. Opciones:
- **mkcert** (recomendado): `mkcert -install`, luego `mkcert -pkcs12 pos-servidor 192.168.1.10 localhost` → `.p12` (contraseña por defecto `changeit`).
- `scripts\windows\nuevo-certificado.ps1`: crea una CA local y un certificado de servidor con SAN (nombre + IP). Devuelve `servidor.pfx` y `POS-CA.cer`.

**Cada caja** debe confiar en la CA: copie `POS-CA.cer` y ejecute como administrador  
`Import-Certificate -FilePath .\POS-CA.cer -CertStoreLocation Cert:\LocalMachine\Root`.  
Acceda siempre con el **nombre o IP incluidos en el certificado** (SAN). Verificación realizada en Linux: con la CA instalada el cliente valida la cadena; sin ella la conexión se rechaza.

### 1.4 Servicio, firewall y configuración
Como administrador: `scripts\windows\instalar-servicio.ps1`. Crea el servicio `PosServidor` (arranque automático, reinicio ante fallos), abre el puerto 5443 solo en perfiles Privado/Dominio y guarda estas variables de entorno del servicio:

| Variable | Significado |
|---|---|
| `ConnectionStrings__Db` | Cadena de PostgreSQL (usuario `pos_app`) |
| `Jwt__Key` | Clave de firma de sesiones (≥ 32 caracteres, aleatoria). Si cambia, se cierran las sesiones |
| `Seed__AdminPassword` | Contraseña inicial de `admin` (solo se usa si la base está vacía; quítela luego) |
| `Backup__Passphrase` | Frase que cifra los respaldos. **Sin ella no se pueden restaurar** |
| `Backup__Directory` / `Backup__PgBinDir` | Carpeta de respaldos / carpeta `bin` de PostgreSQL |
| `Backup__IntervalHours`, `Backup__KeepLast` | Frecuencia (24 h) y retención de automáticos (14) |
| `Kestrel__Endpoints__Https__*` | URL, ruta del `.pfx` y su contraseña |
| `Seed__Demo` | **No** la active en producción (carga datos y contraseñas conocidas) |
| `TZ` | Zona horaria para los reportes por fecha |

Verifique en el servidor: `https://localhost:5443/api/health` → `{"status":"ok"}`.

## 2. Cajas
1. Instale la CA (1.3) y la impresora térmica (docs/IMPRESORAS.md).
2. Abra `https://pos-servidor:5443` (o la IP) en Chrome/Edge. Se puede crear un acceso directo o “instalar como aplicación”.
3. Inicie sesión y, en el menú lateral, elija **“Esta computadora es: Caja N”** (se guarda en ese navegador).
4. Abra turno en *Caja y turno* con el fondo inicial y comience a vender.

## 3. Primer uso
1. Entre como `admin`, cambie su contraseña y su PIN (Administración → Usuarios). Cree supervisores y cajeros (el PIN de supervisor autoriza descuentos altos, devoluciones y anulaciones).
2. Administración → Empresa y ajustes: nombre, RNC, moneda, topes de descuento, tolerancia de diferencia de caja.
3. Cargue productos (o impórtelos con SQL/`POST /api/products`; no hay importador masivo todavía).
4. Realice **una venta de prueba, un respaldo y una restauración de verificación** (sección 4) antes de operar.

## 4. Respaldos y recuperación
- Automáticos cada 24 h (primero 2 minutos tras arrancar) + manual desde Administración → Respaldos. Cada respaldo: `pg_dump` → verificación de legibilidad → cifrado AES-256-GCM → SHA-256 registrado → se podan los automáticos viejos.
- **Copie los `.posbak` fuera del servidor** (otro disco, otra PC, nube con su política). Un respaldo solo en el mismo disco no protege contra la falla del disco.
- **Guarde la frase de paso por separado.** Sin ella el respaldo es irrecuperable (probado: frase incorrecta, archivo alterado o truncado → se rechaza).

### Restauración (procedimiento probado en Linux)
**a) Verificación sin tocar producción** (hágala periódicamente):
```powershell
.\restaurar.ps1 -Archivo D:\POS-Respaldos\pos-AAAAMMDD-HHMMSS-auto.posbak -BaseDestino pos_verificacion
psql -U pos_app -d pos_verificacion -c "select count(*) from ""Sales"";"
```
**b) Recuperación real** (falla del servidor o corrupción):
1. `Stop-Service PosServidor`
2. `.\restaurar.ps1 -Archivo <archivo> -BaseDestino pos -Reemplazar` (elimina y recrea la base `pos`)
3. `Start-Service PosServidor`; entre, revise la última venta y el turno abierto.
4. Se pierden las operaciones posteriores al respaldo: anótelas a mano y regístrelas de nuevo.

Equivalente directo (Linux/Windows): `Pos.Api --restore <archivo.posbak> --target-db <base> [--replace]` con `Backup__Passphrase`, `ConnectionStrings__Db` y `Backup__PgBinDir` definidos.

## 5. Actualizar
Detenga el servicio, haga un respaldo, copie la nueva carpeta sobre `C:\POS\app` (conserve la configuración, que vive en las variables del servicio), inicie el servicio: las migraciones se aplican solas.

## 6. Seguridad: lo que está y lo que no
Está: contraseñas con PBKDF2-SHA256 (210 000 iteraciones), bloqueo de 5 min tras 5 intentos fallidos, permisos verificados en el servidor, auditoría de acciones sensibles, cabeceras de seguridad, HTTPS, PostgreSQL solo local, respaldos cifrados.  
**No está / no se auditó:** pruebas de penetración, límite de intentos por IP, cierre de sesión forzado del lado del servidor (el token JWT dura 12 h), 2FA, cifrado de la base en reposo (use BitLocker en el servidor), política de contraseñas más allá de 8 caracteres. No declare el sistema “seguro”: no se ha auditado.
