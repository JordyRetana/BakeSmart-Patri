param([Parameter(Mandatory = $true)][string]$ConnectionString)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Add-Type -Path (Join-Path $root 'bin/Debug/net8.0/MySqlConnector.dll')
$drafts = Get-Content (Join-Path $PSScriptRoot 'recipe-drafts-2026-09.json') -Raw | ConvertFrom-Json
$connection = [MySqlConnector.MySqlConnection]::new($ConnectionString)
$connection.Open()
$transaction = $connection.BeginTransaction()

function Invoke-Scalar([string]$sql, [hashtable]$parameters) {
    $command = $connection.CreateCommand()
    $command.Transaction = $transaction
    $command.CommandText = $sql
    foreach ($key in $parameters.Keys) { [void]$command.Parameters.AddWithValue("@$key", $parameters[$key]) }
    try { return $command.ExecuteScalar() } finally { $command.Dispose() }
}

function Invoke-NonQuery([string]$sql, [hashtable]$parameters) {
    $command = $connection.CreateCommand()
    $command.Transaction = $transaction
    $command.CommandText = $sql
    foreach ($key in $parameters.Keys) { [void]$command.Parameters.AddWithValue("@$key", $parameters[$key]) }
    try { return $command.ExecuteNonQuery() } finally { $command.Dispose() }
}

try {
    $locationId = Invoke-Scalar "SELECT InventoryLocationId FROM UbicacionesInventario WHERE Name='Bodega principal' LIMIT 1" @{}
    if ($null -eq $locationId) { throw 'Falta la bodega principal.' }
    $targets = @{}
    foreach ($ingredient in $drafts.ingredients) {
        $targets[[string]$ingredient[0]] = if ([string]$ingredient[2] -eq 'L') { [decimal]5 } else { [decimal]5 }
    }
    $targets['PAS-003'] = [decimal]12
    $changed = 0
    foreach ($code in $targets.Keys) {
        $productId = Invoke-Scalar 'SELECT ProductId FROM Productos WHERE Code=@code AND IsActive=1 ORDER BY ProductId DESC LIMIT 1' @{code=$code}
        if ($null -eq $productId) { throw "Producto inexistente: $code" }
        $current = Invoke-Scalar 'SELECT COALESCE(SUM(Quantity),0) FROM ExistenciasInventario WHERE ProductId=@id' @{id=$productId}
        if ([decimal]$current -gt 0) { continue }
        $quantity = $targets[$code]
        [void](Invoke-NonQuery 'INSERT INTO ExistenciasInventario (ProductId,InventoryLocationId,Quantity,UpdatedAt) VALUES (@id,@location,@quantity,UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE Quantity=VALUES(Quantity),UpdatedAt=UTC_TIMESTAMP()' @{id=$productId;location=$locationId;quantity=$quantity})
        [void](Invoke-NonQuery "INSERT INTO MovimientosInventario (ProductId,InventoryLocationId,MovementType,Quantity,ResponsibleUserId,Note,CreatedAt) VALUES (@id,@location,'ENTRADA',@quantity,NULL,@note,UTC_TIMESTAMP())" @{id=$productId;location=$locationId;quantity=$quantity;note='Existencia provisional solicitada para recetas; verificar con conteo físico.'})
        $changed++
    }
    $transaction.Commit()
    Write-Output "Productos con existencia provisional registrada: $changed"
}
catch { $transaction.Rollback(); throw }
finally { $transaction.Dispose(); $connection.Dispose() }
