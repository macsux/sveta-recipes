using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using SvetaRecipes.App.ViewModels;
using SvetaRecipes.App.Views;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;

/// <summary>
/// Recreates each legacy screenshot's screen in the new app (same recipe, list, supplier…) and writes side-by-side
/// "Access -> new" images. Usage: SvetaRecipes.Shots compare &lt;database&gt; &lt;legacy screenshots folder&gt; &lt;output folder&gt;
/// </summary>
static class Compare
{
    private sealed record Pair(string Old, string Title, string Note, Func<Window?> Arrange);

    public static int Run(string db, string legacyDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(outDir, "compare.db");
        File.Copy(db, work, overwrite: true);

        var book = RecipeBook.Open(work);
        var window = new MainWindow { Width = 1366, Height = 768, WindowState = WindowState.Normal };
        var vm = new MainViewModel(book, window);
        window.DataContext = vm;
        window.Show();

        var cremeux = book.Recipes.First(r => r.Name == "Caramel choc cremeux M");
        TabControl ToolsTabs() => window.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("large"));
        Window? Main() => window;

        Window Scale()
        {
            var s = new ScaleViewModel(vm.Recipes, cremeux) { Mode = ScaleMode.Amount, TargetAmount = 402 };
            var w = new Window { Width = 760, Height = 700, Content = s, DataContext = s };
            w.Show();
            return w;
        }

        var pairs = new List<Pair>
        {
            new("01.png", "Main screen — recipes", "Same recipe, same numbers ($5.72 components, $25.72 total). Tree on the left, header + costs, component grid with % and $.",
                () => { vm.SelectedTab = 0; vm.Recipes.SelectRecipe(cremeux.Id).GetAwaiter().GetResult(); return Main(); }),
            new("02.png", "Add recipe component", "The pop-up became an inline bar: Ingredient | Recipe, type to find, qty, unit, notes, cost preview (as the old Cost box). Edit or double-click a line to change it.",
                () =>
                {
                    var e = vm.Recipes.Editor!;
                    e.AddRecipeMode = false;
                    e.AddPick = e.AddCandidates.First(p => p.Name == "Cream 35%");
                    e.AddAmount = "50";
                    e.AddNotes = "whipped";
                    return Main();
                }),
            new("03.png", "Scale recipe up or down", "Scale & preview window: by amount, servings, another pan, or a factor. Sub-recipes are expanded under the line that uses them. Prints to PDF.",
                Scale),
            new("04.png", "Ingredients", "Same datasheet idea: saves as you leave a row. $/kg, number of recipes and suppliers per ingredient; details below.",
                () => { vm.SelectedTab = 1; return Main(); }),
            new("05.png", "Shopping lists", "Same list (6 Nov 2010). Grouped by store; quantities in one field; build from recipes (scaled, totalled); print or copy as text.",
                () => { vm.GoToShoppingList(book.ShoppingLists.First(l => l.Date.Date == new DateTime(2010, 11, 6)).Id); return Main(); }),
            new("06.png", "Invoices", "Same invoices and the same totals as the old screen. Received and Paid edit in place; search by item, customer or number.",
                () => { vm.SelectedTab = 3; return Main(); }),
            new("07.png", "Customers / suppliers", "Customers and suppliers (filter like the old Customers / Suppliers / Both). A customer shows its invoices; a supplier its prices.",
                () =>
                {
                    vm.SelectedTab = 4;
                    Pump();
                    // The supplier with the most prices, like the expanded row in the old screenshot.
                    vm.Companies.Select(book.Companies.Where(c => c.IsSupplier).OrderByDescending(c => c.Prices.Count).First().Id);
                    return Main();
                }),
            new("08.png", "Tools menu", "The Tools menu became the “Tools & settings” tab: settings, your business details, calculator, backups; one sub-tab per tool.",
                () => { vm.SelectedTab = 5; Pump(); ToolsTabs().SelectedIndex = 0; return Main(); }),
            new("09.png", "Units of measurement", "Units and conversions side by side, edited in place. Conversions also work in reverse and in two steps.",
                () => { vm.SelectedTab = 5; Pump(); ToolsTabs().SelectedIndex = 1; return Main(); }),
            new("10.png", "Quick conversion", "Now a strip under the units grids: amount, from, to — answers instantly (cup -> ml shown).",
                () =>
                {
                    vm.SelectedTab = 5; Pump(); ToolsTabs().SelectedIndex = 1;
                    vm.Tools.TestFrom = "cup"; vm.Tools.TestTo = "ml"; vm.Tools.TestAmount = 2;
                    return Main();
                }),
            new("11.png", "Substance conversion", "“Volume <-> weight” tab: same 181 densities (editable), converter on the right.",
                () =>
                {
                    vm.SelectedTab = 5; Pump(); ToolsTabs().SelectedIndex = 2; Pump();
                    vm.Tools.Substance = vm.Tools.SubstanceRows.First(s => s.Name == "almonds, ground");
                    vm.Tools.DensityQty = 1;
                    return Main();
                }),
            new("12.png", "Lookup tables", "“Lists” tab: the same four lists, with how many recipes/ingredients use each entry.",
                () =>
                {
                    vm.SelectedTab = 5; Pump(); ToolsTabs().SelectedIndex = 3;
                    vm.Tools.Lookup = vm.Tools.LookupLists.First(l => l.Value == LookupList.GroceryCategories);
                    return Main();
                }),
        };

