IF DB_ID('BakeSmartPatri') IS NULL
CREATE DATABASE BakeSmartPatri;
GO

USE BakeSmartPatri;
GO

--Creacion de la tabla Roles

IF OBJECT_ID('Dbo.Roles', 'U') IS NOT NULL
DROP TABLE dbo.Roles;
GO

CREATE TABLE dbo.Roles
(
	Id INT PRIMARY KEY IDENTITY(1,1),
	Nombre NVARCHAR(50) NOT NULL UNIQUE,
	Descripcion NVARCHAR(255) NOT NULL,
	Permisos NVARCHAR(MAX) NOT NULL,
	CreatedAt DATETIME2(0) DEFAULT SYSUTCDATETIME(),
);
GO

print 'Tabla Roles creada exitosamente.';
