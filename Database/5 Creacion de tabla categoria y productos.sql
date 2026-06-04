USE BakeSmartPatri;
GO

-- Tabla de Categorias

IF OBJECT_ID('dbo.Categorias', 'U') IS NOT NULL
    DROP TABLE dbo.Categorias;
GO

CREATE TABLE dbo.Categorias
(
    Id INT PRIMARY KEY IDENTITY(1,1),
    Nombre NVARCHAR(100) NOT NULL UNIQUE,
    Descripcion NVARCHAR(255) NULL,
    Activo BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2(0) DEFAULT SYSUTCDATETIME()
);
GO

-- Tabla de Productos
IF OBJECT_ID('dbo.Productos', 'U') IS NOT NULL
    DROP TABLE dbo.Productos;
GO

CREATE TABLE dbo.Productos
(
    Id INT PRIMARY KEY IDENTITY(1,1),
    Nombre NVARCHAR(150) NOT NULL,
    Codigo NVARCHAR(50) NULL UNIQUE,
    CategoriaId INT NOT NULL,
    UnidadMedida NVARCHAR(30) NOT NULL,        -- kg, unidad, paquete, etc.
    Stock DECIMAL(10,2) NOT NULL DEFAULT 0,
    StockMinimo DECIMAL(10,2) NOT NULL DEFAULT 5,
    PrecioVenta DECIMAL(12,2) NOT NULL,
    Costo DECIMAL(12,2) NOT NULL,
    FechaVencimiento DATE NULL,
    Activo BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2(0) DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_Productos_Categorias 
    FOREIGN KEY (CategoriaId) REFERENCES dbo.Categorias(Id)
);
GO

PRINT 'Tablas Categorias y Productos creadas.';
GO