        for (var i = 0; i < pairs.Count; i++)
        {
            var p = pairs[i];
            var target = p.Arrange();
            SKBitmap? newShot = null;
            if (target is not null)
            {
                Pump();
                using var frame = target.CaptureRenderedFrame()!;
                using var ms = new MemoryStream();
                frame.Save(ms);
                newShot = SKBitmap.Decode(ms.ToArray());
                if (target != window) target.Close();
            }
            using var oldShot = SKBitmap.Decode(Path.Combine(legacyDir, p.Old));
            var file = Path.Combine(outDir, $"{i + 1:00} {Slug(p.Title)}.png");
            Compose(oldShot, newShot, $"{i + 1}. {p.Title}", p.Note, file);
            newShot?.Dispose();
            Console.WriteLine("  " + Path.GetFileName(file));
        }
        File.Delete(work);
        return 0;
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static string Slug(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : '-').ToArray());

    // ------------------------------------------------------------------ composition

    private const int Pad = 32, Gap = 40, Head = 96, Caption = 44;
    private static readonly SKColor Bg = SKColor.Parse("#F6F6F7");
    private static readonly SKColor Ink = SKColor.Parse("#1B1C1F");
    private static readonly SKColor Muted = SKColor.Parse("#6B6E75");
    private static readonly SKColor Rule = SKColor.Parse("#D6D7DB");
    private static readonly SKColor Accent = SKColor.Parse("#7C9BF7");

    private static void Compose(SKBitmap old, SKBitmap? @new, string title, string note, string path)
    {
        int newW = @new?.Width ?? 1366, newH = @new?.Height ?? 400;
        var paneH = Math.Max(old.Height, newH);
        var width = Pad + old.Width + Gap + newW + Pad;
        var height = Head + Caption + paneH + Pad;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var c = surface.Canvas;
        c.Clear(Bg);
        using var sans = SKTypeface.FromFamilyName("Helvetica Neue") ?? SKTypeface.Default;
        using var bold = SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var titleFont = new SKFont(bold, 30);
        using var noteFont = new SKFont(sans, 19);
        using var labelFont = new SKFont(bold, 17);
        using var ink = new SKPaint { Color = Ink, IsAntialias = true };
        using var muted = new SKPaint { Color = Muted, IsAntialias = true };
        using var accent = new SKPaint { Color = Accent, IsAntialias = true };

        c.DrawText(title, Pad, 46, titleFont, ink);
        c.DrawText(note, Pad, 78, noteFont, muted);

        var top = Head + Caption;
        c.DrawText("ACCESS (OLD)", Pad, top - 14, labelFont, muted);
        c.DrawText("NEW", Pad + old.Width + Gap, top - 14, labelFont, accent);
        Frame(c, old, Pad, top);

        var x = Pad + old.Width + Gap;
        if (@new is not null)
        {
            Frame(c, @new, x, top);
        }
        else
        {
            using var box = new SKPaint { Color = Rule, IsStroke = true, StrokeWidth = 2, PathEffect = SKPathEffect.CreateDash([10, 8], 0), IsAntialias = true };
            c.DrawRoundRect(new SKRect(x, top, x + newW, top + 360), 12, 12, box);
            using var big = new SKFont(bold, 28);
            c.DrawText("Not carried over", x + 40, top + 160, big, ink);
            c.DrawText("Invoices and customers were dropped: Sveta is paid in cash.", x + 40, top + 200, noteFont, muted);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void Frame(SKCanvas c, SKBitmap bmp, int x, int y)
    {
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 40), ImageFilter = SKImageFilter.CreateBlur(8, 8), IsAntialias = true };
        c.DrawRect(new SKRect(x, y + 4, x + bmp.Width, y + bmp.Height + 4), shadow);
        c.DrawBitmap(bmp, x, y);
        using var border = new SKPaint { Color = Rule, IsStroke = true, StrokeWidth = 1 };
        c.DrawRect(new SKRect(x, y, x + bmp.Width, y + bmp.Height), border);
    }
}
