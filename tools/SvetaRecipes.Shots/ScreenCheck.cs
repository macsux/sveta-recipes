using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SvetaRecipes.App;
using SvetaRecipes.App.ViewModels;
using SvetaRecipes.App.Views;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;
using SvetaRecipes.Reports;

/// <summary>
/// The check that works on ANY data (development mode on her PC, before a change is applied): opens a copy of the
/// database headlessly, shows every screen, saves a PNG of each, and fails if anything throws. Unlike the smoke test in
/// Program.cs it expects nothing about her recipes. Usage: SvetaRecipes.Shots check &lt;database to COPY&gt; &lt;output folder&gt;
/// </summary>
internal static class ScreenCheck
{
    public static int Run(string source, string outDir)
    {
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);
        var db = Path.Combine(outDir, "check.db");
        File.Copy(source, db);

        var bindingErrors = new BindingErrors();
        Logger.Sink = bindingErrors;
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        App.ApplyTheme("Light");
        var failures = new List<string>();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            failures.Add("unhandled: " + e.Exception);
            Console.WriteLine("  FAIL unhandled " + e.Exception.Message);
            e.Handled = true;
        };

        void Pump()
        {
            for (var i = 0; i < 6; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        void Shot(Window w, string name)
        {
            Pump();
            using var frame = w.CaptureRenderedFrame();
            frame?.Save(Path.Combine(outDir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }

        void Step(string what, Action action)
        {
            try
            {
                action();
                Pump();
                Console.WriteLine($"  ok   {what}");
            }
            catch (Exception e)
            {
                failures.Add($"{what}: {e}");
                Console.WriteLine($"  FAIL {what}: {e.Message}");
            }
        }

        RecipeBook book = null!;
        Step("open (and migrate) a copy of the database", () => book = RecipeBook.Open(db));
        if (failures.Count > 0) return Done();
        book.SetSetting(SettingKeys.BackupFolder, Path.Combine(outDir, "backups"));   // never her real backup folder

        var window = new MainWindow { Width = 1366, Height = 768, WindowState = WindowState.Normal };
        MainViewModel vm = null!;
        Step("main window", () =>
        {
            vm = new MainViewModel(book, window);
            window.DataContext = vm;
            window.Show();
            Shot(window, "01-recipes");
        });
        if (failures.Count > 0) return Done();

        // A few recipes: the last edited, one with sub-recipes, the largest. Opening must not mark them modified.
        var recipes = new[]
            {
                book.Recipes.MaxBy(r => r.UpdatedAt ?? DateTime.MinValue),
                book.Recipes.FirstOrDefault(r => r.Lines.Any(l => l.Kind == LineKind.SubRecipe)),
                book.Recipes.MaxBy(r => r.Lines.Count),
            }
            .OfType<Recipe>().DistinctBy(r => r.Id).ToList();
        foreach (var (recipe, i) in recipes.Select((r, i) => (r, i)))
            Step($"recipe \"{recipe.Name}\"", () =>
            {
                vm.Recipes.SelectRecipe(recipe.Id).GetAwaiter().GetResult();
                Shot(window, $"02-recipe-{i + 1}");
                if (vm.Recipes.Editor?.IsDirty == true) throw new Exception("opening it marked it modified");
            });
        if (recipes.Count > 0)
            Step("recipe editor tabs", () =>
            {
                vm.Recipes.SelectRecipe(recipes[0].Id).GetAwaiter().GetResult();
                Pump();
                var tabs = window.GetVisualDescendants().OfType<TabControl>().Skip(1).First();
                for (var t = 1; t < tabs.ItemCount; t++)
                {
                    tabs.SelectedIndex = t;
                    Shot(window, $"03-recipe-tab-{t}");
                }
                tabs.SelectedIndex = 0;
            });
        if (recipes.Count > 0)
            Step("scale & preview", () =>
            {
                var scale = new ScaleViewModel(vm.Recipes, recipes[0]) { Mode = ScaleMode.Factor, FactorInput = 2 };
                var w = new Window { Width = 760, Height = 760, Content = scale, DataContext = scale };
                w.Show();
                Shot(w, "04-scale-preview");
                w.Close();
            });

        string[] names = ["", "05-ingredients", "06-shopping", "07-invoices", "08-companies", "09-tools"];
        for (var tab = 1; tab < names.Length; tab++)
        {
            var index = tab;
            Step($"tab {names[index][3..]}", () =>
            {
                vm.SelectedTab = index;
                Shot(window, names[index]);
            });
        }
        Step("tools & settings pages", () =>
        {
            var pages = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("large"));
            for (var p = 1; p < pages.ItemCount; p++)
            {
                pages.SelectedIndex = p;
                Shot(window, $"09-tools-{p}");
            }
            pages.SelectedIndex = 0;
        });
        if (book.Invoices.FirstOrDefault() is { } invoice)
            Step("invoice editor", () =>
            {
                var editor = new InvoiceEditorViewModel(book, vm.Dialogs, invoice.Clone(), isNew: false);
                var w = new Window { Width = 900, Height = 760, Content = editor, DataContext = editor };
                w.Show();
                Shot(w, "10-invoice-editor");
                w.Close();
            });
        Step("assistant panel", () =>
        {
            vm.SelectedTab = 0;
            vm.IsAssistantOpen = true;
            Shot(window, "11-assistant");
        });
        if (recipes.Count > 0)
            Step("recipe PDF", () =>
            {
                var calc = book.Calculator();
                Pdf.Recipe(Path.Combine(outDir, "recipe.pdf"), recipes[0], null, calc.Calculate(recipes[0]),
                    RecipeExpander.Expand(calc, recipes[0], 1, book.UnitConverter), new RecipePrintOptions(1, true, true, true));
            });
        return Done();

        int Done()
        {
            if (bindingErrors.Messages.Count > 0)
            {
                Console.WriteLine($"  binding errors ({bindingErrors.Messages.Count}; a broken {{Binding}} shows nothing instead of failing):");
                foreach (var m in bindingErrors.Messages.Distinct().Take(20)) Console.WriteLine("    " + m);
            }
            Console.WriteLine($"screenshots: {outDir}");
            Console.WriteLine(failures.Count == 0 ? "CHECK PASSED" : $"CHECK FAILED ({failures.Count})");
            return failures.Count == 0 ? 0 : 1;
        }
    }

    /// <summary>Collects Avalonia's binding errors (they are only logged, never thrown).</summary>
    private sealed class BindingErrors : ILogSink
    {
        public List<string> Messages { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values)
        {
            if (!IsEnabled(level, area)) return;
            var text = messageTemplate;
            foreach (var v in values)
            {
                var start = text.IndexOf('{');
                var end = start < 0 ? -1 : text.IndexOf('}', start);
                if (end < 0) break;
                text = text[..start] + v + text[(end + 1)..];
            }
            lock (Messages) Messages.Add($"{source?.GetType().Name}: {text}");
        }
    }
}
