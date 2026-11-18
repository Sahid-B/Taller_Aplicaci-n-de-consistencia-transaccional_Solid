$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not (Test-Path -LiteralPath '.env')) {
    # ================ Aqui se genera configuracion local sin incluir secretos en el codigo ================
    $passwordBytes = New-Object byte[] 24
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($passwordBytes) } finally { $generator.Dispose() }
    $bankPassword = [BitConverter]::ToString($passwordBytes).Replace('-', '')
    "POSTGRES_PASSWORD=$bankPassword`nPOSTGRES_PORT=55432`nAPP_PORT=5080" | Set-Content -LiteralPath '.env' -Encoding utf8
    Write-Host '.env creado. No lo suba al repositorio ni al ZIP.'
} else { Write-Host '.env ya existe; configuracion conservada.' }
