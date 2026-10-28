<# Publica el servidor para Windows x64 (autocontenido: no requiere instalar .NET en el servidor).
   Ejecutar en una PC de DESARROLLO con .NET 8 SDK y Node 22:   .\publicar.ps1
   NOTA: este script NO se ejecutó en Windows durante el desarrollo (se desarrolló en Linux). Revise la salida. #>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
Push-Location "$root\web";    npm ci; npm run build; Pop-Location          # compila la interfaz hacia server\src\Pos.Api\wwwroot
Push-Location "$root\server"; dotnet publish src\Pos.Api -c Release -r win-x64 --self-contained -o "$root\dist\pos-servidor"; Pop-Location
Write-Host "Listo: $root\dist\pos-servidor  (copie esta carpeta al servidor, p. ej. a C:\POS\app)"
