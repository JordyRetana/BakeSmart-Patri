namespace BakeSmartPatri.Models;

public sealed record CatalogCategoryViewModel(int Id, string Name, string Icon);

public sealed record CatalogProductViewModel(
    int Id,
    string Code,
    string Name,
    string Description,
    string Category,
    string? Subcategory,
    decimal UnitPrice,
    decimal Stock,
    string Unit,
    string ImageUrl,
    string AltText,
    bool IsActive);

public sealed record CatalogProductImageViewModel(
    string ImageUrl,
    string AltText,
    int SortOrder,
    bool IsPrimary);

public sealed record CatalogIndexViewModel(
    IReadOnlyList<CatalogCategoryViewModel> Categories,
    IReadOnlyList<CatalogProductViewModel> Products);

public sealed record CatalogProductDetailsViewModel(
    CatalogProductViewModel Product,
    IReadOnlyList<CatalogProductImageViewModel> Images,
    IReadOnlyList<CatalogProductViewModel> RelatedProducts);

public sealed record OrderCreateViewModel(
    IReadOnlyList<CatalogProductViewModel> Products,
    IReadOnlyList<string> PaymentMethods);
