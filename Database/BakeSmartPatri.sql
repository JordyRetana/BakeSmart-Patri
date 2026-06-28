/*
    BakeSmart Patri - SQL Server database
    Script normalizado para SQL Server.

    Ejecutar en SQL Server Management Studio o Azure Data Studio.
    Luego validar /api/health con la conexion configurada en appsettings.
*/

IF DB_ID(N'BakeSmartPatri') IS NULL
BEGIN
    CREATE DATABASE BakeSmartPatri;
END
GO

USE BakeSmartPatri;
GO

SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* Limpieza ordenada por dependencias. */
/* Tablas antiguas con nombres en ingles, por si la base ya existia antes del cambio. */
IF OBJECT_ID(N'dbo.AuditLogs', N'U') IS NOT NULL DROP TABLE dbo.AuditLogs;
IF OBJECT_ID(N'dbo.AppSettings', N'U') IS NOT NULL DROP TABLE dbo.AppSettings;
IF OBJECT_ID(N'dbo.SupplierPayments', N'U') IS NOT NULL DROP TABLE dbo.SupplierPayments;
IF OBJECT_ID(N'dbo.Suppliers', N'U') IS NOT NULL DROP TABLE dbo.Suppliers;
IF OBJECT_ID(N'dbo.Expenses', N'U') IS NOT NULL DROP TABLE dbo.Expenses;
IF OBJECT_ID(N'dbo.ExpenseCategories', N'U') IS NOT NULL DROP TABLE dbo.ExpenseCategories;
IF OBJECT_ID(N'dbo.AccountingEntryLines', N'U') IS NOT NULL DROP TABLE dbo.AccountingEntryLines;
IF OBJECT_ID(N'dbo.AccountingEntries', N'U') IS NOT NULL DROP TABLE dbo.AccountingEntries;
IF OBJECT_ID(N'dbo.ChartOfAccounts', N'U') IS NOT NULL DROP TABLE dbo.ChartOfAccounts;
IF OBJECT_ID(N'dbo.CashSessionPayments', N'U') IS NOT NULL DROP TABLE dbo.CashSessionPayments;
IF OBJECT_ID(N'dbo.CashSessions', N'U') IS NOT NULL DROP TABLE dbo.CashSessions;
IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL DROP TABLE dbo.Sales;
IF OBJECT_ID(N'dbo.PromotionProducts', N'U') IS NOT NULL DROP TABLE dbo.PromotionProducts;
IF OBJECT_ID(N'dbo.Promotions', N'U') IS NOT NULL DROP TABLE dbo.Promotions;
IF OBJECT_ID(N'dbo.InventoryMovements', N'U') IS NOT NULL DROP TABLE dbo.InventoryMovements;
IF OBJECT_ID(N'dbo.InventoryBalances', N'U') IS NOT NULL DROP TABLE dbo.InventoryBalances;
IF OBJECT_ID(N'dbo.InventoryLocations', N'U') IS NOT NULL DROP TABLE dbo.InventoryLocations;
IF OBJECT_ID(N'dbo.OrderTrackingEvents', N'U') IS NOT NULL DROP TABLE dbo.OrderTrackingEvents;
IF OBJECT_ID(N'dbo.OrderItems', N'U') IS NOT NULL DROP TABLE dbo.OrderItems;
IF OBJECT_ID(N'dbo.Orders', N'U') IS NOT NULL DROP TABLE dbo.Orders;
IF OBJECT_ID(N'dbo.PaymentMethods', N'U') IS NOT NULL DROP TABLE dbo.PaymentMethods;
IF OBJECT_ID(N'dbo.PaymentStatuses', N'U') IS NOT NULL DROP TABLE dbo.PaymentStatuses;
IF OBJECT_ID(N'dbo.OrderStatuses', N'U') IS NOT NULL DROP TABLE dbo.OrderStatuses;
IF OBJECT_ID(N'dbo.OrderChannels', N'U') IS NOT NULL DROP TABLE dbo.OrderChannels;
IF OBJECT_ID(N'dbo.ProductImages', N'U') IS NOT NULL DROP TABLE dbo.ProductImages;
IF OBJECT_ID(N'dbo.Products', N'U') IS NOT NULL DROP TABLE dbo.Products;
IF OBJECT_ID(N'dbo.ProductCategories', N'U') IS NOT NULL DROP TABLE dbo.ProductCategories;
IF OBJECT_ID(N'dbo.ProductTypes', N'U') IS NOT NULL DROP TABLE dbo.ProductTypes;
IF OBJECT_ID(N'dbo.UnitMeasures', N'U') IS NOT NULL DROP TABLE dbo.UnitMeasures;
IF OBJECT_ID(N'dbo.CustomerAddresses', N'U') IS NOT NULL DROP TABLE dbo.CustomerAddresses;
IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL DROP TABLE dbo.Customers;
IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL DROP TABLE dbo.Users;
IF OBJECT_ID(N'dbo.GeoDestinations', N'U') IS NOT NULL DROP TABLE dbo.GeoDestinations;
IF OBJECT_ID(N'dbo.Categories', N'U') IS NOT NULL DROP TABLE dbo.Categories;
IF OBJECT_ID(N'dbo.Logs', N'U') IS NOT NULL DROP TABLE dbo.Logs;

