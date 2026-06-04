USE BakeSmartPatri;
GO

-- 1. Verificar y crear Foreign Key

IF NOT EXISTS (
    SELECT * FROM sys.foreign_keys 
    WHERE name = 'FK_Usuarios_Roles'
)
BEGIN
    ALTER TABLE dbo.Usuarios
    ADD CONSTRAINT FK_Usuarios_Roles 
    FOREIGN KEY (IdRol) REFERENCES dbo.Roles(Id)
    ON DELETE NO ACTION
    ON UPDATE CASCADE;
END
GO

-- 2. Índice para mejorar la consulta o rendimiento

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Usuarios_IdRol')
BEGIN
    CREATE NONCLUSTERED INDEX IX_Usuarios_IdRol 
    ON dbo.Usuarios(IdRol);
END
GO

-- 3. Verificación de la relación (Importante para la tarea)

PRINT 'Verificacion de la relacion usuarios y los roles'

SELECT 
    r.Nombre AS Rol,
    r.Descripcion,
    COUNT(u.Id) AS Cantidad_Usuarios,
    STRING_AGG(u.Nombre + ' ' + u.Apellido, ', ') AS Ejemplos_Usuarios
FROM dbo.Roles r
LEFT JOIN dbo.Usuarios u ON r.Id = u.IdRol
GROUP BY r.Nombre, r.Descripcion
ORDER BY r.Nombre;
GO

-- Mostrar detalles de la tabla usuarios
EXEC sp_help 'dbo.Usuarios';
GO

PRINT 'Relacion entre Usuarios y Roles definida correctamente';