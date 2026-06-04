# BakeSmart Patri - SQL Server

## 1. Crear o reconstruir la base

Ejecutar en SQL Server Management Studio o Azure Data Studio:

```sql
Database/BakeSmartPatri.sql
```

El script crea la base `BakeSmartPatri`, normaliza las tablas y carga datos iniciales.

## 2. Que queda normalizado

- Productos separados de tipos, categorias, subcategorias, unidades e imagenes.
- Inventario separado en ubicaciones, saldos y movimientos.
- Pedidos separados en clientes, direcciones, canales, estados, metodos de pago, items y eventos de seguimiento.
- Promociones con relacion muchos-a-muchos hacia productos.
- Contabilidad separada en catalogo de cuentas, asientos y lineas de asiento.
- Gastos, proveedores, pagos, auditoria y configuraciones en tablas independientes.

## 3. Conexion de la app

Revisar `appsettings.Development.json` o `appsettings.json`:

```json
"ConnectionStrings": {
  "BakeSmartDb": "Server=TU_SERVIDOR;Database=BakeSmartPatri;User Id=TU_USUARIO;Password=TU_PASSWORD;TrustServerCertificate=True;MultipleActiveResultSets=True"
},
"Features": {
  "UseSqlDatabase": true
}
```

Con autenticacion de Windows local:

```json
"BakeSmartDb": "Server=localhost;Database=BakeSmartPatri;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
```

## 4. Script de base de datos

La solucion mantiene un solo script principal para evitar duplicados:

```text
Database/BakeSmartPatri.sql
```

Uso recomendado:

1. Abrir `Database/BakeSmartPatri.sql` en SQL Server Management Studio o Azure Data Studio.
2. Ejecutar el script contra SQL Server.
3. Revisar la conexion en `appsettings.Development.json`.
4. Levantar la app y validar `/api/health`.
5. Usar **SQL Server Object Explorer** si desea revisar las tablas creadas en la base local.

## 5. Probar conexion

Levantar la app y abrir:

```text
http://localhost:5275/api/health
```

Debe responder:

```json
{
  "enabled": true,
  "status": "connected",
  "database": "BakeSmartPatri"
}
```

Endpoints que leen desde SQL:

- `/api/health`
- `/api/dashboard`
- `/api/orders`
- `/api/inventory`
