using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Reports;

public sealed record RecipePrintOptions(double Factor = 1, bool Notes = true, bool Instructions = true, bool Costs = false, float FontSize = 10.5f);

public sealed record LabelLayout(int Columns = 2, int Rows = 5);

/// <summary>Her business as printed on invoices.</summary>
public sealed record BusinessInfo(string? Name, string? Address1, string? Address2, string? City, string? Province, string? Postal, string? Phone, string? Email)
{
    public IEnumerable<string> Lines()
    {
        foreach (var l in new[] { Address1, Address2, string.Join(" ", new[] { City, Province, Postal }.Where(x => !string.IsNullOrWhiteSpace(x))), Phone, Email })
            if (!string.IsNullOrWhiteSpace(l)) yield return l!.Trim();
    }
}

/// <summary>PDF output. Everything prints through the system PDF viewer, so there is no printer code to maintain.</summary>
public static class Pdf
{
    static Pdf() => QuestPDF.Settings.License = LicenseType.Community;

    private const string Ink = "#1B1C1F";
    private const string Muted = "#6B6E75";
    private const string Rule = "#D6D7DB";
    private const string Band = "#F3F3F5";

    private static string Qty(double v) => v.ToString(v >= 100 ? "#,##0" : "#,##0.#", CultureInfo.CurrentCulture);

    // ------------------------------------------------------------------ recipe

