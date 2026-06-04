USE BakeSmartPatri;
GO

-- Insertar Categorías
IF NOT EXISTS (SELECT 1 FROM dbo.Categorias WHERE Nombre = 'Tortas')
BEGIN
    INSERT INTO dbo.Categorias (Nombre, Descripcion)
    VALUES 
    ('Tortas', 'Tortas tradicionales y personalizadas'),
    ('Cupcakes', 'Cupcakes y bocaditos'),
    ('Panes', 'Panes y pastelería básica'),
    ('Postres', 'Postres individuales y cheesecakes'),
    ('Insumos', 'Harinas, azúcares y materiales');
    
    PRINT 'Categorías iniciales insertadas.';
END
ELSE
    PRINT 'Categorías ya existian.';
GO

-- Insertar Productos de prueba
IF NOT EXISTS (SELECT 1 FROM dbo.Productos WHERE Nombre = 'Cake Red Velvet')
BEGIN
    INSERT INTO dbo.Productos 
        (Nombre, Codigo, CategoriaId, UnidadMedida, Stock, StockMinimo, PrecioVenta, Costo, FechaVencimiento)
    VALUES 
    ('Cake Red Velvet', 'CAKE-001', 1, 'kg', 12.5, 5, 28500, 8500, DATEADD(day, 5, GETDATE())),
    ('Cupcakes de Vainilla', 'CUPC-001', 2, 'unidad', 120, 50, 850, 320, DATEADD(day, 3, GETDATE())),
    ('Pan de Mantequilla', 'PAN-001', 3, 'unidad', 45, 20, 1200, 450, DATEADD(day, 2, GETDATE())),
    ('Cheesecake Maracuyá', 'CHEE-001', 4, 'kg', 8, 3, 32000, 9500, DATEADD(day, 4, GETDATE())),
    ('Harina de Trigo', 'INS-001', 5, 'kg', 80, 30, 950, 650, NULL);
    
    PRINT 'Productos iniciales insertados.';
END
ELSE
    PRINT 'Productos demo ya existian.';
GO

-- Verificacion
PRINT 'verificacion de datos insertados:';

SELECT 
    c.Nombre AS Categoria,
    COUNT(p.Id) AS Cantidad_Productos,
    SUM(p.Stock) AS Stock_Total
FROM dbo.Categorias c
LEFT JOIN dbo.Productos p ON c.Id = p.CategoriaId
GROUP BY c.Nombre
ORDER BY c.Nombre;
GO

PRINT 'Datos iniciales de inventario listos';