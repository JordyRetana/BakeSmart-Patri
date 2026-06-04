USE BakeSmartPatri;
GO

-- Tabla de Movimientos de Inventario
IF OBJECT_ID('dbo.MovimientosInventario', 'U') IS NOT NULL
    DROP TABLE dbo.MovimientosInventario;
GO

CREATE TABLE dbo.MovimientosInventario
(
    Id INT PRIMARY KEY IDENTITY(1,1),
    ProductoId INT NOT NULL,
    TipoMovimiento NVARCHAR(30) NOT NULL,
    Cantidad DECIMAL(10,2) NOT NULL,
    Motivo NVARCHAR(200) NULL,               
    Referencia NVARCHAR(100) NULL,            
    UsuarioId INT NULL,                      
    CreatedAt DATETIME2(0) DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_Movimientos_Productos 
    FOREIGN KEY (ProductoId) REFERENCES dbo.Productos(Id),
    
    CONSTRAINT FK_Movimientos_Usuarios 
    FOREIGN KEY (UsuarioId) REFERENCES dbo.Usuarios(Id)
);
GO

-- Índices para mejorar velocidad de consultas
CREATE INDEX IX_Movimientos_ProductoId ON dbo.MovimientosInventario(ProductoId);
CREATE INDEX IX_Movimientos_Fecha ON dbo.MovimientosInventario(CreatedAt);
GO

PRINT 'Tabla MovimientosInventario creada correctamente';
GO