    public static void Recipe(string path, Recipe recipe, string? category, RecipeCost cost, IReadOnlyList<ExpandedLine> lines, RecipePrintOptions o)
    {
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(0.6f, Unit.Inch);
            page.DefaultTextStyle(t => t.FontSize(o.FontSize).FontColor(Ink));

            page.Header().Column(h =>
            {
                h.Item().Text(recipe.Name).FontSize(o.FontSize + 7.5f).SemiBold();
                var sub = new List<string>();
                if (category is not null) sub.Add(category);
                sub.Add($"{Qty(cost.TotalAmount * o.Factor)} {recipe.Unit}");
                if (recipe.Servings is > 0) sub.Add($"{Qty(recipe.Servings.Value * o.Factor)} servings");
                if (Math.Abs(o.Factor - 1) > 0.0001) sub.Add($"scaled ×{o.Factor:0.###}");
                if (!string.IsNullOrWhiteSpace(recipe.Notes)) sub.Add(recipe.Notes!);
                h.Item().PaddingTop(2).Text(string.Join("  ·  ", sub)).FontColor(Muted);
                h.Item().PaddingTop(8).LineHorizontal(0.75f).LineColor(Rule);
            });

            page.Content().PaddingTop(10).Column(col =>
            {
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(o.Notes ? 4 : 6);
                        c.ConstantColumn(70);
                        c.ConstantColumn(34);
                        c.ConstantColumn(48);
                        if (o.Notes) c.RelativeColumn(4);
                    });
                    t.Header(head =>
                    {
                        Head(head.Cell(), "Ingredient");
                        Head(head.Cell().AlignRight(), "Qty");
                        Head(head.Cell(), "");
                        Head(head.Cell().AlignRight(), "%");
                        if (o.Notes) Head(head.Cell().PaddingLeft(10), "Notes");
                    });
                    var i = 0;
                    foreach (var l in lines)
                    {
                        if (l.Kind == LineKind.Separator)
                        {
                            t.Cell().ColumnSpan(o.Notes ? 5u : 4u).PaddingVertical(3).LineHorizontal(0.5f).LineColor(Rule);
                            continue;
                        }
                        var bg = i++ % 2 == 1 ? Band : "#FFFFFF";
                        var indent = l.Depth * 14;
                        var name = t.Cell().Background(bg).PaddingVertical(2.5f).PaddingLeft(indent + 2).Text(l.Name);
                        if (l.Kind == LineKind.SubRecipe) name.SemiBold();
                        if (l.Depth > 0) name.FontColor(Muted);
                        t.Cell().Background(bg).PaddingVertical(2.5f).AlignRight().Text(Qty(l.Amount));
                        t.Cell().Background(bg).PaddingVertical(2.5f).PaddingLeft(4).Text(l.Unit).FontColor(Muted);
                        t.Cell().Background(bg).PaddingVertical(2.5f).AlignRight().Text(l.Depth == 0 ? $"{l.Percent:0.0}" : "").FontColor(Muted);
                        if (o.Notes) t.Cell().Background(bg).PaddingVertical(2.5f).PaddingLeft(10).Text(l.Notes ?? "").FontSize(9.5f);
                    }
                });

                if (o.Costs)
                {
                    col.Item().PaddingTop(12).AlignRight().Text(text =>
                    {
                        text.Span($"Ingredients {cost.ComponentsCost * (decimal)o.Factor:C2}   ·   Labour {cost.LabourCost * (decimal)o.Factor:C2}   ·   ");
                        text.Span($"Total {cost.TotalCost * (decimal)o.Factor:C2}").SemiBold();
                        if (cost.CostPerServing is { } ps) text.Span($"   ·   per serving {ps:C2}");
                    });
                }

                if (o.Instructions && !string.IsNullOrWhiteSpace(recipe.Instructions))
                {
                    col.Item().PaddingTop(18).Text("Method").FontSize(12).SemiBold();
                    col.Item().PaddingTop(4).Text(recipe.Instructions!.Trim()).LineHeight(1.3f);
                }

                if (recipe.Shape != PanShape.None && Pan.Of(recipe).IsMeasured)
                {
                    var p = Pan.Of(recipe);
                    var dims = recipe.Shape == PanShape.Round
                        ? $"round Ø {recipe.Length:0.#} cm" : $"{recipe.Width:0.#} × {recipe.Length:0.#} cm";
                    if (recipe.Height is > 0) dims += $", {recipe.Height:0.#} cm high";
                    col.Item().PaddingTop(14).Text($"Pan: {dims}" + (Math.Abs(o.Factor - 1) > 0.0001 ? " (original)" : "")).FontColor(Muted);
                }
            });

            page.Footer().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8.5f).FontColor(Muted));
                t.Span($"Printed {DateTime.Now:d}   ·   page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
        })).GeneratePdf(path);
    }

    private static void Head(IContainer c, string text) =>
        c.BorderBottom(0.75f).BorderColor(Rule).PaddingBottom(3).Text(text).FontSize(9).SemiBold().FontColor(Muted);

    // ------------------------------------------------------------------ labels

    /// <summary>One label per recipe, laid out on a sheet grid (default 2 × 5, e.g. Avery 5163 4"×2").</summary>
    public static void Labels(string path, IReadOnlyList<(Recipe Recipe, int Copies)> recipes, LabelLayout layout)
    {
        var labels = recipes.SelectMany(r => Enumerable.Repeat(r.Recipe, Math.Max(1, r.Copies))).ToList();
        var perPage = layout.Columns * layout.Rows;
        const float pageH = 11 * 72f, marginV = 0.5f * 72, marginH = 0.19f * 72;
        var cellH = (pageH - 2 * marginV) / layout.Rows;

        Document.Create(doc =>
        {
            for (var start = 0; start < labels.Count; start += perPage)
            {
                var chunk = labels.Skip(start).Take(perPage).ToList();
                doc.Page(page =>
                {
                    page.Size(PageSizes.Letter);
                    page.MarginVertical(marginV);
                    page.MarginHorizontal(marginH);
                    page.DefaultTextStyle(t => t.FontColor(Ink));
                    page.Content().Table(t =>
                    {
                        t.ColumnsDefinition(c => { for (var i = 0; i < layout.Columns; i++) c.RelativeColumn(); });
                        foreach (var r in chunk)
                            t.Cell().Height(cellH).Padding(10).Element(c => Label(c, r));
                    });
                });
            }
        }).GeneratePdf(path);
    }

    private static void Label(IContainer c, Recipe r) => c.Column(col =>
    {
        col.Item().Text(string.IsNullOrWhiteSpace(r.LabelTitle) ? r.Name : r.LabelTitle).FontSize(13).SemiBold();
        if (!string.IsNullOrWhiteSpace(r.LabelText))
            col.Item().PaddingTop(3).Text(r.LabelText).FontSize(8.5f).LineHeight(1.2f);
        var flags = r.Tags.Select(t => t.Name).Where(n => n is "GF" or "DF").ToList();
        var bottom = string.Join("   ", flags.Select(f => f == "GF" ? "Gluten free" : "Dairy free"));
        if (!string.IsNullOrWhiteSpace(r.ChargeCode)) bottom = (bottom + "   " + r.ChargeCode).Trim();
        if (bottom.Length > 0) col.Item().PaddingTop(4).Text(bottom).FontSize(8).SemiBold().FontColor(Muted);
    });

    // ------------------------------------------------------------------ invoice

    public static void Invoice(string path, Invoice invoice, Company customer, BusinessInfo business)
    {
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(0.7f, Unit.Inch);
            page.DefaultTextStyle(t => t.FontSize(10.5f).FontColor(Ink));

            page.Header().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(business.Name ?? "").FontSize(16).SemiBold();
                    foreach (var l in business.Lines()) c.Item().Text(l).FontColor(Muted);
                });
                row.ConstantItem(200).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text("INVOICE").FontSize(20).SemiBold();
                    c.Item().AlignRight().Text($"No. {invoice.Id:0000}");
                    c.Item().AlignRight().Text(invoice.Date.ToString("D", CultureInfo.CurrentCulture)).FontColor(Muted);
                });
            });

            page.Content().PaddingTop(24).Column(col =>
            {
                col.Item().Text("BILL TO").FontSize(9).SemiBold().FontColor(Muted);
                col.Item().PaddingTop(2).Text(customer.Name).SemiBold();
                if (!string.IsNullOrWhiteSpace(customer.Contact) && customer.Contact != customer.Name) col.Item().Text(customer.Contact);
                foreach (var l in new[] { customer.Street, string.Join(", ", new[] { customer.City, customer.Province, customer.PostalCode }.Where(x => !string.IsNullOrWhiteSpace(x))), customer.Phone, customer.Email })
                    if (!string.IsNullOrWhiteSpace(l)) col.Item().Text(l!).FontColor(Muted);

                col.Item().PaddingTop(20).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.RelativeColumn(); c.ConstantColumn(60); c.ConstantColumn(80); c.ConstantColumn(90); });
                    t.Header(h =>
                    {
                        Head(h.Cell(), "Item");
                        Head(h.Cell().AlignRight(), "Qty");
                        Head(h.Cell().AlignRight(), "Price");
                        Head(h.Cell().AlignRight(), "Amount");
                    });
                    var i = 0;
                    foreach (var item in invoice.Items.OrderBy(x => x.Position))
                    {
                        var bg = i++ % 2 == 1 ? Band : "#FFFFFF";
                        t.Cell().Background(bg).PaddingVertical(4).PaddingLeft(2).Text(item.Name);
                        t.Cell().Background(bg).PaddingVertical(4).AlignRight().Text(item.Quantity.ToString("0.##", CultureInfo.CurrentCulture));
                        t.Cell().Background(bg).PaddingVertical(4).AlignRight().Text(item.Price.ToString("C2", CultureInfo.CurrentCulture));
                        t.Cell().Background(bg).PaddingVertical(4).AlignRight().Text(item.Amount.ToString("C2", CultureInfo.CurrentCulture));
                    }
                });

                col.Item().PaddingTop(10).AlignRight().Width(240).Column(c =>
                {
                    void Line(string label, decimal value, bool strong = false)
                    {
                        c.Item().PaddingVertical(2).Row(r =>
                        {
                            var l = r.RelativeItem().Text(label);
                            var v = r.ConstantItem(100).AlignRight().Text(value.ToString("C2", CultureInfo.CurrentCulture));
                            if (strong) { l.SemiBold(); v.SemiBold(); }
                        });
                    }
                    Line("Subtotal", invoice.NetAmount);
                    if (invoice.Discount != 0) Line("Discount", -invoice.Discount);
                    if (invoice.Tax1 != 0) Line("Tax", invoice.Tax1);
                    if (invoice.Tax2 != 0) Line("Tax 2", invoice.Tax2);
                    c.Item().PaddingVertical(3).LineHorizontal(0.75f).LineColor(Rule);
                    Line("Total", invoice.Total, strong: true);
                    if (invoice.AmountReceived != 0)
                    {
                        Line("Received", -invoice.AmountReceived);
                        Line("Balance due", invoice.Balance, strong: true);
                    }
                });

                if (!string.IsNullOrWhiteSpace(invoice.Notes))
                    col.Item().PaddingTop(20).Text(invoice.Notes!).FontColor(Muted);
            });

            page.Footer().AlignCenter().Text("Thank you!").FontColor(Muted);
        })).GeneratePdf(path);
    }

    // ------------------------------------------------------------------ shopping list

    public static void ShoppingList(string path, ShoppingList list, bool groupByStore)
    {
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(0.6f, Unit.Inch);
            page.DefaultTextStyle(t => t.FontSize(11).FontColor(Ink));
            page.Header().Column(h =>
            {
                h.Item().Text(string.IsNullOrWhiteSpace(list.Name) ? $"Shopping list — {list.Date:D}" : $"{list.Name} — {list.Date:D}").FontSize(16).SemiBold();
                h.Item().PaddingTop(6).LineHorizontal(0.75f).LineColor(Rule);
            });
            page.Content().PaddingTop(8).Column(col =>
            {
                var groups = groupByStore
                    ? list.Items.Where(i => !i.Done).GroupBy(i => string.IsNullOrWhiteSpace(i.Store) ? "Any store" : i.Store!.Trim()).OrderBy(g => g.Key == "Any store").ThenBy(g => g.Key)
                    : list.Items.Where(i => !i.Done).GroupBy(_ => "");
                foreach (var g in groups)
                {
                    if (g.Key.Length > 0) col.Item().PaddingTop(10).Text(g.Key).FontSize(12.5f).SemiBold();
                    foreach (var item in g.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        col.Item().PaddingVertical(2).Row(row =>
                        {
                            row.ConstantItem(14).PaddingTop(2).Height(10).Width(10).Border(0.75f).BorderColor(Muted);
                            row.RelativeItem(5).PaddingLeft(6).Text(item.Name);
                            row.RelativeItem(2).AlignRight().Text(QuantityText(item)).FontColor(Muted);
                            row.RelativeItem(4).PaddingLeft(12).Text(item.Notes ?? "").FontSize(9).FontColor(Muted);
                        });
                    }
                }
            });
        })).GeneratePdf(path);
    }

    public static string QuantityText(ShoppingItem i) =>
        i.Quantity is { } q ? $"{Qty(q)} {i.Unit}".Trim() : i.QuantityText ?? "";
}
