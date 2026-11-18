param([string]$File = 'backups/BD_BosquezFelixRommelSahid.backup', [string]$Database = 'banking_restored')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($Database -notmatch '^[a-z][a-z0-9_]+$') { throw 'Nombre de base invalido' }
if (-not (Test-Path -LiteralPath $File)) { throw 'No existe el respaldo' }
docker compose cp $File 'postgres:/tmp/restore.backup'
if ($LASTEXITCODE -ne 0) { throw 'Fallo la copia' }
# No borra una base existente. Si existe, createdb falla y se detiene.
docker compose exec -T postgres createdb -U banking $Database
if ($LASTEXITCODE -ne 0) { throw 'No se creo la base; elija un nombre nuevo' }
docker compose exec -T postgres pg_restore -U banking -d $Database --exit-on-error /tmp/restore.backup
if ($LASTEXITCODE -ne 0) { throw 'Fallo pg_restore' }
Write-Host "Restauracion verificada en $Database. Consulte con psql o pgAdmin."
