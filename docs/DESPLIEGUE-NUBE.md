# Despliegue en la nube: API en Render + interfaz en Vercel

> **Verificación:** se comprobó localmente que la API arranca con `DATABASE_URL`, el puerto `PORT` y credenciales demo configurables. El `Dockerfile`, `render.yaml` y `vercel.json` **no se han ejecutado** (no había Docker ni cuentas en el entorno de desarrollo): pueden requerir ajustes al primer despliegue.

## Qué corre dónde
```
Navegador ──► Vercel (interfaz estática) ──/api/*──► Render (contenedor .NET) ──► PostgreSQL de Supabase
```
Vercel no ejecuta .NET; solo sirve la interfaz y **reenvía `/api/*`** a Render (así el navegador ve un solo origen). La API de Render **también sirve la misma interfaz**, así que la URL de Render ya funciona sola; Vercel es opcional.

## 1. Render (servidor + base de datos)
1. https://render.com → *New → Blueprint* → conecte el repositorio `samauel05-byte/pos-multicaja` (rama `main`). Render lee `render.yaml` y crea `pos-api` (Docker). **No crea base de datos:** Render pedirá el valor de `DATABASE_URL`; pegue la cadena de conexión de Supabase (*Project Settings → Database → Connection string → Session pooler*, puerto 5432, con la contraseña real). Las migraciones y datos iniciales se aplican solos al arrancar.
2. Espere el primer *build* (varios minutos). Al terminar, la URL será `https://pos-api-XXXX.onrender.com`.
3. En *pos-api → Environment* lea las contraseñas generadas: `Seed__AdminPassword` (usuario `admin`), `Seed__DemoPassword` (usuarios `supervisor`, `cajero1`, `cajero2`), `Seed__DemoPin` (PIN de supervisor). Si la base ya existía antes de cambiarlas, no se actualizan.
4. Pruebe `https://pos-api-XXXX.onrender.com/api/health` y luego abra la URL raíz.

**Límites del plan gratuito:** el servicio se duerme tras ~15 min sin tráfico (primera petición lenta); no hay disco persistente, por eso los respaldos automáticos van desactivados. Para uso real: plan de pago + disco + `Backup__AutoEnabled=true` + copiar los `.posbak` fuera.

**Importante:** `Seed__Demo=true` carga datos ficticios. En un negocio real póngalo en `false` *antes del primer arranque* y use datos reales. Una URL pública con datos demo es solo para pruebas: no registre información real.

## 2. Vercel (interfaz)
1. Edite `web/vercel.json`: reemplace `REEMPLAZAR-URL-DE-RENDER.onrender.com` por el host real de Render.
2. Vercel → *Add New Project* → importe el repositorio → **Root Directory: `web`** (Framework: Vite; ya definido en `vercel.json`).
3. Despliegue. Entre con las credenciales del paso 1.3.

## 3. Seguridad al exponerlo a Internet
- Cambie las contraseñas generadas por las suyas y quite `Seed__*Password` después del primer arranque.
- El bloqueo por intentos es por usuario, no por IP: en Internet conviene un WAF/límite de peticiones (Vercel Firewall / Cloudflare).
- No se ha auditado la seguridad. No procese datos reales de clientes sin revisarla.
- Internet **no** es el modelo previsto del sistema: el diseño es servidor local en la LAN que funciona sin Internet. La nube sirve para pruebas y demostraciones.