IF OBJECT_ID(N'dbo.BitacoraAuditoria', N'U') IS NOT NULL DROP TABLE dbo.BitacoraAuditoria;
IF OBJECT_ID(N'dbo.ConfiguracionesAplicacion', N'U') IS NOT NULL DROP TABLE dbo.ConfiguracionesAplicacion;
IF OBJECT_ID(N'dbo.PagosProveedor', N'U') IS NOT NULL DROP TABLE dbo.PagosProveedor;
IF OBJECT_ID(N'dbo.Proveedores', N'U') IS NOT NULL DROP TABLE dbo.Proveedores;
IF OBJECT_ID(N'dbo.Gastos', N'U') IS NOT NULL DROP TABLE dbo.Gastos;
IF OBJECT_ID(N'dbo.CategoriasGasto', N'U') IS NOT NULL DROP TABLE dbo.CategoriasGasto;
IF OBJECT_ID(N'dbo.LineasAsientoContable', N'U') IS NOT NULL DROP TABLE dbo.LineasAsientoContable;
IF OBJECT_ID(N'dbo.AsientosContables', N'U') IS NOT NULL DROP TABLE dbo.AsientosContables;
IF OBJECT_ID(N'dbo.CatalogoCuentas', N'U') IS NOT NULL DROP TABLE dbo.CatalogoCuentas;
IF OBJECT_ID(N'dbo.PagosSesionCaja', N'U') IS NOT NULL DROP TABLE dbo.PagosSesionCaja;
IF OBJECT_ID(N'dbo.SesionesCaja', N'U') IS NOT NULL DROP TABLE dbo.SesionesCaja;
IF OBJECT_ID(N'dbo.Ventas', N'U') IS NOT NULL DROP TABLE dbo.Ventas;
IF OBJECT_ID(N'dbo.ProductosPromocion', N'U') IS NOT NULL DROP TABLE dbo.ProductosPromocion;
IF OBJECT_ID(N'dbo.Promociones', N'U') IS NOT NULL DROP TABLE dbo.Promociones;
IF OBJECT_ID(N'dbo.MovimientosInventario', N'U') IS NOT NULL DROP TABLE dbo.MovimientosInventario;
IF OBJECT_ID(N'dbo.ExistenciasInventario', N'U') IS NOT NULL DROP TABLE dbo.ExistenciasInventario;
IF OBJECT_ID(N'dbo.UbicacionesInventario', N'U') IS NOT NULL DROP TABLE dbo.UbicacionesInventario;
IF OBJECT_ID(N'dbo.EventosSeguimientoPedido', N'U') IS NOT NULL DROP TABLE dbo.EventosSeguimientoPedido;
IF OBJECT_ID(N'dbo.DetallePedido', N'U') IS NOT NULL DROP TABLE dbo.DetallePedido;
IF OBJECT_ID(N'dbo.Pedidos', N'U') IS NOT NULL DROP TABLE dbo.Pedidos;
IF OBJECT_ID(N'dbo.MetodosPago', N'U') IS NOT NULL DROP TABLE dbo.MetodosPago;
IF OBJECT_ID(N'dbo.EstadosPago', N'U') IS NOT NULL DROP TABLE dbo.EstadosPago;
IF OBJECT_ID(N'dbo.EstadosPedido', N'U') IS NOT NULL DROP TABLE dbo.EstadosPedido;
IF OBJECT_ID(N'dbo.CanalesPedido', N'U') IS NOT NULL DROP TABLE dbo.CanalesPedido;
IF OBJECT_ID(N'dbo.ImagenesProducto', N'U') IS NOT NULL DROP TABLE dbo.ImagenesProducto;
IF OBJECT_ID(N'dbo.Productos', N'U') IS NOT NULL DROP TABLE dbo.Productos;
IF OBJECT_ID(N'dbo.CategoriasProducto', N'U') IS NOT NULL DROP TABLE dbo.CategoriasProducto;
IF OBJECT_ID(N'dbo.TiposProducto', N'U') IS NOT NULL DROP TABLE dbo.TiposProducto;
IF OBJECT_ID(N'dbo.UnidadesMedida', N'U') IS NOT NULL DROP TABLE dbo.UnidadesMedida;
IF OBJECT_ID(N'dbo.DireccionesCliente', N'U') IS NOT NULL DROP TABLE dbo.DireccionesCliente;
IF OBJECT_ID(N'dbo.Clientes', N'U') IS NOT NULL DROP TABLE dbo.Clientes;
IF OBJECT_ID(N'dbo.Usuarios', N'U') IS NOT NULL DROP TABLE dbo.Usuarios;
IF OBJECT_ID(N'dbo.Roles', N'U') IS NOT NULL DROP TABLE dbo.Roles;
IF OBJECT_ID(N'dbo.DestinosGeograficos', N'U') IS NOT NULL DROP TABLE dbo.DestinosGeograficos;
GO

CREATE TABLE dbo.Roles
(
    RoleId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    RoleName nvarchar(50) NOT NULL CONSTRAINT UQ_Roles_RoleName UNIQUE,
    Description nvarchar(200) NOT NULL,
    IsSystemRole bit NOT NULL CONSTRAINT DF_Roles_IsSystemRole DEFAULT 0
);

