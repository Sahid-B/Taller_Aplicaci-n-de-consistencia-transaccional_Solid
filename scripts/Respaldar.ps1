param([string]$Name = 'BD_BosquezFelixRommelSahid.backup')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($Name -notmatch '^[A-Za-z0-9_.-]+\.backup$') { throw 'Use un nombre simple terminado en .backup' }
New-Item -ItemType Directory -Path 'backups' -Force | Out-Null
# Se copia el binario sin redireccion de PowerShell para evitar corrupcion del backup.
docker compose exec -T postgres pg_dump -U banking -d banking_system -F c -f /tmp/banking.backup
if ($LASTEXITCODE -ne 0) { throw 'Fallo pg_dump' }
docker compose cp "postgres:/tmp/banking.backup" "backups/$Name"
if ($LASTEXITCODE -ne 0) { throw 'Fallo la copia del respaldo' }
Write-Host "Backup listo: backups/$Name"
