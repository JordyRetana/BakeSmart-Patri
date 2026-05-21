/*
    BakeSmart Patri - SQL Server setup
    Ejecutar en SQL Server Management Studio o Azure Data Studio.

    Después de correrlo, cambiar en appsettings.json:
    ConnectionStrings:BakeSmartDb
    Features:UseSqlDatabase = true
*/

IF DB_ID(N'BakeSmartPatri') IS NULL
BEGIN
    CREATE DATABASE BakeSmartPatri;
END
GO

USE BakeSmartPatri;
GO

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.Logs', N'U') IS NOT NULL DROP TABLE dbo.Logs;
IF OBJECT_ID(N'dbo.SupplierPayments', N'U') IS NOT NULL DROP TABLE dbo.SupplierPayments;
IF OBJECT_ID(N'dbo.Expenses', N'U') IS NOT NULL DROP TABLE dbo.Expenses;
IF OBJECT_ID(N'dbo.AccountingEntries', N'U') IS NOT NULL DROP TABLE dbo.AccountingEntries;
IF OBJECT_ID(N'dbo.CashSessions', N'U') IS NOT NULL DROP TABLE dbo.CashSessions;
IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL DROP TABLE dbo.Sales;
IF OBJECT_ID(N'dbo.Promotions', N'U') IS NOT NULL DROP TABLE dbo.Promotions;
IF OBJECT_ID(N'dbo.InventoryMovements', N'U') IS NOT NULL DROP TABLE dbo.InventoryMovements;
IF OBJECT_ID(N'dbo.OrderItems', N'U') IS NOT NULL DROP TABLE dbo.OrderItems;
IF OBJECT_ID(N'dbo.Orders', N'U') IS NOT NULL DROP TABLE dbo.Orders;
IF OBJECT_ID(N'dbo.Products', N'U') IS NOT NULL DROP TABLE dbo.Products;
IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL DROP TABLE dbo.Customers;
IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL DROP TABLE dbo.Users;
IF OBJECT_ID(N'dbo.Roles', N'U') IS NOT NULL DROP TABLE dbo.Roles;
IF OBJECT_ID(N'dbo.GeoDestinations', N'U') IS NOT NULL DROP TABLE dbo.GeoDestinations;
IF OBJECT_ID(N'dbo.AppSettings', N'U') IS NOT NULL DROP TABLE dbo.AppSettings;
GO

CREATE TABLE dbo.Roles
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    Name nvarchar(50) NOT NULL CONSTRAINT UQ_Roles_Name UNIQUE,
    Description nvarchar(200) NOT NULL,
    Permissions nvarchar(max) NOT NULL
);

CREATE TABLE dbo.Users
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    FirstName nvarchar(80) NOT NULL,
    LastName nvarchar(80) NOT NULL,
    Email nvarchar(160) NOT NULL CONSTRAINT UQ_Users_Email UNIQUE,
    Phone nvarchar(40) NULL,
    PasswordHash nvarchar(300) NOT NULL,
    Address nvarchar(300) NULL,
    RoleId int NOT NULL CONSTRAINT FK_Users_Roles REFERENCES dbo.Roles(Id),
    Active bit NOT NULL CONSTRAINT DF_Users_Active DEFAULT 1,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Customers
