param(
    [Parameter(Mandatory = $true)][string]$ConnectionString
)

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
    $ingredientIds = @{}
    $createdIngredients = 0
    foreach ($ingredient in $drafts.ingredients) {
        $code = [string]$ingredient[0]
        $name = [string]$ingredient[1]
        $unit = [string]$ingredient[2]
        $id = Invoke-Scalar 'SELECT ProductId FROM Productos WHERE Code=@code AND Name=@name AND IsActive=1 LIMIT 1' @{code=$code;name=$name}
        if ($null -eq $id) {
            $codeTaken = Invoke-Scalar 'SELECT COUNT(*) FROM Productos WHERE Code=@code' @{code=$code}
            if ([int]$codeTaken -ne 0) { throw "Código de ingrediente ocupado: $code" }
            $unitId = Invoke-Scalar 'SELECT UnitMeasureId FROM UnidadesMedida WHERE Name=@unit LIMIT 1' @{unit=$unit}
            if ($null -eq $unitId) { throw "Unidad inexistente: $unit" }
            [void](Invoke-NonQuery 'INSERT INTO Productos (ProductTypeId,ProductCategoryId,UnitMeasureId,Code,Name,Description,UnitPrice,UnitCost,MinStock,IsActive,CreatedAt) VALUES (2,5,@unitId,@code,@name,@description,0,0,0,1,UTC_TIMESTAMP())' @{unitId=$unitId;code=$code;name=$name;description='Ingrediente para borrador de receta; existencia inicial cero.'})
            $id = Invoke-Scalar 'SELECT LAST_INSERT_ID()' @{}
            $createdIngredients++
        }
        $ingredientIds[$code] = [int]$id
    }

    $createdRecipes = 0
    $skippedRecipes = 0
    foreach ($recipe in $drafts.recipes) {
        $productId = [int]$recipe.productId
        $valid = Invoke-Scalar "SELECT COUNT(*) FROM Productos p JOIN TiposProducto t ON t.ProductTypeId=p.ProductTypeId WHERE p.ProductId=@id AND p.IsActive=1 AND t.Name='Producto terminado'" @{id=$productId}
        if ([int]$valid -ne 1) { throw "Producto terminado activo inexistente: $productId" }
        $existing = Invoke-Scalar 'SELECT RecipeId FROM RecetasProducto WHERE ProductId=@id' @{id=$productId}
        if ($null -ne $existing) { $skippedRecipes++; continue }
        $notes = "BORRADOR ESTIMADO: validar fórmula, rendimiento y unidades antes de aprobar. Fuente de referencia: $($recipe.source)"
        [void](Invoke-NonQuery "INSERT INTO RecetasProducto (ProductId,Status,YieldQuantity,YieldUnit,WastePercent,Notes,CreatedAt,UpdatedAt) VALUES (@id,'En revision',@yield,'unidad',0,@notes,UTC_TIMESTAMP(),UTC_TIMESTAMP())" @{id=$productId;yield=[decimal]$recipe.yield;notes=$notes})
        $recipeId = [int](Invoke-Scalar 'SELECT LAST_INSERT_ID()' @{})
        foreach ($item in $recipe.items) {
            $key = [string]$item[0]
            $ingredientId = if ($ingredientIds.ContainsKey($key)) { $ingredientIds[$key] } else { [int]$key }
            $unit = Invoke-Scalar 'SELECT u.Name FROM Productos p JOIN UnidadesMedida u ON u.UnitMeasureId=p.UnitMeasureId WHERE p.ProductId=@id AND p.IsActive=1' @{id=$ingredientId}
            if ($null -eq $unit) { throw "Ingrediente inexistente: $key" }
            [void](Invoke-NonQuery 'INSERT INTO IngredientesReceta (RecipeId,IngredientProductId,Quantity,Unit,IsOptional,Notes) VALUES (@recipeId,@ingredientId,@quantity,@unit,0,NULL)' @{recipeId=$recipeId;ingredientId=$ingredientId;quantity=[decimal]$item[1];unit=[string]$unit})
        }
        $createdRecipes++
    }
    $transaction.Commit()
    Write-Output "Ingredientes creados: $createdIngredients; recetas en revisión creadas: $createdRecipes; recetas existentes respetadas: $skippedRecipes"
}
catch {
    $transaction.Rollback()
    throw
}
finally {
    $transaction.Dispose()
    $connection.Dispose()
}
