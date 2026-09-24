using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GamingCenter.Application.Common;
using GamingCenter.Application.DTOs;
using GamingCenter.Application.Interfaces;
using GamingCenter.Domain.Enums;

namespace GamingCenter.App.Services;

/// <summary>
/// Prints receipts and reports with WPF FlowDocuments. Works with thermal receipt printers
/// (set the printer and paper width in Settings) and with "Microsoft Print to PDF" for PDF files.
/// </summary>
public sealed class PrintService(ISettingsService settings)
{
    private static readonly FontFamily Mono = new("Consolas");

    /// <summary>Prints silently to the configured receipt printer, or asks when none is set / <paramref name="choosePrinter"/>.</summary>
    public bool PrintReceipt(ReceiptDto r, bool choosePrinter = false)
    {
        var cfg = settings.Current;
        double width = cfg.ReceiptWidthMm * 96 / 25.4;
        var doc = BuildReceipt(r, width);

        var dialog = new PrintDialog();
        if (!choosePrinter && !string.IsNullOrWhiteSpace(cfg.ReceiptPrinterName))
        {
            try
            {
                dialog.PrintQueue = new LocalPrintServer().GetPrintQueue(cfg.ReceiptPrinterName);
            }
            catch (Exception)
            {
                if (dialog.ShowDialog() != true) return false;
            }
        }
        else if (dialog.ShowDialog() != true) return false;

        doc.PageWidth = Math.Min(width, dialog.PrintableAreaWidth > 0 ? dialog.PrintableAreaWidth : width);
        doc.ColumnWidth = doc.PageWidth;
        dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"Receipt {r.ReceiptNumber}");
        return true;
    }

    /// <summary>Opens the print dialog so the user can pick "Microsoft Print to PDF" or a paper printer.</summary>
    public bool PrintDocument(FlowDocument doc, string title)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return false;
        doc.PageWidth = dialog.PrintableAreaWidth;
        doc.PageHeight = dialog.PrintableAreaHeight;
        doc.ColumnWidth = double.PositiveInfinity;
        doc.PagePadding = new Thickness(48);
        dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, title);
        return true;
    }

    public static IReadOnlyList<string> InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]).Select(q => q.FullName).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public FlowDocument BuildReceipt(ReceiptDto r, double width)
    {
        var doc = new FlowDocument
        {
            FontFamily = Mono, FontSize = 11, PageWidth = width, ColumnWidth = width,
            PagePadding = new Thickness(8), Background = Brushes.White, Foreground = Brushes.Black,
        };
        doc.Blocks.Add(Center(r.CenterName, 14, FontWeights.Bold));
        var contact = string.Join(" · ", new[] { r.Address, r.Phone }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (contact.Length > 0) doc.Blocks.Add(Center(contact, 10));
        doc.Blocks.Add(Center($"{r.IssuedAt:dd/MM/yyyy HH:mm} · #{r.ReceiptNumber}", 10));
        doc.Blocks.Add(Rule());

        if (r.Mode != SessionMode.CounterSale)
        {
            doc.Blocks.Add(Row("Station", r.StationName));
            doc.Blocks.Add(Row("Session", $"{r.StartTime:HH:mm} → {r.EndTime:HH:mm}"));
            doc.Blocks.Add(Row("Play time", Durations.Long(TimeSpan.FromSeconds(r.PlayedSeconds))));
            doc.Blocks.Add(Row($"Gaming @{Money.Number(Math.Round(r.HourlyRate, 0))}/h", Money.Number(r.GamingTotal)));
            if (!string.IsNullOrWhiteSpace(r.RateNote))
                doc.Blocks.Add(new Paragraph(new Run(r.RateNote)) { FontSize = 9, Margin = new Thickness(0, 0, 0, 2), Foreground = Brushes.DimGray });
            doc.Blocks.Add(Rule());
        }
        foreach (var l in r.Lines)
            doc.Blocks.Add(Row($"{l.Name} x{l.Quantity}", Money.Number(l.LineTotal)));
        if (r.Lines.Count > 0) doc.Blocks.Add(Rule());

        doc.Blocks.Add(Row("TOTAL", Money.Format(r.Total), bold: true, size: 13));
        doc.Blocks.Add(Row(r.Method.ToString(), Money.Number(r.AmountReceived)));
        if (r.Change > 0) doc.Blocks.Add(Row("Change", Money.Number(r.Change)));
        if (!string.IsNullOrWhiteSpace(r.CustomerName)) doc.Blocks.Add(Row("Customer", r.CustomerName));
        if (!string.IsNullOrWhiteSpace(r.Footer)) doc.Blocks.Add(Center(r.Footer, 10, margin: 10));
        return doc;
    }

    public FlowDocument BuildReport(ReportData d, string title, string centerName)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Background = Brushes.White, Foreground = Brushes.Black };
        doc.Blocks.Add(new Paragraph(new Run(centerName)) { FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0) });
        doc.Blocks.Add(new Paragraph(new Run($"{title} · {d.From:dd/MM/yyyy} – {d.To.AddDays(-1):dd/MM/yyyy}")) { Foreground = Brushes.DimGray });

        doc.Blocks.Add(Table(["Metric", "Value"], [
            ["Total revenue", Money.Format(d.TotalRevenue)],
            ["Gaming revenue", Money.Format(d.GamingRevenue)],
            ["Product revenue", Money.Format(d.ProductRevenue)],
            ["Estimated product profit", Money.Format(d.ProductProfit)],
            ["Sessions", d.Sessions.ToString()],
            ["Average session", Durations.Short(d.AverageSession)],
        ]));
        doc.Blocks.Add(Heading("Revenue per day"));
        doc.Blocks.Add(Table(["Day", "Gaming", "Products", "Total"],
            d.PerDay.Select(x => new[] { x.Day.ToString("ddd dd/MM"), Money.Number(x.Gaming), Money.Number(x.Products), Money.Number(x.Total) }).ToList()));
        doc.Blocks.Add(Heading("Revenue per station"));
        doc.Blocks.Add(Table(["Station", "Sessions", "Play time", "Revenue"],
            d.PerStation.Select(x => new[] { x.Name, x.Sessions.ToString(), Durations.Short(x.PlayTime), Money.Number(x.Revenue) }).ToList()));
        doc.Blocks.Add(Heading("Product sales"));
        doc.Blocks.Add(Table(["Product", "Units", "Revenue", "Profit"],
            d.TopProducts.Select(x => new[] { x.Name, x.Units.ToString(), Money.Number(x.Revenue), Money.Number(x.Profit) }).ToList()));
        return doc;
    }

    private static Paragraph Heading(string text) => new(new Run(text)) { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };

    private static Table Table(string[] headers, IReadOnlyList<string[]> rows)
    {
        var t = new Table { CellSpacing = 0, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 0, 0, 1) };
        foreach (var _ in headers) t.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        var head = new TableRow { FontWeight = FontWeights.SemiBold, Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)) };
        foreach (var h in headers) head.Cells.Add(new TableCell(new Paragraph(new Run(h))) { Padding = new Thickness(6, 4, 6, 4) });
        group.Rows.Add(head);
        foreach (var r in rows)
        {
            var row = new TableRow();
            foreach (var c in r) row.Cells.Add(new TableCell(new Paragraph(new Run(c))) { Padding = new Thickness(6, 3, 6, 3) });
            group.Rows.Add(row);
        }
        t.RowGroups.Add(group);
        return t;
    }

    private static Paragraph Center(string text, double size, FontWeight? weight = null, double margin = 0) =>
        new(new Run(text)) { TextAlignment = TextAlignment.Center, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Margin = new Thickness(0, margin, 0, 2) };

    private static Paragraph Rule() => new(new Run(new string('-', 40))) { Margin = new Thickness(0, 2, 0, 2), TextAlignment = TextAlignment.Center, Foreground = Brushes.Gray };

    private static BlockUIContainer Row(string left, string right, bool bold = false, double size = 11)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = left, FontFamily = Mono, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
        var r = new TextBlock { Text = right, FontFamily = Mono, FontSize = size, Margin = new Thickness(8, 0, 0, 0), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
        Grid.SetColumn(r, 1);
        grid.Children.Add(l);
        grid.Children.Add(r);
        return new BlockUIContainer(grid) { Margin = new Thickness(0, 1, 0, 1) };
    }
}
