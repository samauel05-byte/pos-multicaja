<# Instala el servidor como Servicio de Windows, abre el puerto en el firewall y define la configuración (variables de entorno del servicio).
   Ejecutar como ADMINISTRADOR en el servidor, con la carpeta publicada en C:\POS\app.
   NOTA: no ejecutado en Windows durante el desarrollo. Pruébelo primero en una máquina de ensayo. #>
param(
  [string]$AppDir = 'C:\POS\app',
  [string]$DbPassword = $(Read-Host 'Contraseña del usuario PostgreSQL "pos_app"'),
  [string]$BackupPassphrase = $(Read-Host 'Frase de paso para cifrar respaldos (guárdela en un lugar seguro)'),
  [string]$AdminPassword = $(Read-Host 'Contraseña inicial de "admin" (mín. 8)'),
  [string]$CertPfx = 'C:\POS\certs\servidor.pfx',
  [string]$CertPassword = $(Read-Host 'Contraseña del .pfx'),
  [int]$Port = 5443,
  [string]$BackupDir = 'D:\POS-Respaldos',
  [string]$PgBin = 'C:\Program Files\PostgreSQL\16\bin'
)
$ErrorActionPreference = 'Stop'
$jwt = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
$env_ = @(
  "ConnectionStrings__Db=Host=localhost;Port=5432;Database=pos;Username=pos_app;Password=$DbPassword",
  "Jwt__Key=$jwt",
  "Seed__AdminPassword=$AdminPassword",
  "Backup__Passphrase=$BackupPassphrase", "Backup__Directory=$BackupDir", "Backup__PgBinDir=$PgBin",
  "Kestrel__Endpoints__Https__Url=https://0.0.0.0:$Port",
  "Kestrel__Endpoints__Https__Certificate__Path=$CertPfx", "Kestrel__Endpoints__Https__Certificate__Password=$CertPassword",
  "TZ=America/Santo_Domingo"
)
New-Item -ItemType Directory -Force $BackupDir | Out-Null
if (-not (Get-Service PosServidor -ErrorAction SilentlyContinue)) {
  New-Service -Name PosServidor -DisplayName 'POS Servidor' -BinaryPathName "$AppDir\Pos.Api.exe" -StartupType Automatic | Out-Null
}
# Las variables del servicio se guardan en el registro (visibles solo para administradores).
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\PosServidor" -Name Environment -Type MultiString -Value $env_
sc.exe failure PosServidor reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
New-NetFirewallRule -DisplayName "POS Servidor $Port (solo red privada)" -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Private,Domain -ErrorAction SilentlyContinue | Out-Null
Start-Service PosServidor
Write-Host "Servicio iniciado. Pruebe: https://$env:COMPUTERNAME`:$Port  (el certificado debe estar confiado en cada caja)."
Write-Host "IMPORTANTE: elimine Seed__AdminPassword del registro del servicio después del primer arranque y cambie la contraseña de admin."
