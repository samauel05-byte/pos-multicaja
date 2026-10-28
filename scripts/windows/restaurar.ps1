<# Restaura un respaldo .posbak en una base NUEVA para verificarlo, o reemplaza la base de producción.
   Uso de verificación (seguro):    .\restaurar.ps1 -Archivo D:\POS-Respaldos\pos-...-auto.posbak -BaseDestino pos_verificacion
   Reemplazo de producción:         detenga el servicio, ejecute con -BaseDestino pos -Reemplazar, y vuelva a iniciar el servicio.
   NOTA: no ejecutado en Windows; el mismo comando (dotnet Pos.Api.dll --restore) se probó en Linux. #>
param([Parameter(Mandatory)][string]$Archivo, [Parameter(Mandatory)][string]$BaseDestino, [switch]$Reemplazar,
      [string]$AppDir = 'C:\POS\app', [string]$PgBin = 'C:\Program Files\PostgreSQL\16\bin',
      [string]$DbPassword = $(Read-Host 'Contraseña de pos_app'), [string]$Frase = $(Read-Host 'Frase de paso de los respaldos'))
$ErrorActionPreference = 'Stop'
if ($Reemplazar -and (Get-Service PosServidor -ErrorAction SilentlyContinue).Status -eq 'Running') { throw 'Detenga el servicio PosServidor antes de reemplazar la base.' }
$env:ConnectionStrings__Db = "Host=localhost;Port=5432;Database=postgres;Username=pos_app;Password=$DbPassword"
$env:Backup__Passphrase = $Frase; $env:Backup__PgBinDir = $PgBin
$args_ = @('--restore', $Archivo, '--target-db', $BaseDestino); if ($Reemplazar) { $args_ += '--replace' }
& "$AppDir\Pos.Api.exe" @args_
if ($LASTEXITCODE -ne 0) { throw "La restauración falló (código $LASTEXITCODE)" }
Write-Host "Restauración completa en '$BaseDestino'. Verifique: psql -U pos_app -d $BaseDestino -c 'select count(*) from ""Sales""'"
