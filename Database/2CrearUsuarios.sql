USE BakeSmartPatri;
GO

--Creacion de la tabla Usuarios

IF OBJECT_ID('Dbo.Usuarios', 'U') IS NOT NULL
DROP TABLE dbo.Usuarios;
GO

CREATE TABLE dbo.Usuarios
(
	Id INT PRIMARY KEY IDENTITY(1,1),
	Nombre NVARCHAR(50) NOT NULL,
	Apellido NVARCHAR(50) NOT NULL,
	email NVARCHAR(100) NOT NULL UNIQUE,
	telefono NVARCHAR(20) NULL,
	PasswordHash NVARCHAR(255) NOT NULL,
	Direccion NVARCHAR(255) NULL,
	IdRol INT NOT NULL,
	Activo BIT NOT NULL DEFAULT 1,
	CreatedAt datetime2(0) DEFAULT SYSUTCDATETIME(),

	Constraint FK_Usuarios_Roles FOREIGN KEY (IdRol) REFERENCES Roles(Id)
	
);
GO

CREATE INDEX IX_Usuarios_email ON dbo.Usuarios(email);
CREATE INDEX IX_Usuarios_IdRol ON dbo.Usuarios(IdRol);
GO

print 'Tabla Usuarios creada exitosamente.';
