using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;
using System.Text.Json;

namespace BakeSmartPatri.Services;

public sealed class ReportExportService
{
    private static readonly CultureInfo CostaRica = CultureInfo.GetCultureInfo("es-CR");
    private static readonly string Purple = "#56318F";
    private static readonly string Pink = "#C23D83";
    private readonly IWebHostEnvironment _environment;

    public ReportExportService(IWebHostEnvironment environment) => _environment = environment;

    public byte[] CreateExcel(string type, object report, DateTime? start, DateTime? end)
    {
        var model = Parse(type, report, start, end);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Reporte");
        sheet.ShowGridLines = false;

        var logoPath = Path.Combine(_environment.WebRootPath, "img", "logo.png");
        var hasLogo = File.Exists(logoPath);
        var firstColumn = hasLogo ? 2 : 1;
        var lastColumn = Math.Max(firstColumn, firstColumn + model.Headers.Count - 1);
        if (hasLogo)
            sheet.AddPicture(logoPath).MoveTo(sheet.Cell(1, 1)).WithSize(46, 46);

        sheet.Range(1, firstColumn, 1, lastColumn).Merge().Value = "Repostería Patri · BakeSmart";
        sheet.Range(2, firstColumn, 2, lastColumn).Merge().Value = model.Title;
        sheet.Range(3, firstColumn, 3, lastColumn).Merge().Value = $"Periodo: {model.Period}  |  Generado: {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CostaRica)}";
        sheet.Range(4, firstColumn, 4, lastColumn).Merge().Value = model.Summary;

        var title = sheet.Range(1, firstColumn, 1, lastColumn);
        title.Style.Fill.BackgroundColor = XLColor.FromHtml(Purple);
        title.Style.Font.FontColor = XLColor.White;
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 18;
        title.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        sheet.Range(2, firstColumn, 2, lastColumn).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(XLColor.FromHtml(Purple));
        sheet.Range(3, firstColumn, 4, lastColumn).Style.Font.SetFontColor(XLColor.FromHtml("#667085"));

        const int headerRow = 6;
        for (var column = 0; column < model.Headers.Count; column++)
            sheet.Cell(headerRow, firstColumn + column).Value = model.Headers[column].Label;
        var header = sheet.Range(headerRow, firstColumn, headerRow, lastColumn);
        header.Style.Fill.BackgroundColor = XLColor.FromHtml(Pink);
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Font.Bold = true;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        for (var rowIndex = 0; rowIndex < model.Rows.Count; rowIndex++)
        {
            var excelRow = headerRow + 1 + rowIndex;
            for (var column = 0; column < model.Headers.Count; column++)
            {
                var definition = model.Headers[column];
                var value = model.Rows[rowIndex].TryGetProperty(definition.Key, out var property) ? property : default;
                WriteExcelValue(sheet.Cell(excelRow, firstColumn + column), definition.Key, value);
            }
            if (rowIndex % 2 == 1)
                sheet.Range(excelRow, firstColumn, excelRow, lastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("#F7F4FB");
            sheet.Range(excelRow, firstColumn, excelRow, lastColumn).Style.Border.BottomBorder = XLBorderStyleValues.Hair;
            sheet.Range(excelRow, firstColumn, excelRow, lastColumn).Style.Border.BottomBorderColor = XLColor.FromHtml("#E4E7EC");
        }

        if (model.Rows.Count > 0)
        {
            var tableRange = sheet.Range(headerRow, firstColumn, headerRow + model.Rows.Count, lastColumn);
            tableRange.SetAutoFilter();
        }

        sheet.SheetView.FreezeRows(headerRow);
        sheet.Columns().AdjustToContents(1, Math.Max(headerRow + model.Rows.Count, headerRow));
        foreach (var column in sheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width + 2, 12, 34);
        sheet.Row(1).Height = 30;
        sheet.Row(headerRow).Height = 24;
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.Margins.Top = .45;
        sheet.PageSetup.Margins.Bottom = .45;
        sheet.PageSetup.Footer.Center.AddText("Página &P de &N");

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public byte[] CreatePdf(string type, object report, DateTime? start, DateTime? end)
    {
        var model = Parse(type, report, start, end);
        var logoPath = Path.Combine(_environment.WebRootPath, "img", "logo.png");
        var logo = File.Exists(logoPath) ? File.ReadAllBytes(logoPath) : null;

        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(28);
            page.DefaultTextStyle(style => style.FontSize(9).FontColor("#25293B"));
            page.Header().Column(header =>
            {
                header.Item().Row(row =>
                {
                    if (logo is not null) row.ConstantItem(54).Height(46).Image(logo).FitArea();
                    row.RelativeItem().PaddingLeft(10).Column(text =>
                    {
                        text.Item().Text("BakeSmart Patri").Bold().FontSize(16).FontColor(Purple);
                        text.Item().Text(model.Title).Bold().FontSize(12);
                    });
                    row.RelativeItem().AlignRight().Column(text =>
                    {
                        text.Item().Text($"Periodo: {model.Period}").Bold();
                        text.Item().Text($"Generado: {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CostaRica)}").FontColor("#667085");
                    });
                });
                header.Item().PaddingTop(8).BorderBottom(2).BorderColor(Pink);
            });
            page.Content().PaddingTop(12).Column(content =>
            {
                content.Item().Background("#F6F0FB").Border(1).BorderColor("#E7DDF0").Padding(8).Text(model.Summary).Bold().FontColor(Purple);
                content.Item().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        foreach (var _ in model.Headers) columns.RelativeColumn();
                    });
                    table.Header(header =>
                    {
                        foreach (var column in model.Headers)
                            header.Cell().Background(Purple).Padding(6).Text(column.Label).Bold().FontColor(Colors.White);
                    });
                    for (var index = 0; index < model.Rows.Count; index++)
                    {
                        var row = model.Rows[index];
                        foreach (var column in model.Headers)
                        {
                            var value = row.TryGetProperty(column.Key, out var property) ? FormatPdfValue(column.Key, property) : "";
                            table.Cell().Background(index % 2 == 1 ? "#F8F6FB" : Colors.White).BorderBottom(1).BorderColor("#E4E7EC").Padding(5).Text(value);
                        }
                    }
                });
            });
            page.Footer().AlignCenter().Text(text =>
            {
                text.Span("BakeSmart Patri  ·  ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        })).GeneratePdf();
    }

    private static ExportModel Parse(string type, object report, DateTime? start, DateTime? end)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(report));
        var root = document.RootElement;
        var rows = root.TryGetProperty("rows", out var rowsElement)
            ? rowsElement.EnumerateArray().Select(row => row.Clone()).ToList()
            : [];
        var headers = rows.Count == 0
            ? []
            : rows.SelectMany(row => row.EnumerateObject().Select(property => property.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(key => !IsImageField(key))
                .Select(key => new Header(key, Friendly(key)))
                .ToList();
        var summary = string.Join("  ·  ", root.EnumerateObject()
            .Where(property => property.Name != "rows")
            .Select(property => $"{Friendly(property.Name)}: {FormatSummary(property.Name, property.Value)}"));
        return new ExportModel(Title(type), Period(start, end), string.IsNullOrWhiteSpace(summary) ? $"{rows.Count} registros" : summary, headers, rows);
    }

    private static void WriteExcelValue(IXLCell cell, string key, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
            return;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            cell.Value = number;
            cell.Style.NumberFormat.Format = IsMoney(key) ? "₡#,##0.00" : number == decimal.Truncate(number) ? "#,##0" : "#,##0.00";
        }
        else if (value.ValueKind == JsonValueKind.String && IsDate(key) && DateTime.TryParse(value.GetString(), out var date))
        {
            cell.Value = date;
            cell.Style.DateFormat.Format = date.TimeOfDay == TimeSpan.Zero ? "dd/mm/yyyy" : "dd/mm/yyyy hh:mm";
        }
        else cell.Value = value.ToString();
    }

    private static string FormatPdfValue(string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return IsMoney(key) ? number.ToString("C0", CostaRica) : number.ToString(number == decimal.Truncate(number) ? "N0" : "N2", CostaRica);
        if (value.ValueKind == JsonValueKind.String && IsDate(key) && DateTime.TryParse(value.GetString(), out var date))
            return date.ToString(date.TimeOfDay == TimeSpan.Zero ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm", CostaRica);
        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : value.ToString();
    }

    private static string FormatSummary(string key, JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? IsMoney(key) ? number.ToString("C0", CostaRica) : number.ToString("N0", CostaRica)
            : value.ToString();

    private static bool IsMoney(string key) => key.Contains("total", StringComparison.OrdinalIgnoreCase) || key.Contains("monto", StringComparison.OrdinalIgnoreCase) || key.Contains("income", StringComparison.OrdinalIgnoreCase) || key.Contains("subtotal", StringComparison.OrdinalIgnoreCase) || key.Contains("tax", StringComparison.OrdinalIgnoreCase) || key.Contains("precio", StringComparison.OrdinalIgnoreCase) || key.Contains("costo", StringComparison.OrdinalIgnoreCase);
    private static bool IsDate(string key) => key.Contains("fecha", StringComparison.OrdinalIgnoreCase) || key.Contains("date", StringComparison.OrdinalIgnoreCase) || key.Contains("apertura", StringComparison.OrdinalIgnoreCase) || key.Contains("cierre", StringComparison.OrdinalIgnoreCase);
    private static bool IsImageField(string key) => key.Contains("image", StringComparison.OrdinalIgnoreCase) || key.Contains("imagen", StringComparison.OrdinalIgnoreCase) || key.Contains("photo", StringComparison.OrdinalIgnoreCase) || key.Contains("foto", StringComparison.OrdinalIgnoreCase) || key.Contains("avatar", StringComparison.OrdinalIgnoreCase);
    private static string Period(DateTime? start, DateTime? end) => $"{start?.ToString("dd/MM/yyyy") ?? "Todos"} - {end?.ToString("dd/MM/yyyy") ?? "Actual"}";
    private static string Title(string type) => type switch { "sales" => "Reporte de ventas", "inventory" => "Reporte de inventario", "users" => "Reporte de usuarios", "promotions" => "Reporte de promociones", "cashClosures" => "Reporte de cierres de caja", "orders" => "Reporte de pedidos", _ => "Reporte administrativo" };
    private static string Friendly(string value)
    {
        var label = value switch
        {
            "totalIncome" => "Ingresos totales", "totalTransactions" => "Transacciones", "lowStock" => "Stock bajo",
            "negativeStock" => "Stock negativo", "activeUsers" => "Usuarios activos", "activePromotions" => "Promociones activas",
            "totalSales" => "Ventas totales", "totalOrders" => "Pedidos", "montoInicial" => "Monto inicial",
            "montoFinal" => "Monto final", "totalVentas" => "Total ventas", "id" => "N.º", "saleId" => "N.º venta",
            "orderId" => "N.º pedido", "productId" => "N.º producto", "customerName" => "Cliente", "customerEmail" => "Correo",
            "customerPhone" => "Teléfono", "paymentStatus" => "Estado de pago", "orderStatus" => "Estado del pedido",
            "isActive" => "Activo", "unitPrice" => "Precio unitario", "stock" => "Existencia", "minimumStock" => "Stock mínimo",
            "createdAt" => "Fecha de creación", "updatedAt" => "Última actualización", "userName" => "Usuario", "role" => "Rol",
            _ => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? $" {char.ToLowerInvariant(character)}" : character.ToString())).Replace('_', ' ').Trim()
        };
        return string.IsNullOrEmpty(label) ? label : char.ToUpperInvariant(label[0]) + label[1..];
    }

    private sealed record Header(string Key, string Label);
    private sealed record ExportModel(string Title, string Period, string Summary, IReadOnlyList<Header> Headers, IReadOnlyList<JsonElement> Rows);
}
