USE BakeSmartPatri;
GO

-- Insertar Roles iniciales
IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE Nombre = 'Admin')
BEGIN
    INSERT INTO dbo.Roles (Nombre, Descripcion, Permisos)
    VALUES 
    ('Admin',   'Acceso total al sistema', 'all'),
    ('Staff',   'Operador del sistema', 'pedidos,inventario,pos,reportes'),
    ('Cliente', 'Cliente del portal web', 'mis-pedidos,perfil');
    
    PRINT 'Roles iniciales insertados.';
END
ELSE
    PRINT 'Los roles ya existian.';
GO

-- Insertar Usuarios iniciales para pruebas
IF NOT EXISTS (SELECT 1 FROM dbo.Usuarios WHERE email = 'admin@demo.com')
BEGIN
    INSERT INTO dbo.Usuarios 
        (Nombre, Apellido, email, telefono, PasswordHash, Direccion, IdRol, Activo)
    VALUES 
        ('Admin',    'Demo',     'admin@demo.com',    '8888-0000', '$2a$11$dummyhash1234', 'San José',     1, 1),
        ('Staff',    'Demo',     'staff@demo.com',    '8888-0001', '$2a$11$dummyhash1234', 'San José',     2, 1),
        ('Ricardo',  'Ulate',    'cliente@demo.com',  '8888-0002', '$2a$11$dummyhash1234', 'San Rafael',   3, 1),
        ('Jordy',    'Retana',   'jordy@demo.com',    '8888-0003', '$2a$11$dummyhash1234', 'Heredia',      1, 1),
        ('Gabriel',  'Reyes',    'gabriel@demo.com',  '8888-0004', '$2a$11$dummyhash1234', 'Alajuela',     2, 1);
    
    PRINT 'Usuarios iniciales insertados correctamente.';
END
ELSE
    PRINT 'Los usuarios demo ya existian.';
GO

-- verificacion final
PRINT 'verificacion: '

SELECT 
    u.Id,
    u.Nombre + ' ' + u.Apellido AS NombreCompleto,
    u.email,
    r.Nombre AS Rol,
    u.Activo
FROM dbo.Usuarios u
INNER JOIN dbo.Roles r ON u.IdRol = r.Id
ORDER BY r.Nombre, u.Nombre;
GO

PRINT 'Tarea 39 terminada';