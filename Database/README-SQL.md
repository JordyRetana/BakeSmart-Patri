# BakeSmart Patri - conexión SQL

## 1. Crear la base

Ejecuta este archivo en SQL Server Management Studio o Azure Data Studio:

```sql
Database/BakeSmartPatri.sql
```

El script crea la base `BakeSmartPatri`, todas las tablas principales y datos demo:

- usuarios y roles
- clientes
- productos e inventario
- movimientos de inventario
- pedidos y detalle de pedidos
- ventas
- promociones
- contabilidad básica
- pagos a proveedores
- destinos con latitud y longitud
- bitácora

## 2. Conectar el proyecto

Edita `appsettings.json` o `appsettings.Development.json`:

```json
"ConnectionStrings": {
  "BakeSmartDb": "Server=TU_SERVIDOR;Database=BakeSmartPatri;User Id=TU_USUARIO;Password=TU_PASSWORD;TrustServerCertificate=True;MultipleActiveResultSets=True"
},
"Features": {
  "UseSqlDatabase": true
}
```

Si usas autenticación de Windows local:

```json
"BakeSmartDb": "Server=localhost;Database=BakeSmartPatri;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
```

## 3. Probar conexión

Levanta el proyecto y abre:

```text
http://localhost:5275/api/health
```

Cuando esté conectado debe responder algo parecido a:

```json
{
  "enabled": true,
  "status": "connected",
  "database": "BakeSmartPatri"
}
```

## 4. Endpoints listos

Con `UseSqlDatabase` en `true`, estos endpoints leen desde SQL:

- `/api/health`
- `/api/dashboard`
- `/api/orders`
- `/api/inventory`

