$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Preparar.ps1')
docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Abra Docker Desktop y espere a que el motor este listo.' }
# Docker BuildKit en Windows puede rechazar rutas con tildes en claves de sesion gRPC.
# El codigo permanece en la carpeta original; solo el contexto de construccion es temporal.
$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('banking-u2-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Dockerfile') -Destination $buildRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot '.dockerignore') -Destination $buildRoot
    # Copiar solo fuentes; no dependencias, secretos ni backup.
    robocopy (Join-Path $projectRoot 'src') (Join-Path $buildRoot 'src') /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'No se pudo preparar el contexto Docker' }
    Set-Location -LiteralPath $buildRoot
    docker build --build-arg PROJECT=BankingApp -t banking-u2-app:local .
    if ($LASTEXITCODE -ne 0) { throw 'Fallo la imagen de la aplicacion' }
    docker build --build-arg PROJECT=ExternalService -t banking-u2-external-service:local .
    if ($LASTEXITCODE -ne 0) { throw 'Fallo la imagen del proveedor' }
    Set-Location -LiteralPath $projectRoot
    docker compose up -d --no-build
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron iniciar los contenedores' }
    docker compose ps
    Write-Host 'Aplicacion lista. Abra http://localhost:5080 (o APP_PORT de su .env).'
} finally {
    Set-Location -LiteralPath $projectRoot
    $resolvedBuild = [System.IO.Path]::GetFullPath($buildRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedBuild.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $resolvedBuild -Leaf) -like 'banking-u2-build-*') {
        Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
    }
}
