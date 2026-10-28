# POS en Vercel y Supabase

El proyecto Vercel `samkill/pos-multicaja` construye la raíz del repositorio con `Dockerfile.vercel`, framework `container`, región `iad1`. La API .NET sirve también la interfaz React; no se utiliza el proxy de `web/vercel.json`.

La base de datos está en el proyecto Supabase `uvaotfqutfojokbnzgcs`. Las tablas de EF Core se crean en el esquema privado `pos`, usando el rol de conexión `pos_app` (sin privilegios de superusuario ni creación de roles o bases). El esquema no se concede a `anon` ni `authenticated`; el acceso de usuarios pasa por la API y sus permisos existentes.

## Configuración

Los secretos se guardan en las variables de entorno de Vercel:
- `ConnectionStrings__Db`: conexión Npgsql mediante Session pooler, SSL, `Search Path=pos` y máximo de 10 conexiones por instancia.
- `Jwt__Key`: clave persistente para firmar sesiones.
- `Seed__AdminPassword` y `Seed__AdminPin`: credenciales del administrador inicial.

No guardar estos valores en Git. Las credenciales iniciales solo se aplican cuando la base está vacía. Cambiar variables después no cambia la contraseña de un usuario existente.

El contenedor desactiva datos demo y respaldos programados locales. El almacenamiento del contenedor es efímero. Los respaldos persistentes requieren una solución externa. Las dos migraciones EF Core y la inicialización ya se aplicaron. En Vercel se configura `Database__MigrateOnStart=false` para evitar ejecutar migraciones en cada arranque de instancia. El historial está en `pos.__EFMigrationsHistory`: futuras migraciones deben usar explícitamente ese esquema y ejecutarse de forma controlada antes de publicar cambios que dependan de ellas. No activar las migraciones automáticas con el historial predeterminado de `public`, porque el arranque intentaría crear un historial que ya existe en `pos`.

## Verificación

`GET /api/health` debe devolver HTTP 200 con `status: "ok"`. Comprobar también inicio de sesión y operaciones antes de usar datos reales. El estado READY de Vercel solo confirma el despliegue, no la conexión a la base.

La aplicación sigue siendo el prototipo descrito en `docs/ESTADO.md`.
