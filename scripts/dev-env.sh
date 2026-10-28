# Variables para desarrollo en Linux (source scripts/dev-env.sh). Valores de DEMO: no usar en producción.
export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:/root/.dotnet/tools:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
export ConnectionStrings__Db="Host=localhost;Port=5432;Database=pos_dev;Username=postgres;Password=postgres"
export Jwt__Key="dev-only-key-dev-only-key-dev-only-key-1234"
export Seed__Demo=true Backup__Passphrase="clave-de-respaldo-demo" Backup__Directory=/tmp/pos-backups
export Backup__PgBinDir=/usr/lib/postgresql/16/bin ASPNETCORE_URLS=http://127.0.0.1:5080
