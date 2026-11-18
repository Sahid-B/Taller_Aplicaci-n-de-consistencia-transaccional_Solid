param([string]$BaseUrl = 'http://localhost:5080')
$ErrorActionPreference = 'Stop'
$headers = @{ 'X-Operator' = 'Prueba automatizada' }
function Api([string]$Path, [string]$Method = 'GET', $Body = $null) {
    $argsApi = @{ Uri = "$BaseUrl/api/$Path"; Method = $Method; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $argsApi.Body = ConvertTo-Json $Body -Depth 8 }
    # Invoke-RestMethod puede entregar una matriz como un solo objeto al pipeline.
    foreach ($item in (Invoke-RestMethod @argsApi)) { $item }
}
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw "FALLO: $Message" }; Write-Host "OK: $Message" }
function ExpectFailure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch {
        if ($_.ErrorDetails.Message -match $Pattern -or $_.Exception.Message -match $Pattern) { return }
        throw
    }
    throw "Se esperaba un error: $Pattern"
}
Write-Host 'Estas pruebas crean clientes, cuentas y movimientos de demostracion.'
Api 'provider/false' 'POST' | Out-Null
# Cada ejecucion comienza con proveedor disponible. Esperar ventana si quedo OPEN de una demostracion.
Start-Sleep -Seconds 11
$inputCustomer = @{ firstName = 'Prueba'; lastName = 'CRUD'; email = "test-$([Guid]::NewGuid().ToString('N'))@example.com"; city = 'Quito' }
$created = Api 'customers' 'POST' $inputCustomer
Assert ($created.id -gt 0) 'Crear cliente'
$inputCustomer.city = 'Guayaquil'
Api "customers/$($created.id)" 'PUT' $inputCustomer | Out-Null
$updated = @(Api 'customers') | Where-Object customer_id -eq $created.id
Assert ($updated.city -eq 'Guayaquil') 'Editar y consultar cliente'
Api "customers/$($created.id)" 'DELETE' | Out-Null
Assert (-not (@(Api 'customers') | Where-Object customer_id -eq $created.id)) 'Eliminacion logica'
$demo = Api 'demo/concurrency' 'POST'
Assert (@($demo.attempts | Where-Object success).Count -eq 1) 'Concurrencia: exactamente un COMMIT'
Assert (@($demo.attempts | Where-Object { -not $_.success }).Count -eq 1) 'Concurrencia: un rechazo'
Assert ($demo.balances[0].balance -eq 20) 'Aislamiento: saldo origen 20'
Assert (($demo.balances | Measure-Object balance -Sum).Sum -eq 100) 'Consistencia: total conservado'
$source = [int]$demo.accounts[0]; $destination = [int]$demo.accounts[1]
$request = @{ fromAccountId = $source; toAccountId = $destination; amount = 1; simulateError = $true }
$before = @(Api 'accounts') | Where-Object account_id -in $demo.accounts
$beforeStats = (Api 'dashboard')[0]
ExpectFailure { Api 'transfers' 'POST' $request } 'ROLLBACK'
$after = @(Api 'accounts') | Where-Object account_id -in $demo.accounts
Assert ((ConvertTo-Json $before -Compress) -eq (ConvertTo-Json $after -Compress)) 'Atomicidad: error no cambia saldos'
Assert (((Api 'dashboard')[0]).transactions -eq $beforeStats.transactions) 'Atomicidad: sin movimiento parcial'
$request.simulateError = $false
$transfer = Api 'transfers' 'POST' $request
Assert ($transfer.status -eq 'COMMIT' -and $transfer.observers.Count -eq 2) 'COMMIT y dos observadores'
$savedHeaders = $headers; $headers = @{}
ExpectFailure { Api 'transfers' 'POST' $request } 'operador'
$headers = $savedHeaders
Write-Host 'OK: Proxy exige identificar operador'
Api 'provider/true' 'POST' | Out-Null
1..3 | ForEach-Object { ExpectFailure { Api 'transfers' 'POST' $request } 'Proveedor externo' }
Assert ((Api 'circuit').state -eq 'OPEN') 'Circuit Breaker OPEN tras tres fallos'
$callsBefore = (Api 'provider').calls
ExpectFailure { Api 'transfers' 'POST' $request } 'OPEN'
Assert ((Api 'provider').calls -eq $callsBefore) 'OPEN evita una llamada al proveedor'
Start-Sleep -Seconds 11
ExpectFailure { Api 'transfers' 'POST' $request } 'Proveedor externo'
Assert ((Api 'circuit').state -eq 'OPEN') 'HALF-OPEN vuelve a OPEN si el proveedor sigue fallando'
Api 'provider/false' 'POST' | Out-Null
Start-Sleep -Seconds 11
$recovered = Api 'transfers' 'POST' $request
$circuit = Api 'circuit'
Assert ($circuit.state -eq 'CLOSED') 'Recuperacion HALF-OPEN -> CLOSED'
Assert (($circuit.history -join ' ') -match 'HALF-OPEN') 'Historial registra HALF-OPEN'
Write-Host "PRUEBAS CORRECTAS. Movimiento para verificar durabilidad: $($recovered.transactionId)"
