<# Genera una Autoridad Certificadora local + certificado del servidor (con SAN para el nombre y la IP de la LAN).
   Alternativa recomendada: mkcert (https://github.com/FiloSottile/mkcert):  mkcert -install ; mkcert -pkcs12 pos-servidor 192.168.1.10
   NOTA: no ejecutado en Windows durante el desarrollo; el equivalente con openssl SÍ se verificó en Linux (curl/openssl validaron la cadena). #>
param([string]$Nombre = 'pos-servidor', [string]$Ip = '192.168.1.10', [string]$Salida = 'C:\POS\certs', [string]$Password = $(Read-Host 'Contraseña para el .pfx'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Salida | Out-Null
$ca = New-SelfSignedCertificate -Type Custom -Subject 'CN=POS Local CA' -KeyUsage CertSign,CRLSign -KeyExportPolicy Exportable `
      -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(10) -TextExtension @('2.5.29.19={critical}{text}ca=true')
$srv = New-SelfSignedCertificate -Subject "CN=$Nombre" -DnsName $Nombre,'localhost' -TextExtension @("2.5.29.17={text}DNS=$Nombre&DNS=localhost&IPAddress=$Ip", '2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
      -Signer $ca -KeyExportPolicy Exportable -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(2)
$pw = ConvertTo-SecureString $Password -AsPlainText -Force
Export-PfxCertificate -Cert $srv -FilePath "$Salida\servidor.pfx" -Password $pw | Out-Null
Export-Certificate -Cert $ca -FilePath "$Salida\POS-CA.cer" | Out-Null
Write-Host "Servidor: $Salida\servidor.pfx   |   CA para instalar en CADA caja: $Salida\POS-CA.cer"
Write-Host "En cada caja (como administrador):  Import-Certificate -FilePath .\POS-CA.cer -CertStoreLocation Cert:\LocalMachine\Root"