(
    Id int IDENTITY(200,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    FullName nvarchar(160) NOT NULL,
    Email nvarchar(160) NOT NULL CONSTRAINT UQ_Customers_Email UNIQUE,
    Phone nvarchar(40) NULL,
    Address nvarchar(300) NULL,
    Frequent bit NOT NULL CONSTRAINT DF_Customers_Frequent DEFAULT 0,
    TotalSpent decimal(18,2) NOT NULL CONSTRAINT DF_Customers_TotalSpent DEFAULT 0,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Products
(
    Id int IDENTITY(300,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    Code nvarchar(40) NOT NULL CONSTRAINT UQ_Products_Code UNIQUE,
    Description nvarchar(180) NOT NULL,
    ProductType nvarchar(60) NOT NULL,
    Unit nvarchar(30) NOT NULL,
    Category nvarchar(80) NOT NULL,
    Subcategory nvarchar(80) NULL,
    Price decimal(18,2) NOT NULL,
    Stock decimal(18,2) NOT NULL CONSTRAINT DF_Products_Stock DEFAULT 0,
    MinStock decimal(18,2) NOT NULL CONSTRAINT DF_Products_MinStock DEFAULT 0,
    Active bit NOT NULL CONSTRAINT DF_Products_Active DEFAULT 1,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Products_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Products_Stock CHECK (Stock >= 0),
    CONSTRAINT CK_Products_MinStock CHECK (MinStock >= 0)
);

CREATE TABLE dbo.Orders
(
    Id int IDENTITY(1000,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers(Id),
    Channel nvarchar(40) NOT NULL,
    Address nvarchar(300) NOT NULL,
    Notes nvarchar(500) NULL,
    Status nvarchar(60) NOT NULL,
    PaymentStatus nvarchar(60) NOT NULL,
    PaymentMethod nvarchar(60) NOT NULL,
    Subtotal decimal(18,2) NOT NULL,
    Discount decimal(18,2) NOT NULL CONSTRAINT DF_Orders_Discount DEFAULT 0,
    Tax decimal(18,2) NOT NULL CONSTRAINT DF_Orders_Tax DEFAULT 0,
    Total decimal(18,2) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Orders_CreatedAt DEFAULT SYSUTCDATETIME(),
    DeliveryDate date NOT NULL,
    CurrentLat decimal(10,6) NOT NULL,
    CurrentLng decimal(10,6) NOT NULL,
    DestinationLat decimal(10,6) NOT NULL,
    DestinationLng decimal(10,6) NOT NULL,
    DestinationCountry nvarchar(80) NOT NULL,
    DestinationLabel nvarchar(160) NOT NULL,
    RouteMode nvarchar(20) NOT NULL,
    TrackingStep int NOT NULL CONSTRAINT DF_Orders_TrackingStep DEFAULT 0,
    OriginLabel nvarchar(160) NOT NULL,
    CONSTRAINT CK_Orders_Total CHECK (Total >= 0),
    CONSTRAINT CK_Orders_Lat CHECK (CurrentLat BETWEEN -90 AND 90 AND DestinationLat BETWEEN -90 AND 90),
    CONSTRAINT CK_Orders_Lng CHECK (CurrentLng BETWEEN -180 AND 180 AND DestinationLng BETWEEN -180 AND 180)
);

CREATE TABLE dbo.OrderItems
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
    OrderId int NOT NULL CONSTRAINT FK_OrderItems_Orders REFERENCES dbo.Orders(Id) ON DELETE CASCADE,
    ProductId int NOT NULL CONSTRAINT FK_OrderItems_Products REFERENCES dbo.Products(Id),
    Quantity decimal(18,2) NOT NULL,
    UnitPrice decimal(18,2) NOT NULL,
    CONSTRAINT CK_OrderItems_Quantity CHECK (Quantity > 0)
);

CREATE TABLE dbo.InventoryMovements
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryMovements PRIMARY KEY,
    ProductId int NOT NULL CONSTRAINT FK_InventoryMovements_Products REFERENCES dbo.Products(Id),
    MovementType nvarchar(40) NOT NULL,
    Quantity decimal(18,2) NOT NULL,
    Unit nvarchar(30) NOT NULL,
    Responsible nvarchar(160) NOT NULL,
    Note nvarchar(300) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryMovements_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Promotions
(
    Id int IDENTITY(400,1) NOT NULL CONSTRAINT PK_Promotions PRIMARY KEY,
    Name nvarchar(120) NOT NULL,
    StartDate date NOT NULL,
    EndDate date NOT NULL,
    Discount decimal(5,4) NOT NULL,
    Active bit NOT NULL CONSTRAINT DF_Promotions_Active DEFAULT 1,
    CONSTRAINT CK_Promotions_Discount CHECK (Discount >= 0 AND Discount <= 1),
    CONSTRAINT CK_Promotions_Dates CHECK (EndDate >= StartDate)
);

CREATE TABLE dbo.Sales
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sales PRIMARY KEY,
    OrderId int NOT NULL CONSTRAINT FK_Sales_Orders REFERENCES dbo.Orders(Id),
    CustomerName nvarchar(160) NOT NULL,
    Subtotal decimal(18,2) NOT NULL,
    Tax decimal(18,2) NOT NULL,
    Total decimal(18,2) NOT NULL,
    PaymentMethod nvarchar(60) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Sales_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.CashSessions
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CashSessions PRIMARY KEY,
    OpenedBy nvarchar(160) NOT NULL,
    OpeningAmount decimal(18,2) NOT NULL,
    ClosingAmount decimal(18,2) NULL,
    Status nvarchar(30) NOT NULL CONSTRAINT DF_CashSessions_Status DEFAULT N'Abierta',
    OpenedAt datetime2(0) NOT NULL CONSTRAINT DF_CashSessions_OpenedAt DEFAULT SYSUTCDATETIME(),
    ClosedAt datetime2(0) NULL
);

CREATE TABLE dbo.AccountingEntries
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountingEntries PRIMARY KEY,
    EntryType nvarchar(60) NOT NULL,
    ReferenceId int NULL,
    AccountName nvarchar(160) NOT NULL,
    Debit decimal(18,2) NOT NULL CONSTRAINT DF_AccountingEntries_Debit DEFAULT 0,
    Credit decimal(18,2) NOT NULL CONSTRAINT DF_AccountingEntries_Credit DEFAULT 0,
    Balanced bit NOT NULL CONSTRAINT DF_AccountingEntries_Balanced DEFAULT 1,
    Note nvarchar(300) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AccountingEntries_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Expenses
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Expenses PRIMARY KEY,
    Category nvarchar(120) NOT NULL,
    Description nvarchar(220) NOT NULL,
    Amount decimal(18,2) NOT NULL,
    PaymentMethod nvarchar(60) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Expenses_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.SupplierPayments
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupplierPayments PRIMARY KEY,
    Supplier nvarchar(160) NOT NULL,
    Concept nvarchar(220) NOT NULL,
    Amount decimal(18,2) NOT NULL,
    DueDate date NOT NULL,
    Paid bit NOT NULL CONSTRAINT DF_SupplierPayments_Paid DEFAULT 0,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_SupplierPayments_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.GeoDestinations
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_GeoDestinations PRIMARY KEY,
    Code nvarchar(60) NOT NULL CONSTRAINT UQ_GeoDestinations_Code UNIQUE,
    Name nvarchar(160) NOT NULL,
    City nvarchar(100) NOT NULL,
    Country nvarchar(80) NOT NULL,
    Lat decimal(10,6) NOT NULL,
    Lng decimal(10,6) NOT NULL,
    Keywords nvarchar(500) NOT NULL,
    CONSTRAINT CK_GeoDestinations_Lat CHECK (Lat BETWEEN -90 AND 90),
    CONSTRAINT CK_GeoDestinations_Lng CHECK (Lng BETWEEN -180 AND 180)
);

CREATE TABLE dbo.Logs
(
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Logs PRIMARY KEY,
    LogType nvarchar(80) NOT NULL,
    Detail nvarchar(500) NOT NULL,
    Responsible nvarchar(160) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Logs_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.AppSettings
(
    SettingKey nvarchar(120) NOT NULL CONSTRAINT PK_AppSettings PRIMARY KEY,
    SettingValue nvarchar(max) NOT NULL
);
GO

INSERT INTO dbo.Roles (Name, Description, Permissions)
VALUES
(N'Admin', N'Acceso completo', N'usuarios,roles,inventario,pedidos,reportes,contabilidad,marketing,pos,produccion'),
(N'Staff', N'Operación diaria', N'inventario,pedidos,reportes,pos,produccion'),
(N'Cliente', N'Portal del cliente', N'perfil,mis-pedidos,seguimiento');

INSERT INTO dbo.Users (FirstName, LastName, Email, Phone, PasswordHash, Address, RoleId, Active, CreatedAt)
VALUES
(N'Admin', N'Demo', N'admin@demo.com', N'88881111', N'DEMO-PLAIN-1234', N'San José, Costa Rica', (SELECT Id FROM dbo.Roles WHERE Name = N'Admin'), 1, '2026-02-01T09:00:00'),
(N'Staff', N'Demo', N'staff@demo.com', N'88882222', N'DEMO-PLAIN-1234', N'Heredia, Costa Rica', (SELECT Id FROM dbo.Roles WHERE Name = N'Staff'), 1, '2026-02-02T09:00:00'),
(N'Cliente', N'Demo', N'cliente@demo.com', N'88883333', N'DEMO-PLAIN-1234', N'Escazú, San José', (SELECT Id FROM dbo.Roles WHERE Name = N'Cliente'), 1, '2026-02-03T09:00:00');

SET IDENTITY_INSERT dbo.Customers ON;
INSERT INTO dbo.Customers (Id, FullName, Email, Phone, Address, Frequent, TotalSpent, CreatedAt)
VALUES
(201, N'Cliente Demo', N'cliente@demo.com', N'88883333', N'Escazú, San José', 1, 96000, '2026-02-03T09:00:00'),
(202, N'María Gómez', N'maria@correo.com', N'88884444', N'Curridabat, San José', 1, 164000, '2026-02-04T09:00:00'),
(203, N'Ana Solís', N'ana@correo.com', N'88885555', N'Cartago Centro', 0, 45000, '2026-02-05T09:00:00'),
(204, N'Clínica Santa Ana', N'compras@clinicasantana.com', N'22225555', N'Santa Ana, San José', 1, 228000, '2026-02-06T09:00:00');
SET IDENTITY_INSERT dbo.Customers OFF;

SET IDENTITY_INSERT dbo.Products ON;
INSERT INTO dbo.Products (Id, Code, Description, ProductType, Unit, Category, Subcategory, Price, Stock, MinStock, Active, CreatedAt)
VALUES
(301, N'PAST-001', N'Cake Red Velvet 1.5kg', N'Producto terminado', N'unidad', N'Pasteles', N'Personalizado', 32000, 10, 3, 1, '2026-02-01T10:00:00'),
(302, N'PAST-002', N'Cheesecake Frutos Rojos', N'Producto terminado', N'unidad', N'Postres', N'Cheesecake', 28000, 8, 3, 1, '2026-02-01T10:10:00'),
(303, N'CUP-003', N'Cupcakes Caja 12', N'Producto terminado', N'caja', N'Cupcakes', N'Caja', 18000, 20, 6, 1, '2026-02-01T10:20:00'),
(304, N'GAL-004', N'Galletas Decoradas', N'Producto terminado', N'paquete', N'Galletas', N'Eventos', 12000, 25, 8, 1, '2026-02-01T10:30:00'),
(305, N'BROW-005', N'Brownie Gourmet', N'Producto terminado', N'unidad', N'Postres', N'Brownie', 8500, 0, 5, 1, '2026-02-01T10:40:00'),
(306, N'MP-HAR-001', N'Harina pastelera', N'Materia prima', N'kg', N'Ingredientes', N'Secos', 950, 42.5, 15, 1, '2026-02-01T10:50:00'),
(307, N'MP-AZU-001', N'Azúcar blanca', N'Materia prima', N'kg', N'Ingredientes', N'Secos', 780, 31, 12, 1, '2026-02-01T10:55:00'),
(308, N'MP-MAN-001', N'Mantequilla sin sal', N'Materia prima', N'kg', N'Ingredientes', N'Lácteos', 4200, 8.5, 6, 1, '2026-02-01T11:00:00'),
(309, N'EMP-CAJ-012', N'Caja para cupcakes x12', N'Empaque', N'unidad', N'Empaques', N'Cajas', 420, 60, 20, 1, '2026-02-01T11:05:00');
SET IDENTITY_INSERT dbo.Products OFF;

INSERT INTO dbo.GeoDestinations (Code, Name, City, Country, Lat, Lng, Keywords)
VALUES
(N'sagrada-familia', N'Sagrada Familia', N'San José', N'Costa Rica', 9.913900, -84.073700, N'sagrada familia,hatillo centro'),
(N'escuela', N'Centro Educativo El Carmelo', N'San José', N'Costa Rica', 9.916000, -84.070400, N'escuela,colegio,centro educativo,carmelo'),
(N'san-jose-centro', N'San José Centro', N'San José', N'Costa Rica', 9.932500, -84.079600, N'san jose centro,san josé centro,centro de san jose,avenida central'),
(N'escazu', N'Escazú Centro', N'Escazú', N'Costa Rica', 9.918700, -84.139900, N'escazu,escazú'),
(N'santa-ana', N'Santa Ana Centro', N'Santa Ana', N'Costa Rica', 9.932700, -84.182800, N'santa ana'),
(N'curridabat', N'Curridabat Centro', N'Curridabat', N'Costa Rica', 9.911700, -84.034200, N'curridabat'),
(N'cartago', N'Cartago Centro', N'Cartago', N'Costa Rica', 9.864400, -83.919400, N'cartago'),
(N'heredia', N'Heredia Centro', N'Heredia', N'Costa Rica', 9.998100, -84.116500, N'heredia'),
(N'alajuela', N'Alajuela Centro', N'Alajuela', N'Costa Rica', 10.016300, -84.211600, N'alajuela'),
(N'desamparados', N'Desamparados Centro', N'Desamparados', N'Costa Rica', 9.896900, -84.062000, N'desamparados'),
(N'pavas', N'Pavas', N'San José', N'Costa Rica', 9.949900, -84.133400, N'pavas,rohrmoser'),
(N'la-sabana', N'La Sabana', N'San José', N'Costa Rica', 9.936900, -84.106600, N'sabana,la sabana,estadio nacional'),
(N'moravia', N'Moravia Centro', N'Moravia', N'Costa Rica', 9.961400, -84.048800, N'moravia'),
(N'tibas', N'Tibás Centro', N'Tibás', N'Costa Rica', 9.960700, -84.078100, N'tibas,tibás'),
(N'guadalupe', N'Guadalupe', N'Goicoechea', N'Costa Rica', 9.947700, -84.056000, N'guadalupe,goicoechea'),
(N'panama-city', N'Ciudad de Panamá', N'Ciudad de Panamá', N'Panamá', 8.982400, -79.519900, N'panama,panamá,panama city,ciudad de panamá,ciudad de panama');

SET IDENTITY_INSERT dbo.Orders ON;
INSERT INTO dbo.Orders
(Id, CustomerId, Channel, Address, Notes, Status, PaymentStatus, PaymentMethod, Subtotal, Discount, Tax, Total, CreatedAt, DeliveryDate, CurrentLat, CurrentLng, DestinationLat, DestinationLng, DestinationCountry, DestinationLabel, RouteMode, TrackingStep, OriginLabel)
VALUES
(1012, 201, N'Web', N'Escazú, San José', N'Entregar antes de las 4 pm', N'En producción', N'Pagado', N'Tarjeta', 32000, 1600, 3952, 34352, '2026-02-20T15:30:00', '2026-03-27', 9.915010, -84.085370, 9.918700, -84.139900, N'Costa Rica', N'Escazú Centro', N'ground', 1, N'BakeSmart Patri · Sagrada Familia'),
(1014, 202, N'WhatsApp', N'Curridabat, San José', N'', N'Pendiente pago', N'Pendiente', N'SINPE', 28000, 0, 3640, 31640, '2026-02-22T18:00:00', '2026-03-28', 9.914200, -84.073400, 9.911700, -84.034200, N'Costa Rica', N'Curridabat Centro', N'ground', 0, N'BakeSmart Patri · Sagrada Familia'),
(1018, 204, N'Tienda', N'Santa Ana, San José', N'Evento corporativo', N'Confirmado', N'Pagado', N'Transferencia', 36000, 0, 4680, 40680, '2026-02-25T09:00:00', '2026-03-29', 9.921230, -84.114970, 9.932700, -84.182800, N'Costa Rica', N'Santa Ana Centro', N'ground', 2, N'BakeSmart Patri · Sagrada Familia');
SET IDENTITY_INSERT dbo.Orders OFF;

INSERT INTO dbo.OrderItems (OrderId, ProductId, Quantity, UnitPrice)
VALUES
(1012, 301, 1, 32000),
(1014, 302, 1, 28000),
(1018, 303, 2, 18000);

INSERT INTO dbo.InventoryMovements (ProductId, MovementType, Quantity, Unit, Responsible, Note, CreatedAt)
SELECT Id, N'CREACION', Stock, Unit, N'Sistema', N'Carga inicial', CreatedAt
FROM dbo.Products;

INSERT INTO dbo.Promotions (Name, StartDate, EndDate, Discount, Active)
VALUES
(N'Cliente frecuente', '2026-03-01', '2026-12-31', 0.0500, 1),
(N'Semana dulce', '2026-03-20', '2026-03-31', 0.1000, 1);

INSERT INTO dbo.Sales (OrderId, CustomerName, Subtotal, Tax, Total, PaymentMethod, CreatedAt)
SELECT o.Id, c.FullName, o.Subtotal, o.Tax, o.Total, o.PaymentMethod, o.CreatedAt
FROM dbo.Orders o
INNER JOIN dbo.Customers c ON c.Id = o.CustomerId
WHERE o.PaymentStatus = N'Pagado';

INSERT INTO dbo.AccountingEntries (EntryType, ReferenceId, AccountName, Debit, Credit, Balanced, Note, CreatedAt)
SELECT N'VENTA', s.OrderId, N'Ingresos por ventas', s.Total, s.Total, 1, CONCAT(N'Venta #', s.OrderId), s.CreatedAt
FROM dbo.Sales s;

INSERT INTO dbo.Expenses (Category, Description, Amount, PaymentMethod, CreatedAt)
VALUES
(N'Servicios', N'Electricidad del local', 42000, N'Transferencia', '2026-03-01T09:00:00'),
(N'Ingredientes', N'Compra de lácteos', 68500, N'SINPE', '2026-03-03T10:30:00');

INSERT INTO dbo.SupplierPayments (Supplier, Concept, Amount, DueDate, Paid, CreatedAt)
VALUES
(N'Distribuidora Central', N'Harina y secos', 128000, '2026-03-30', 0, '2026-03-10T09:00:00'),
(N'Lácteos del Valle', N'Mantequilla y crema', 94000, '2026-03-25', 1, '2026-03-08T11:00:00');

INSERT INTO dbo.CashSessions (OpenedBy, OpeningAmount, ClosingAmount, Status, OpenedAt, ClosedAt)
VALUES
(N'staff@demo.com', 50000, 186000, N'Cerrada', '2026-03-15T08:00:00', '2026-03-15T18:00:00');

INSERT INTO dbo.AppSettings (SettingKey, SettingValue)
VALUES
(N'iva', N'0.13'),
(N'frequentCustomerDiscount', N'0.05'),
(N'originName', N'BakeSmart Patri · Sagrada Familia'),
(N'originLat', N'9.9142'),
(N'originLng', N'-84.0734');

INSERT INTO dbo.Logs (LogType, Detail, Responsible, CreatedAt)
VALUES
(N'LOGIN', N'Inicio de sesión demo', N'Sistema', '2026-02-01T09:00:00'),
(N'CREACION_PRODUCTO', N'Carga inicial de inventario', N'Sistema', '2026-02-01T10:00:00'),
(N'CREAR_PEDIDO', N'Carga inicial de pedidos', N'Sistema', '2026-02-20T15:30:00'),
(N'GENERAR_REPORTE', N'Carga inicial de reportes', N'Sistema', '2026-03-01T12:00:00');
GO

CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);
CREATE INDEX IX_Orders_Status ON dbo.Orders(Status);
CREATE INDEX IX_Orders_CreatedAt ON dbo.Orders(CreatedAt);
CREATE INDEX IX_OrderItems_OrderId ON dbo.OrderItems(OrderId);
CREATE INDEX IX_Products_Stock ON dbo.Products(Stock, MinStock);
CREATE INDEX IX_InventoryMovements_ProductId ON dbo.InventoryMovements(ProductId);
CREATE INDEX IX_Sales_CreatedAt ON dbo.Sales(CreatedAt);
GO

SELECT
    (SELECT COUNT(*) FROM dbo.Users) AS UsersCount,
    (SELECT COUNT(*) FROM dbo.Customers) AS CustomersCount,
    (SELECT COUNT(*) FROM dbo.Products) AS ProductsCount,
    (SELECT COUNT(*) FROM dbo.Orders) AS OrdersCount,
    (SELECT COUNT(*) FROM dbo.GeoDestinations) AS GeoDestinationsCount;
GO
