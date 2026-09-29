using System.Collections;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Aksara.Desktop.Services;
using System.Xml;
using Microsoft.Win32;

namespace Aksara.Desktop.Views;

internal static class ReportGridTools
{
    private static readonly CultureInfo IndonesianCulture = CultureInfo.GetCultureInfo("id-ID");

    public static void ConfigureScrollableReportGrid(DataGrid grid)
    {
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        grid.EnableColumnVirtualization = true;
        grid.EnableRowVirtualization = true;
        grid.CanUserResizeColumns = true;
        ScrollViewer.SetCanContentScroll(grid, true);
    }

    public static void PrintDataGrid(DataGrid grid, string title)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var columns = grid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex)
            .ToList();
        var rows = Rows(grid).ToList();

        if (columns.Count == 0 || rows.Count == 0)
        {
            MessageBox.Show("Tidak ada data laporan untuk dicetak.", $"AKSARA - {title}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        var document = BuildDocument(title, columns, rows, dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, title);
    }

    public static void ExportDataGridToExcel(DataGrid grid, string title)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        var columns = grid.Columns
            .Where(column => column.Visibility == Visibility.Visible)
            .OrderBy(column => column.DisplayIndex)
            .Select(column => new ExcelColumn(
                column.Header?.ToString() ?? "",
                ColumnWeight(column),
                row => BoundValue(column, row),
                column is DataGridBoundColumn { Binding: Binding binding } ? binding.StringFormat : null))
            .ToList();
        ExportRowsToExcel(title, columns, Rows(grid).ToList());
    }

    public static void ExportObjectsToExcel(string title, IEnumerable<object> sourceRows)
    {
        var rows = sourceRows.Where(row => row is not null).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("Tidak ada data laporan untuk diekspor.", $"AKSARA - {title}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var properties = rows[0].GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var columns = properties
            .Select(property => new ExcelColumn(ToHeader(property.Name), 110, row => property.GetValue(row), null))
            .ToList();
        ExportRowsToExcel(title, columns, rows);
    }

    public static void ExportLedgerAccountTemplate()
    {
        var columns = new[]
        {
            new ExcelColumn("Kode Perkiraan", 120, _ => null, null),
            new ExcelColumn("Nama Perkiraan", 240, _ => null, null),
            new ExcelColumn("Jenis", 110, _ => null, null),
            new ExcelColumn("Saldo Normal", 100, _ => null, null),
            new ExcelColumn("Kode Induk", 110, _ => null, null),
            new ExcelColumn("Dapat Diposting", 110, _ => null, null),
            new ExcelColumn("Kas Bank", 90, _ => null, null),
            new ExcelColumn("Aktif", 80, _ => null, null)
        };
        var dialog = new SaveFileDialog
        {
            Title = "Unduh Template Impor COA",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = "Template-Impor-COA-AKSARA.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            WriteWorkbook(dialog.FileName, "Template COA", columns, Array.Empty<object>());
            MessageBox.Show(
                $"Template COA berhasil disimpan ke:\n{dialog.FileName}\n\nJenis akun: ASSET, LIABILITY, EQUITY, REVENUE, EXPENSE, atau COGS. Kolom pilihan dapat diisi YA/TIDAK.",
                "AKSARA - Template COA",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "AKSARA - Template COA", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static FlowDocument BuildDocument(string title, IReadOnlyList<DataGridColumn> columns, IReadOnlyList<object> rows, double printableWidth, double printableHeight)
    {
        var pageWidth = printableWidth > 0 ? printableWidth : 1050;
        var pageHeight = printableHeight > 0 ? printableHeight : 740;
        var contentWidth = Math.Max(320, pageWidth - 48);
        var fontSize = columns.Count > 11 ? 7.5 : columns.Count > 8 ? 8 : 9;

        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Tahoma"),
            FontSize = fontSize,
            PagePadding = new Thickness(24),
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            ColumnWidth = double.PositiveInfinity,
            Background = Brushes.White
        };

        document.Blocks.Add(new Paragraph(new Run(ProgramConfigurationCache.PrintCompanyName))
        {
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        });
        document.Blocks.Add(new Paragraph(new Run(title.ToUpperInvariant()))
        {
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2)
        });
        document.Blocks.Add(new Paragraph(new Run($"Dicetak {DateTime.Now:dd/MM/yyyy HH:mm:ss}"))
        {
            FontSize = 8,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var weights = columns.Select(ColumnWeight).ToList();
        var totalWeight = Math.Max(1, weights.Sum());
        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Margin = new Thickness(0, 0, 0, 8)
        };
        foreach (var weight in weights)
            table.Columns.Add(new TableColumn { Width = new GridLength(contentWidth * weight / totalWeight) });

        var group = new TableRowGroup();
        var header = new TableRow();
        foreach (var column in columns)
            header.Cells.Add(Cell(column.Header?.ToString() ?? "", true, false));
        group.Rows.Add(header);

        foreach (var row in rows)
        {
            var tableRow = new TableRow();
            foreach (var column in columns)
            {
                var value = CellValue(column, row);
                tableRow.Cells.Add(Cell(value.Text, false, value.IsNumeric));
            }
            group.Rows.Add(tableRow);
        }

        table.RowGroups.Add(group);
        document.Blocks.Add(table);
        return document;
    }

    private static void ExportRowsToExcel(string title, IReadOnlyList<ExcelColumn> columns, IReadOnlyList<object> rows)
    {
        if (columns.Count == 0 || rows.Count == 0)
        {
            MessageBox.Show("Tidak ada data laporan untuk diekspor.", $"AKSARA - {title}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = $"Ekspor Excel - {title}",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = SafeFileName($"{title}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"),
            AddExtension = true,
            DefaultExt = ".xlsx",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            WriteWorkbook(dialog.FileName, title, columns, rows);
            MessageBox.Show($"Laporan berhasil diekspor ke:\n{dialog.FileName}", $"AKSARA - {title}", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, $"AKSARA - Ekspor Excel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void WriteWorkbook(string path, string title, IReadOnlyList<ExcelColumn> columns, IReadOnlyList<object> rows)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteContentTypes(archive);
        WriteRootRelationships(archive);
        WriteWorkbookPart(archive, title);
        WriteWorkbookRelationships(archive);
        WriteWorksheet(archive, columns, rows);
    }

    private static void WriteContentTypes(ZipArchive archive) => WriteXml(archive, "[Content_Types].xml", writer =>
    {
        writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
        writer.WriteStartElement("Default"); writer.WriteAttributeString("Extension", "rels"); writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml"); writer.WriteEndElement();
        writer.WriteStartElement("Default"); writer.WriteAttributeString("Extension", "xml"); writer.WriteAttributeString("ContentType", "application/xml"); writer.WriteEndElement();
        writer.WriteStartElement("Override"); writer.WriteAttributeString("PartName", "/xl/workbook.xml"); writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"); writer.WriteEndElement();
        writer.WriteStartElement("Override"); writer.WriteAttributeString("PartName", "/xl/worksheets/sheet1.xml"); writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"); writer.WriteEndElement();
        writer.WriteEndElement();
    });

    private static void WriteRootRelationships(ZipArchive archive) => WriteXml(archive, "_rels/.rels", writer =>
    {
        writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", "rId1");
        writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
        writer.WriteAttributeString("Target", "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndElement();
    });

    private static void WriteWorkbookPart(ZipArchive archive, string title) => WriteXml(archive, "xl/workbook.xml", writer =>
    {
        writer.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        writer.WriteStartElement("sheets");
        writer.WriteStartElement("sheet");
        writer.WriteAttributeString("name", SheetName(title));
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", null, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    });

    private static void WriteWorkbookRelationships(ZipArchive archive) => WriteXml(archive, "xl/_rels/workbook.xml.rels", writer =>
    {
        writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", "rId1");
        writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet");
        writer.WriteAttributeString("Target", "worksheets/sheet1.xml");
        writer.WriteEndElement();
        writer.WriteEndElement();
    });

    private static void WriteWorksheet(ZipArchive archive, IReadOnlyList<ExcelColumn> columns, IReadOnlyList<object> rows) => WriteXml(archive, "xl/worksheets/sheet1.xml", writer =>
    {
        writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        writer.WriteStartElement("cols");
        for (var i = 0; i < columns.Count; i++)
        {
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", Math.Clamp(columns[i].Width / 7.0, 9, 38).ToString("0.##", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("sheetData");
        WriteExcelRow(writer, 1, columns.Select(column => new ExcelCell(column.Header, false)));
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            WriteExcelRow(writer, rowIndex + 2, columns.Select(column =>
            {
                var value = column.Value(row);
                return new ExcelCell(FormatValue(value, column.StringFormat), IsNumeric(value), value);
            }));
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
    });

    private static void WriteExcelRow(XmlWriter writer, int rowNumber, IEnumerable<ExcelCell> cells)
    {
        writer.WriteStartElement("row");
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        var columnIndex = 1;
        foreach (var cell in cells)
        {
            writer.WriteStartElement("c");
            writer.WriteAttributeString("r", $"{ColumnName(columnIndex)}{rowNumber}");
            if (cell.IsNumeric)
            {
                writer.WriteStartElement("v");
                writer.WriteString(Convert.ToDecimal(cell.RawValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }
            else
            {
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is");
                writer.WriteElementString("t", cell.Text);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            columnIndex++;
        }
        writer.WriteEndElement();
    }

    private static void WriteXml(ZipArchive archive, string entryName, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = false });
        write(writer);
    }

    private static IEnumerable<object> Rows(DataGrid grid)
    {
        var source = grid.ItemsSource as IEnumerable ?? grid.Items;
        foreach (var item in source)
        {
            if (item is null || ReferenceEquals(item, CollectionView.NewItemPlaceholder)) continue;
            yield return item;
        }
    }

    private static TableCell Cell(string text, bool header, bool numeric)
    {
        var paragraph = new Paragraph(new Run(text))
        {
            Margin = new Thickness(2, 1, 2, 1),
            TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left
        };
        return new TableCell(paragraph)
        {
            Padding = new Thickness(2),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(0.5),
            FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
            Background = header ? LedgerAccountView.Brush("#E8EDF3") : Brushes.White
        };
    }

    private static (string Text, bool IsNumeric) CellValue(DataGridColumn column, object row)
    {
        var value = BoundValue(column, row);
        var format = column is DataGridBoundColumn { Binding: Binding binding } ? binding.StringFormat : null;
        return (FormatValue(value, format), IsNumeric(value));
    }

    private static object? BoundValue(DataGridColumn column, object row)
    {
        if (column is DataGridBoundColumn { Binding: Binding binding })
        {
            var path = binding.Path?.Path;
            if (!string.IsNullOrWhiteSpace(path)) return ReadPath(row, path);
        }
        return string.IsNullOrWhiteSpace(column.SortMemberPath) ? null : ReadPath(row, column.SortMemberPath);
    }

    private static object? ReadPath(object row, string path)
    {
        object? current = row;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null) return null;
            var property = current.GetType().GetProperty(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            current = property?.GetValue(current);
        }
        return current;
    }

    private static string FormatValue(object? value, string? stringFormat)
    {
        if (value is null) return "";
        if (value is DateOnly date) return date.ToString("dd/MM/yyyy", IndonesianCulture);
        if (value is DateTime dateTime) return dateTime.ToString("dd/MM/yyyy", IndonesianCulture);
        if (value is bool flag) return flag ? "Ya" : "Tidak";

        if (!string.IsNullOrWhiteSpace(stringFormat))
        {
            var format = stringFormat.Trim();
            if (format.StartsWith("{0:", StringComparison.Ordinal) && format.EndsWith('}'))
                format = format[3..^1];
            if (value is IFormattable formattable)
                return formattable.ToString(format, IndonesianCulture);
            return string.Format(IndonesianCulture, stringFormat, value);
        }

        if (value is decimal decimalValue) return decimalValue.ToString("#,0.##", IndonesianCulture);
        if (value is double doubleValue) return doubleValue.ToString("#,0.##", IndonesianCulture);
        if (value is float floatValue) return floatValue.ToString("#,0.##", IndonesianCulture);
        return Convert.ToString(value, IndonesianCulture) ?? "";
    }

    private static bool IsNumeric(object? value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static double ColumnWeight(DataGridColumn column)
    {
        if (!double.IsNaN(column.ActualWidth) && column.ActualWidth > 0) return column.ActualWidth;
        if (column.Width.IsAbsolute && column.Width.Value > 0) return column.Width.Value;
        return Math.Max(90, column.MinWidth);
    }

    private static string ColumnName(int index)
    {
        var name = "";
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    private static string SheetName(string title)
    {
        var value = Regex.Replace(title, @"[\[\]\:\*\?\/\\]", " ").Trim();
        return string.IsNullOrWhiteSpace(value) ? "Laporan" : value[..Math.Min(31, value.Length)];
    }

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '-');
        return value;
    }

    private static string ToHeader(string propertyName) => Regex.Replace(propertyName, "([a-z0-9])([A-Z])", "$1 $2");

    private sealed record ExcelColumn(string Header, double Width, Func<object, object?> Value, string? StringFormat);
    private sealed record ExcelCell(string Text, bool IsNumeric, object? RawValue = null);
}