CREATE TABLE dbo.Usuarios
(
    UserId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    RoleId int NOT NULL CONSTRAINT FK_Users_Roles REFERENCES dbo.Roles(RoleId),
    FirstName nvarchar(80) NOT NULL,
    LastName nvarchar(80) NOT NULL,
    Email nvarchar(160) NOT NULL CONSTRAINT UQ_Users_Email UNIQUE,
    Phone nvarchar(40) NULL,
    PasswordHash nvarchar(300) NOT NULL,
    AddressLine nvarchar(300) NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT 1,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Clientes
(
    CustomerId int IDENTITY(200,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    UserId int NULL CONSTRAINT FK_Customers_Users REFERENCES dbo.Usuarios(UserId),
    FullName nvarchar(160) NOT NULL,
    Email nvarchar(160) NOT NULL CONSTRAINT UQ_Customers_Email UNIQUE,
    Phone nvarchar(40) NULL,
    IsFrequent bit NOT NULL CONSTRAINT DF_Customers_IsFrequent DEFAULT 0,
    TotalSpent decimal(18,2) NOT NULL CONSTRAINT DF_Customers_TotalSpent DEFAULT 0,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.DestinosGeograficos
(
    GeoDestinationId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_GeoDestinations PRIMARY KEY,
    Code nvarchar(60) NOT NULL CONSTRAINT UQ_GeoDestinations_Code UNIQUE,
    Name nvarchar(160) NOT NULL,
    City nvarchar(100) NOT NULL,
    Country nvarchar(80) NOT NULL,
    Latitude decimal(10,6) NOT NULL,
    Longitude decimal(10,6) NOT NULL,
    Keywords nvarchar(500) NOT NULL,
    CONSTRAINT CK_GeoDestinations_Latitude CHECK (Latitude BETWEEN -90 AND 90),
    CONSTRAINT CK_GeoDestinations_Longitude CHECK (Longitude BETWEEN -180 AND 180)
);

CREATE TABLE dbo.DireccionesCliente
(
    CustomerAddressId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CustomerAddresses PRIMARY KEY,
    CustomerId int NOT NULL CONSTRAINT FK_CustomerAddresses_Customers REFERENCES dbo.Clientes(CustomerId),
    GeoDestinationId int NULL CONSTRAINT FK_CustomerAddresses_GeoDestinations REFERENCES dbo.DestinosGeograficos(GeoDestinationId),
    Label nvarchar(120) NOT NULL,
    AddressLine nvarchar(300) NOT NULL,
    IsDefault bit NOT NULL CONSTRAINT DF_CustomerAddresses_IsDefault DEFAULT 0,
    Latitude decimal(10,6) NULL,
    Longitude decimal(10,6) NULL,
    Status nvarchar(20) NOT NULL CONSTRAINT DF_CustomerAddresses_Status DEFAULT N'Activa',
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_CustomerAddresses_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT CK_CustomerAddresses_Latitude CHECK (Latitude IS NULL OR Latitude BETWEEN -90 AND 90),
    CONSTRAINT CK_CustomerAddresses_Longitude CHECK (Longitude IS NULL OR Longitude BETWEEN -180 AND 180)
);

CREATE TABLE dbo.UnidadesMedida
(
    UnitMeasureId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_UnitMeasures PRIMARY KEY,
    Code nvarchar(20) NOT NULL CONSTRAINT UQ_UnitMeasures_Code UNIQUE,
    Name nvarchar(60) NOT NULL,
    AllowsDecimal bit NOT NULL CONSTRAINT DF_UnitMeasures_AllowsDecimal DEFAULT 1
);

CREATE TABLE dbo.TiposProducto
(
    ProductTypeId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductTypes PRIMARY KEY,
    Name nvarchar(80) NOT NULL CONSTRAINT UQ_ProductTypes_Name UNIQUE
);

CREATE TABLE dbo.CategoriasProducto
(
    ProductCategoryId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductCategories PRIMARY KEY,
    ParentCategoryId int NULL CONSTRAINT FK_ProductCategories_Parent REFERENCES dbo.CategoriasProducto(ProductCategoryId),
    Name nvarchar(100) NOT NULL,
    CONSTRAINT UQ_ProductCategories_Parent_Name UNIQUE (ParentCategoryId, Name)
);

CREATE TABLE dbo.Productos
(
    ProductId int IDENTITY(300,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    ProductTypeId int NOT NULL CONSTRAINT FK_Products_ProductTypes REFERENCES dbo.TiposProducto(ProductTypeId),
    ProductCategoryId int NOT NULL CONSTRAINT FK_Products_ProductCategories REFERENCES dbo.CategoriasProducto(ProductCategoryId),
    UnitMeasureId int NOT NULL CONSTRAINT FK_Products_UnitMeasures REFERENCES dbo.UnidadesMedida(UnitMeasureId),
    Code nvarchar(40) NOT NULL CONSTRAINT UQ_Products_Code UNIQUE,
    Name nvarchar(160) NOT NULL,
    Description nvarchar(500) NULL,
    UnitPrice decimal(18,2) NOT NULL CONSTRAINT DF_Products_UnitPrice DEFAULT 0,
    UnitCost decimal(18,2) NOT NULL CONSTRAINT DF_Products_UnitCost DEFAULT 0,
    MinStock decimal(18,2) NOT NULL CONSTRAINT DF_Products_MinStock DEFAULT 0,
    IsActive bit NOT NULL CONSTRAINT DF_Products_IsActive DEFAULT 1,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Products_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Products_UnitPrice CHECK (UnitPrice >= 0),
    CONSTRAINT CK_Products_UnitCost CHECK (UnitCost >= 0),
    CONSTRAINT CK_Products_MinStock CHECK (MinStock >= 0)
);

CREATE TABLE dbo.ImagenesProducto
(
    ProductImageId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductImages PRIMARY KEY,
    ProductId int NOT NULL CONSTRAINT FK_ProductImages_Products REFERENCES dbo.Productos(ProductId) ON DELETE CASCADE,
    ImageUrl nvarchar(600) NOT NULL,
    AltText nvarchar(200) NOT NULL,
    SortOrder int NOT NULL CONSTRAINT DF_ProductImages_SortOrder DEFAULT 1,
    IsPrimary bit NOT NULL CONSTRAINT DF_ProductImages_IsPrimary DEFAULT 0
);

CREATE TABLE dbo.UbicacionesInventario
(
    InventoryLocationId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryLocations PRIMARY KEY,
    Name nvarchar(100) NOT NULL CONSTRAINT UQ_InventoryLocations_Name UNIQUE,
    Description nvarchar(200) NULL
);

CREATE TABLE dbo.ExistenciasInventario
(
    ProductId int NOT NULL CONSTRAINT FK_InventoryBalances_Products REFERENCES dbo.Productos(ProductId),
    InventoryLocationId int NOT NULL CONSTRAINT FK_InventoryBalances_Locations REFERENCES dbo.UbicacionesInventario(InventoryLocationId),
    Quantity decimal(18,2) NOT NULL CONSTRAINT DF_InventoryBalances_Quantity DEFAULT 0,
    UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryBalances_UpdatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_InventoryBalances PRIMARY KEY (ProductId, InventoryLocationId),
    CONSTRAINT CK_InventoryBalances_Quantity CHECK (Quantity >= 0)
);

CREATE TABLE dbo.MovimientosInventario
(
    InventoryMovementId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryMovements PRIMARY KEY,
    ProductId int NOT NULL CONSTRAINT FK_InventoryMovements_Products REFERENCES dbo.Productos(ProductId),
    InventoryLocationId int NOT NULL CONSTRAINT FK_InventoryMovements_Locations REFERENCES dbo.UbicacionesInventario(InventoryLocationId),
    MovementType nvarchar(40) NOT NULL,
    Quantity decimal(18,2) NOT NULL,
    ResponsibleUserId int NULL CONSTRAINT FK_InventoryMovements_Users REFERENCES dbo.Usuarios(UserId),
    Note nvarchar(300) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryMovements_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_InventoryMovements_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_InventoryMovements_Type CHECK (MovementType IN (N'ENTRADA', N'SALIDA', N'AJUSTE', N'CREACION'))
);

CREATE TABLE dbo.CanalesPedido
(
    OrderChannelId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderChannels PRIMARY KEY,
    Name nvarchar(60) NOT NULL CONSTRAINT UQ_OrderChannels_Name UNIQUE
);

CREATE TABLE dbo.EstadosPedido
(
    OrderStatusId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderStatuses PRIMARY KEY,
    Name nvarchar(60) NOT NULL CONSTRAINT UQ_OrderStatuses_Name UNIQUE,
    SortOrder int NOT NULL
);

CREATE TABLE dbo.EstadosPago
(
    PaymentStatusId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PaymentStatuses PRIMARY KEY,
    Name nvarchar(60) NOT NULL CONSTRAINT UQ_PaymentStatuses_Name UNIQUE
);

CREATE TABLE dbo.MetodosPago
(
    PaymentMethodId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PaymentMethods PRIMARY KEY,
    Name nvarchar(60) NOT NULL CONSTRAINT UQ_PaymentMethods_Name UNIQUE,
    CommissionRate decimal(8,4) NOT NULL CONSTRAINT DF_PaymentMethods_Commission DEFAULT 0,
    IsActive bit NOT NULL CONSTRAINT DF_PaymentMethods_IsActive DEFAULT 1,
    CONSTRAINT CK_PaymentMethods_Commission CHECK (CommissionRate >= 0 AND CommissionRate <= 1)
);

CREATE TABLE dbo.Pedidos
(
    OrderId int IDENTITY(1000,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Clientes(CustomerId),
    CustomerAddressId int NULL CONSTRAINT FK_Orders_CustomerAddresses REFERENCES dbo.DireccionesCliente(CustomerAddressId),
    OrderChannelId int NOT NULL CONSTRAINT FK_Orders_OrderChannels REFERENCES dbo.CanalesPedido(OrderChannelId),
    OrderStatusId int NOT NULL CONSTRAINT FK_Orders_OrderStatuses REFERENCES dbo.EstadosPedido(OrderStatusId),
    PaymentStatusId int NOT NULL CONSTRAINT FK_Orders_PaymentStatuses REFERENCES dbo.EstadosPago(PaymentStatusId),
    PaymentMethodId int NOT NULL CONSTRAINT FK_Orders_PaymentMethods REFERENCES dbo.MetodosPago(PaymentMethodId),
    Notes nvarchar(500) NULL,
    Subtotal decimal(18,2) NOT NULL,
    Discount decimal(18,2) NOT NULL CONSTRAINT DF_Orders_Discount DEFAULT 0,
    Tax decimal(18,2) NOT NULL CONSTRAINT DF_Orders_Tax DEFAULT 0,
    Total decimal(18,2) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Orders_CreatedAt DEFAULT SYSUTCDATETIME(),
    DeliveryDate date NOT NULL,
    CurrentLatitude decimal(10,6) NOT NULL,
    CurrentLongitude decimal(10,6) NOT NULL,
    DestinationLatitude decimal(10,6) NOT NULL,
    DestinationLongitude decimal(10,6) NOT NULL,
    DestinationLabel nvarchar(160) NOT NULL,
    DestinationCountry nvarchar(80) NOT NULL,
    RouteMode nvarchar(20) NOT NULL,
    TrackingStep int NOT NULL CONSTRAINT DF_Orders_TrackingStep DEFAULT 0,
    OriginLabel nvarchar(160) NOT NULL,
    DeliveryReference nvarchar(250) NULL,
    CONSTRAINT CK_Orders_Amounts CHECK (Subtotal >= 0 AND Discount >= 0 AND Tax >= 0 AND Total >= 0),
    CONSTRAINT CK_Orders_CurrentLatitude CHECK (CurrentLatitude BETWEEN -90 AND 90),
    CONSTRAINT CK_Orders_CurrentLongitude CHECK (CurrentLongitude BETWEEN -180 AND 180),
    CONSTRAINT CK_Orders_DestinationLatitude CHECK (DestinationLatitude BETWEEN -90 AND 90),
    CONSTRAINT CK_Orders_DestinationLongitude CHECK (DestinationLongitude BETWEEN -180 AND 180)
);

CREATE TABLE dbo.DetallePedido
(
    OrderItemId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
    OrderId int NOT NULL CONSTRAINT FK_OrderItems_Orders REFERENCES dbo.Pedidos(OrderId) ON DELETE CASCADE,
    ProductId int NOT NULL CONSTRAINT FK_OrderItems_Products REFERENCES dbo.Productos(ProductId),
    Quantity decimal(18,2) NOT NULL,
    UnitPrice decimal(18,2) NOT NULL,
    LineTotal AS CAST(Quantity * UnitPrice AS decimal(18,2)) PERSISTED,
    CONSTRAINT CK_OrderItems_Quantity CHECK (Quantity > 0),
    CONSTRAINT CK_OrderItems_UnitPrice CHECK (UnitPrice >= 0)
);

CREATE TABLE dbo.EventosSeguimientoPedido
(
    OrderTrackingEventId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderTrackingEvents PRIMARY KEY,
    OrderId int NOT NULL CONSTRAINT FK_OrderTrackingEvents_Orders REFERENCES dbo.Pedidos(OrderId) ON DELETE CASCADE,
    OrderStatusId int NOT NULL CONSTRAINT FK_OrderTrackingEvents_Statuses REFERENCES dbo.EstadosPedido(OrderStatusId),
    Latitude decimal(10,6) NULL,
    Longitude decimal(10,6) NULL,
    Detail nvarchar(250) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_OrderTrackingEvents_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.Promociones
(
    PromotionId int IDENTITY(400,1) NOT NULL CONSTRAINT PK_Promotions PRIMARY KEY,
    Name nvarchar(120) NOT NULL,
    StartDate date NOT NULL,
    EndDate date NOT NULL,
    DiscountRate decimal(5,4) NOT NULL,
    IsActive bit NOT NULL CONSTRAINT DF_Promotions_IsActive DEFAULT 1,
    CONSTRAINT CK_Promotions_DiscountRate CHECK (DiscountRate >= 0 AND DiscountRate <= 1),
    CONSTRAINT CK_Promotions_Dates CHECK (EndDate >= StartDate)
);

CREATE TABLE dbo.ProductosPromocion
(
    PromotionId int NOT NULL CONSTRAINT FK_PromotionProducts_Promotions REFERENCES dbo.Promociones(PromotionId) ON DELETE CASCADE,
    ProductId int NOT NULL CONSTRAINT FK_PromotionProducts_Products REFERENCES dbo.Productos(ProductId) ON DELETE CASCADE,
    CONSTRAINT PK_PromotionProducts PRIMARY KEY (PromotionId, ProductId)
);

CREATE TABLE dbo.Ventas
(
    SaleId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sales PRIMARY KEY,
    OrderId int NOT NULL CONSTRAINT FK_Sales_Orders REFERENCES dbo.Pedidos(OrderId),
    PaymentMethodId int NOT NULL CONSTRAINT FK_Sales_PaymentMethods REFERENCES dbo.MetodosPago(PaymentMethodId),
    Subtotal decimal(18,2) NOT NULL,
    Tax decimal(18,2) NOT NULL,
    Total decimal(18,2) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Sales_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.SesionesCaja
(
    CashSessionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CashSessions PRIMARY KEY,
    OpenedByUserId int NOT NULL CONSTRAINT FK_CashSessions_Users REFERENCES dbo.Usuarios(UserId),
    OpeningAmount decimal(18,2) NOT NULL,
    ClosingAmount decimal(18,2) NULL,
    Status nvarchar(30) NOT NULL CONSTRAINT DF_CashSessions_Status DEFAULT N'Abierta',
    OpenedAt datetime2(0) NOT NULL CONSTRAINT DF_CashSessions_OpenedAt DEFAULT SYSUTCDATETIME(),
    ClosedAt datetime2(0) NULL
);

CREATE TABLE dbo.PagosSesionCaja
(
    CashSessionPaymentId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CashSessionPayments PRIMARY KEY,
    CashSessionId int NOT NULL CONSTRAINT FK_CashSessionPayments_CashSessions REFERENCES dbo.SesionesCaja(CashSessionId),
    SaleId int NOT NULL CONSTRAINT FK_CashSessionPayments_Sales REFERENCES dbo.Ventas(SaleId),
    Amount decimal(18,2) NOT NULL
);

CREATE TABLE dbo.CatalogoCuentas
(
    AccountId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChartOfAccounts PRIMARY KEY,
    AccountCode nvarchar(30) NOT NULL CONSTRAINT UQ_ChartOfAccounts_Code UNIQUE,
    AccountName nvarchar(160) NOT NULL,
    AccountType nvarchar(40) NOT NULL
);

CREATE TABLE dbo.AsientosContables
(
    AccountingEntryId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountingEntries PRIMARY KEY,
    EntryType nvarchar(60) NOT NULL,
    ReferenceTable nvarchar(80) NULL,
    ReferenceId int NULL,
    Note nvarchar(300) NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AccountingEntries_CreatedAt DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.LineasAsientoContable
(
    AccountingEntryLineId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountingEntryLines PRIMARY KEY,
    AccountingEntryId int NOT NULL CONSTRAINT FK_AccountingEntryLines_Entries REFERENCES dbo.AsientosContables(AccountingEntryId) ON DELETE CASCADE,
    AccountId int NOT NULL CONSTRAINT FK_AccountingEntryLines_Accounts REFERENCES dbo.CatalogoCuentas(AccountId),
    Debit decimal(18,2) NOT NULL CONSTRAINT DF_AccountingEntryLines_Debit DEFAULT 0,
    Credit decimal(18,2) NOT NULL CONSTRAINT DF_AccountingEntryLines_Credit DEFAULT 0,
    CONSTRAINT CK_AccountingEntryLines_Amounts CHECK (Debit >= 0 AND Credit >= 0 AND (Debit > 0 OR Credit > 0))
);

CREATE TABLE dbo.CategoriasGasto
(
    ExpenseCategoryId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExpenseCategories PRIMARY KEY,
    Name nvarchar(120) NOT NULL CONSTRAINT UQ_ExpenseCategories_Name UNIQUE
);

CREATE TABLE dbo.Gastos
(
    ExpenseId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Expenses PRIMARY KEY,
    ExpenseCategoryId int NOT NULL CONSTRAINT FK_Expenses_Categories REFERENCES dbo.CategoriasGasto(ExpenseCategoryId),
    PaymentMethodId int NOT NULL CONSTRAINT FK_Expenses_PaymentMethods REFERENCES dbo.MetodosPago(PaymentMethodId),
    Description nvarchar(220) NOT NULL,
    Amount decimal(18,2) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Expenses_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Expenses_Amount CHECK (Amount > 0)
);

CREATE TABLE dbo.Proveedores
(
    SupplierId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Suppliers PRIMARY KEY,
    Name nvarchar(160) NOT NULL CONSTRAINT UQ_Suppliers_Name UNIQUE,
    Phone nvarchar(40) NULL,
    Email nvarchar(160) NULL
);

CREATE TABLE dbo.PagosProveedor
(
    SupplierPaymentId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupplierPayments PRIMARY KEY,
    SupplierId int NOT NULL CONSTRAINT FK_SupplierPayments_Suppliers REFERENCES dbo.Proveedores(SupplierId),
    PaymentMethodId int NOT NULL CONSTRAINT FK_SupplierPayments_PaymentMethods REFERENCES dbo.MetodosPago(PaymentMethodId),
    Concept nvarchar(220) NOT NULL,
    Amount decimal(18,2) NOT NULL,
    DueDate date NOT NULL,
    PaidAt date NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_SupplierPayments_CreatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_SupplierPayments_Amount CHECK (Amount > 0)
);

CREATE TABLE dbo.ConfiguracionesAplicacion
(
    SettingKey nvarchar(120) NOT NULL CONSTRAINT PK_AppSettings PRIMARY KEY,
    SettingValue nvarchar(max) NOT NULL
);

CREATE TABLE dbo.BitacoraAuditoria
(
    AuditLogId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLogs PRIMARY KEY,
    UserId int NULL CONSTRAINT FK_AuditLogs_Users REFERENCES dbo.Usuarios(UserId),
    LogType nvarchar(80) NOT NULL,
    Detail nvarchar(500) NOT NULL,
    CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_AuditLogs_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

INSERT INTO dbo.Roles (RoleName, Description, IsSystemRole)
SELECT N'Admin', N'Acceso completo', 1
UNION ALL SELECT N'Staff', N'Operacion diaria', 1
UNION ALL SELECT N'Cliente', N'Portal del cliente', 1
UNION ALL SELECT N'Cajero', N'Gestion de caja, ventas y pedidos de mostrador', 1
UNION ALL SELECT N'Repostero', N'Produccion, recetas e inventario operativo', 1
UNION ALL SELECT N'Supervisor', N'Seguimiento operativo, reportes y control de tienda', 1;

INSERT INTO dbo.Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
VALUES (1, N'Administrador', N'Principal', N'admin@demo.com', N'88881111', N'PBKDF2-SHA256$120000$ZAZVwhLsAp7QiXkwG/P+XA==$IQ+vDnKlWZgKUOMRJJjJ42G5POH2nLuPEkwfq1AnM/4=', N'San Jose, Costa Rica', 1, '2026-02-01T09:00:00');
INSERT INTO dbo.Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
VALUES (2, N'Operador', N'Principal', N'staff@demo.com', N'88882222', N'PBKDF2-SHA256$120000$BxlfDfT5g4xUTwXWYOsSHg==$taD51EB8hMgiXUgOq7rqEXscvvRehWuUCX35nbeEX3E=', N'Heredia, Costa Rica', 1, '2026-02-02T09:00:00');
INSERT INTO dbo.Usuarios (RoleId, FirstName, LastName, Email, Phone, PasswordHash, AddressLine, IsActive, CreatedAt)
VALUES (3, N'Cliente', N'Inicial', N'cliente@demo.com', N'88883333', N'PBKDF2-SHA256$120000$B5oOn5mgAYeDXj3I8tLABg==$iAOPbfRDma8GTxCHXrqvS2m9wLQe2nxw13TmTQiA75A=', N'Escazu, San Jose', 1, '2026-02-03T09:00:00');

INSERT INTO dbo.DestinosGeograficos (Code, Name, City, Country, Latitude, Longitude, Keywords)
SELECT N'sagrada-familia', N'Sagrada Familia', N'San Jose', N'Costa Rica', 9.913900, -84.073700, N'sagrada familia,hatillo centro'
UNION ALL SELECT N'escuela', N'Centro Educativo El Carmelo', N'San Jose', N'Costa Rica', 9.916000, -84.070400, N'escuela,colegio,centro educativo,carmelo'
UNION ALL SELECT N'san-jose-centro', N'San Jose Centro', N'San Jose', N'Costa Rica', 9.932500, -84.079600, N'san jose centro,centro de san jose,avenida central'
UNION ALL SELECT N'escazu', N'Escazu Centro', N'Escazu', N'Costa Rica', 9.918700, -84.139900, N'escazu'
UNION ALL SELECT N'santa-ana', N'Santa Ana Centro', N'Santa Ana', N'Costa Rica', 9.932700, -84.182800, N'santa ana'
UNION ALL SELECT N'curridabat', N'Curridabat Centro', N'Curridabat', N'Costa Rica', 9.911700, -84.034200, N'curridabat'
UNION ALL SELECT N'cartago', N'Cartago Centro', N'Cartago', N'Costa Rica', 9.864400, -83.919400, N'cartago'
UNION ALL SELECT N'heredia', N'Heredia Centro', N'Heredia', N'Costa Rica', 9.998100, -84.116500, N'heredia'
UNION ALL SELECT N'alajuela', N'Alajuela Centro', N'Alajuela', N'Costa Rica', 10.016300, -84.211600, N'alajuela'
UNION ALL SELECT N'desamparados', N'Desamparados Centro', N'Desamparados', N'Costa Rica', 9.896900, -84.062000, N'desamparados'
UNION ALL SELECT N'pavas', N'Pavas', N'San Jose', N'Costa Rica', 9.949900, -84.133400, N'pavas,rohrmoser'
UNION ALL SELECT N'la-sabana', N'La Sabana', N'San Jose', N'Costa Rica', 9.936900, -84.106600, N'sabana,la sabana,estadio nacional'
UNION ALL SELECT N'moravia', N'Moravia Centro', N'Moravia', N'Costa Rica', 9.961400, -84.048800, N'moravia'
UNION ALL SELECT N'tibas', N'Tibas Centro', N'Tibas', N'Costa Rica', 9.960700, -84.078100, N'tibas'
UNION ALL SELECT N'guadalupe', N'Guadalupe', N'Goicoechea', N'Costa Rica', 9.947700, -84.056000, N'guadalupe,goicoechea'
UNION ALL SELECT N'panama-city', N'Ciudad de Panama', N'Ciudad de Panama', N'Panama', 8.982400, -79.519900, N'panama,panama city,ciudad de panama';

SET IDENTITY_INSERT dbo.Clientes ON;
INSERT INTO dbo.Clientes (CustomerId, UserId, FullName, Email, Phone, IsFrequent, TotalSpent, CreatedAt)
SELECT 201, 3, N'Cliente Inicial', N'cliente@demo.com', N'88883333', 1, 96000, '2026-02-03T09:00:00'
UNION ALL SELECT 202, NULL, N'Maria Gomez', N'maria@correo.com', N'88884444', 1, 164000, '2026-02-04T09:00:00'
UNION ALL SELECT 203, NULL, N'Ana Solis', N'ana@correo.com', N'88885555', 0, 45000, '2026-02-05T09:00:00'
UNION ALL SELECT 204, NULL, N'Clinica Santa Ana', N'compras@clinicasantana.com', N'22225555', 1, 228000, '2026-02-06T09:00:00';
SET IDENTITY_INSERT dbo.Clientes OFF;

INSERT INTO dbo.DireccionesCliente (CustomerId, GeoDestinationId, Label, AddressLine, IsDefault, Latitude, Longitude)
SELECT 201, 4, N'Casa', N'Escazu, San Jose', 1, 9.918700, -84.139900
UNION ALL SELECT 202, 6, N'Casa', N'Curridabat, San Jose', 1, 9.911700, -84.034200
UNION ALL SELECT 203, 7, N'Casa', N'Cartago Centro', 1, 9.864400, -83.919400
UNION ALL SELECT 204, 5, N'Oficina', N'Santa Ana, San Jose', 1, 9.932700, -84.182800;

INSERT INTO dbo.UnidadesMedida (Code, Name, AllowsDecimal)
SELECT N'und', N'Unidad', 0
UNION ALL SELECT N'kg', N'Kilogramo', 1
UNION ALL SELECT N'caja', N'Caja', 0
UNION ALL SELECT N'paq', N'Paquete', 0;

INSERT INTO dbo.TiposProducto (Name)
VALUES (N'Producto terminado'), (N'Materia prima'), (N'Empaque');

INSERT INTO dbo.CategoriasProducto (ParentCategoryId, Name)
SELECT NULL, N'Pasteles'
UNION ALL SELECT NULL, N'Postres'
UNION ALL SELECT NULL, N'Cupcakes'
UNION ALL SELECT NULL, N'Galletas'
UNION ALL SELECT NULL, N'Ingredientes'
UNION ALL SELECT NULL, N'Empaques';

INSERT INTO dbo.CategoriasProducto (ParentCategoryId, Name)
SELECT 1, N'Personalizado'
UNION ALL SELECT 2, N'Cheesecake'
UNION ALL SELECT 2, N'Brownie'
UNION ALL SELECT 3, N'Caja'
UNION ALL SELECT 4, N'Eventos'
UNION ALL SELECT 5, N'Secos'
UNION ALL SELECT 5, N'Lacteos'
UNION ALL SELECT 6, N'Cajas';

SET IDENTITY_INSERT dbo.Productos ON;
INSERT INTO dbo.Productos (ProductId, ProductTypeId, ProductCategoryId, UnitMeasureId, Code, Name, Description, UnitPrice, UnitCost, MinStock, IsActive, CreatedAt)
SELECT 301, 1, 7, 1, N'PAST-001', N'Cake Red Velvet 1.5kg', N'Pastel personalizado de red velvet con cobertura premium.', 32000, 18000, 3, 1, '2026-02-01T10:00:00'
UNION ALL SELECT 302, 1, 8, 1, N'PAST-002', N'Cheesecake Frutos Rojos', N'Cheesecake con salsa de frutos rojos.', 28000, 15500, 3, 1, '2026-02-01T10:10:00'
UNION ALL SELECT 303, 1, 10, 3, N'CUP-003', N'Cupcakes Caja 12', N'Caja de 12 cupcakes decorados.', 18000, 9000, 6, 1, '2026-02-01T10:20:00'
UNION ALL SELECT 304, 1, 11, 4, N'GAL-004', N'Galletas Decoradas', N'Paquete de galletas decoradas para eventos.', 12000, 5200, 8, 1, '2026-02-01T10:30:00'
UNION ALL SELECT 305, 1, 9, 1, N'BROW-005', N'Brownie Gourmet', N'Brownie con chocolate premium.', 8500, 3400, 5, 1, '2026-02-01T10:40:00'
UNION ALL SELECT 306, 2, 12, 2, N'MP-HAR-001', N'Harina pastelera', N'Harina para produccion de reposteria.', 950, 950, 15, 1, '2026-02-01T10:50:00'
UNION ALL SELECT 307, 2, 12, 2, N'MP-AZU-001', N'Azucar blanca', N'Azucar blanca para produccion.', 780, 780, 12, 1, '2026-02-01T10:55:00'
UNION ALL SELECT 308, 2, 13, 2, N'MP-MAN-001', N'Mantequilla sin sal', N'Mantequilla sin sal para produccion.', 4200, 4200, 6, 1, '2026-02-01T11:00:00'
UNION ALL SELECT 309, 3, 14, 1, N'EMP-CAJ-012', N'Caja para cupcakes x12', N'Caja de empaque para cupcakes.', 420, 420, 20, 1, '2026-02-01T11:05:00';
SET IDENTITY_INSERT dbo.Productos OFF;

INSERT INTO dbo.ImagenesProducto (ProductId, ImageUrl, AltText, SortOrder, IsPrimary)
SELECT 301, N'/img/products/cake-red-velvet.jpg', N'Cake Red Velvet', 1, 1
UNION ALL SELECT 301, N'/img/products/pastel-decorado.jpg', N'Pastel decorado', 2, 0
UNION ALL SELECT 302, N'/img/products/cheesecake-frutos-rojos.jpg', N'Cheesecake de frutos rojos', 1, 1
UNION ALL SELECT 303, N'/img/products/cupcakes-decorados.jpg', N'Cupcakes decorados', 1, 1
UNION ALL SELECT 304, N'/img/products/galletas-decoradas.jpg', N'Galletas decoradas', 1, 1
UNION ALL SELECT 305, N'/img/products/brownie-gourmet.jpg', N'Brownie gourmet', 1, 1;

INSERT INTO dbo.UbicacionesInventario (Name, Description)
SELECT N'Bodega principal', N'Insumos y producto terminado del local'
UNION ALL SELECT N'Vitrina/POS', N'Producto disponible para venta directa';

INSERT INTO dbo.ExistenciasInventario (ProductId, InventoryLocationId, Quantity)
SELECT 301, 1, 10
UNION ALL SELECT 302, 1, 8
UNION ALL SELECT 303, 1, 20
UNION ALL SELECT 304, 1, 25
UNION ALL SELECT 305, 1, 0
UNION ALL SELECT 306, 1, 42.5
UNION ALL SELECT 307, 1, 31
UNION ALL SELECT 308, 1, 8.5
UNION ALL SELECT 309, 1, 60;

INSERT INTO dbo.MovimientosInventario (ProductId, InventoryLocationId, MovementType, Quantity, ResponsibleUserId, Note, CreatedAt)
SELECT ProductId, InventoryLocationId, N'CREACION', Quantity, 1, N'Carga inicial normalizada', '2026-02-01T10:00:00'
FROM dbo.ExistenciasInventario
WHERE Quantity > 0;

INSERT INTO dbo.CanalesPedido (Name)
VALUES (N'Web'), (N'WhatsApp'), (N'Tienda'), (N'Instagram'), (N'POS');

INSERT INTO dbo.EstadosPedido (Name, SortOrder)
SELECT N'Pendiente pago', 1
UNION ALL SELECT N'Confirmado', 2
UNION ALL SELECT N'En produccion', 3
UNION ALL SELECT N'Listo', 4
UNION ALL SELECT N'En camino', 5
UNION ALL SELECT N'Entregado', 6;

INSERT INTO dbo.EstadosPago (Name)
VALUES (N'Pendiente'), (N'Pagado'), (N'Anulado');

INSERT INTO dbo.MetodosPago (Name, CommissionRate, IsActive)
SELECT N'Efectivo', 0, 1
UNION ALL SELECT N'SINPE', 0, 1
UNION ALL SELECT N'Tarjeta', 0.0250, 1
UNION ALL SELECT N'Transferencia', 0, 1
UNION ALL SELECT N'Pendiente', 0, 1;

SET IDENTITY_INSERT dbo.Pedidos ON;
INSERT INTO dbo.Pedidos
(OrderId, CustomerId, CustomerAddressId, OrderChannelId, OrderStatusId, PaymentStatusId, PaymentMethodId, Notes, Subtotal, Discount, Tax, Total, CreatedAt, DeliveryDate, CurrentLatitude, CurrentLongitude, DestinationLatitude, DestinationLongitude, DestinationLabel, DestinationCountry, RouteMode, TrackingStep, OriginLabel)
SELECT 1012, 201, 1, 1, 3, 2, 3, N'Entregar antes de las 4 pm', 32000, 1600, 3952, 34352, '2026-02-20T15:30:00', '2026-03-27', 9.915010, -84.085370, 9.918700, -84.139900, N'Escazu Centro', N'Costa Rica', N'ground', 1, N'BakeSmart Patri - Sagrada Familia'
UNION ALL SELECT 1014, 202, 2, 2, 1, 1, 2, N'', 28000, 0, 3640, 31640, '2026-02-22T18:00:00', '2026-03-28', 9.914200, -84.073400, 9.911700, -84.034200, N'Curridabat Centro', N'Costa Rica', N'ground', 0, N'BakeSmart Patri - Sagrada Familia'
UNION ALL SELECT 1018, 204, 4, 3, 2, 2, 4, N'Evento corporativo', 36000, 0, 4680, 40680, '2026-02-25T09:00:00', '2026-03-29', 9.921230, -84.114970, 9.932700, -84.182800, N'Santa Ana Centro', N'Costa Rica', N'ground', 2, N'BakeSmart Patri - Sagrada Familia';
SET IDENTITY_INSERT dbo.Pedidos OFF;

INSERT INTO dbo.DetallePedido (OrderId, ProductId, Quantity, UnitPrice)
SELECT 1012, 301, 1, 32000
UNION ALL SELECT 1014, 302, 1, 28000
UNION ALL SELECT 1018, 303, 2, 18000;

INSERT INTO dbo.EventosSeguimientoPedido (OrderId, OrderStatusId, Latitude, Longitude, Detail, CreatedAt)
SELECT OrderId, OrderStatusId, CurrentLatitude, CurrentLongitude, N'Estado inicial del pedido', CreatedAt
FROM dbo.Pedidos;

INSERT INTO dbo.Promociones (Name, StartDate, EndDate, DiscountRate, IsActive)
SELECT N'Cliente frecuente', '2026-03-01', '2026-12-31', 0.0500, 1
UNION ALL SELECT N'Semana dulce', '2026-03-20', '2026-03-31', 0.1000, 1;

INSERT INTO dbo.ProductosPromocion (PromotionId, ProductId)
SELECT p.PromotionId, pr.ProductId
FROM dbo.Promociones p
CROSS JOIN dbo.Productos pr
WHERE p.Name = N'Semana dulce'
  AND pr.ProductTypeId = 1;

INSERT INTO dbo.Ventas (OrderId, PaymentMethodId, Subtotal, Tax, Total, CreatedAt)
SELECT o.OrderId, o.PaymentMethodId, o.Subtotal, o.Tax, o.Total, o.CreatedAt
FROM dbo.Pedidos o
INNER JOIN dbo.EstadosPago ps ON ps.PaymentStatusId = o.PaymentStatusId
WHERE ps.Name = N'Pagado';

INSERT INTO dbo.SesionesCaja (OpenedByUserId, OpeningAmount, ClosingAmount, Status, OpenedAt, ClosedAt)
VALUES
(2, 50000, 186000, N'Cerrada', '2026-03-15T08:00:00', '2026-03-15T18:00:00');

INSERT INTO dbo.CatalogoCuentas (AccountCode, AccountName, AccountType)
SELECT N'1-01', N'Caja general', N'Activo'
UNION ALL SELECT N'1-02', N'Banco / SINPE', N'Activo'
UNION ALL SELECT N'4-01', N'Ingresos por ventas', N'Ingreso'
UNION ALL SELECT N'5-01', N'Gastos operativos', N'Gasto'
UNION ALL SELECT N'2-01', N'Cuentas por pagar', N'Pasivo';

INSERT INTO dbo.AsientosContables (EntryType, ReferenceTable, ReferenceId, Note, CreatedAt)
SELECT N'VENTA', N'Sales', SaleId, CONCAT(N'Venta del pedido #', OrderId), CreatedAt
FROM dbo.Ventas;

INSERT INTO dbo.LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
SELECT ae.AccountingEntryId, (SELECT AccountId FROM dbo.CatalogoCuentas WHERE AccountCode = N'1-02'), s.Total, 0
FROM dbo.AsientosContables ae
INNER JOIN dbo.Ventas s ON s.SaleId = ae.ReferenceId
WHERE ae.ReferenceTable = N'Sales';

INSERT INTO dbo.LineasAsientoContable (AccountingEntryId, AccountId, Debit, Credit)
SELECT ae.AccountingEntryId, (SELECT AccountId FROM dbo.CatalogoCuentas WHERE AccountCode = N'4-01'), 0, s.Total
FROM dbo.AsientosContables ae
INNER JOIN dbo.Ventas s ON s.SaleId = ae.ReferenceId
WHERE ae.ReferenceTable = N'Sales';

INSERT INTO dbo.CategoriasGasto (Name)
VALUES (N'Servicios'), (N'Ingredientes'), (N'Empaques');

INSERT INTO dbo.Gastos (ExpenseCategoryId, PaymentMethodId, Description, Amount, CreatedAt)
SELECT 1, 4, N'Electricidad del local', 42000, '2026-03-01T09:00:00'
UNION ALL SELECT 2, 2, N'Compra de lacteos', 68500, '2026-03-03T10:30:00';

INSERT INTO dbo.Proveedores (Name, Phone, Email)
SELECT N'Distribuidora Central', N'22220000', N'ventas@central.local'
UNION ALL SELECT N'Lacteos del Valle', N'22221111', N'info@lacteos.local';

INSERT INTO dbo.PagosProveedor (SupplierId, PaymentMethodId, Concept, Amount, DueDate, PaidAt, CreatedAt)
SELECT 1, 4, N'Harina y secos', 128000, '2026-03-30', NULL, '2026-03-10T09:00:00'
UNION ALL SELECT 2, 2, N'Mantequilla y crema', 94000, '2026-03-25', '2026-03-20', '2026-03-08T11:00:00';

INSERT INTO dbo.ConfiguracionesAplicacion (SettingKey, SettingValue)
SELECT N'iva', N'0.13'
UNION ALL SELECT N'frequentCustomerDiscount', N'0.05'
UNION ALL SELECT N'originName', N'BakeSmart Patri - Sagrada Familia'
UNION ALL SELECT N'originAddress', N'Sagrada Familia, San Jose, Costa Rica'
UNION ALL SELECT N'originLatitude', N'9.9142'
UNION ALL SELECT N'originLongitude', N'-84.0734';

INSERT INTO dbo.BitacoraAuditoria (UserId, LogType, Detail, CreatedAt)
SELECT 1, N'LOGIN', N'Inicio de sesion inicial', '2026-02-01T09:00:00'
UNION ALL SELECT 1, N'CREACION_PRODUCTO', N'Carga inicial de inventario', '2026-02-01T10:00:00'
UNION ALL SELECT 2, N'CREAR_PEDIDO', N'Carga inicial de pedidos', '2026-02-20T15:30:00'
UNION ALL SELECT 1, N'GENERAR_REPORTE', N'Carga inicial de reportes', '2026-03-01T12:00:00';
GO

CREATE INDEX IX_Users_RoleId ON dbo.Usuarios(RoleId);
CREATE INDEX IX_Customers_UserId ON dbo.Clientes(UserId);
CREATE INDEX IX_CustomerAddresses_CustomerId ON dbo.DireccionesCliente(CustomerId);
CREATE INDEX IX_Products_Category ON dbo.Productos(ProductCategoryId);
CREATE INDEX IX_Products_Type ON dbo.Productos(ProductTypeId);
CREATE INDEX IX_ProductImages_ProductId ON dbo.ImagenesProducto(ProductId, SortOrder);
CREATE INDEX IX_InventoryMovements_ProductId ON dbo.MovimientosInventario(ProductId, CreatedAt);
CREATE INDEX IX_Orders_CustomerId ON dbo.Pedidos(CustomerId);
CREATE INDEX IX_Orders_Status ON dbo.Pedidos(OrderStatusId);
CREATE INDEX IX_Orders_CreatedAt ON dbo.Pedidos(CreatedAt);
CREATE INDEX IX_OrderItems_OrderId ON dbo.DetallePedido(OrderId);
CREATE INDEX IX_Sales_CreatedAt ON dbo.Ventas(CreatedAt);
CREATE INDEX IX_AuditLogs_CreatedAt ON dbo.BitacoraAuditoria(CreatedAt);
GO

-- Migracion para bases existentes (ejecutar solo si faltan columnas)
IF COL_LENGTH(N'dbo.DireccionesCliente', N'Status') IS NULL
    ALTER TABLE dbo.DireccionesCliente ADD Status nvarchar(20) NOT NULL CONSTRAINT DF_CustomerAddresses_Status_Mig DEFAULT N'Activa';
IF COL_LENGTH(N'dbo.DireccionesCliente', N'CreatedAt') IS NULL
    ALTER TABLE dbo.DireccionesCliente ADD CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_CustomerAddresses_CreatedAt_Mig DEFAULT SYSUTCDATETIME();
IF COL_LENGTH(N'dbo.DireccionesCliente', N'UpdatedAt') IS NULL
    ALTER TABLE dbo.DireccionesCliente ADD UpdatedAt datetime2(0) NULL;
IF COL_LENGTH(N'dbo.Pedidos', N'DeliveryReference') IS NULL
    ALTER TABLE dbo.Pedidos ADD DeliveryReference nvarchar(250) NULL;
IF NOT EXISTS (SELECT 1 FROM dbo.ConfiguracionesAplicacion WHERE SettingKey = N'originAddress')
    INSERT INTO dbo.ConfiguracionesAplicacion (SettingKey, SettingValue) VALUES (N'originAddress', N'Sagrada Familia, San Jose, Costa Rica');
GO

SELECT
    (SELECT COUNT(*) FROM dbo.Usuarios) AS UsersCount,
    (SELECT COUNT(*) FROM dbo.Clientes) AS CustomersCount,
    (SELECT COUNT(*) FROM dbo.Productos) AS ProductsCount,
    (SELECT COUNT(*) FROM dbo.ImagenesProducto) AS ProductImagesCount,
    (SELECT COUNT(*) FROM dbo.ExistenciasInventario) AS InventoryBalancesCount,
    (SELECT COUNT(*) FROM dbo.Pedidos) AS OrdersCount,
    (SELECT COUNT(*) FROM dbo.DestinosGeograficos) AS GeoDestinationsCount;
GO
