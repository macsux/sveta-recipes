using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SvetaRecipes.App;
using SvetaRecipes.App.ViewModels;
using SvetaRecipes.App.Views;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;
using SvetaRecipes.Reports;

// Renders the real UI headlessly (no display needed) and drives the main flows as a smoke test.
// Usage: SvetaRecipes.Shots <database to COPY> <output folder> [Light|Dark]
// The database is copied first; the original is never modified.
// Or:    SvetaRecipes.Shots compare <database> <legacy screenshots folder> <output folder>   (old → new side by side)
if (args[0] == "compare")
{
    AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
    App.ApplyTheme("Light");
    return Compare.Run(args[1], args[2], args[3]);
}

var (source, outDir) = (args[0], args[1]);
Directory.CreateDirectory(outDir);
var db = Path.Combine(outDir, "smoke.db");
File.Copy(source, db, overwrite: true);
var failures = new List<string>();
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "  ok  " : "  FAIL")} {what}"); if (!ok) failures.Add(what); }

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
App.ApplyTheme(args.Length > 2 ? args[2] : "Light");

var book = RecipeBook.Open(db);
var window = new MainWindow { Width = 1366, Height = 768, WindowState = WindowState.Normal };
var vm = new MainViewModel(book, window);
window.DataContext = vm;
window.Show();

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
    w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"), 100);
    Console.WriteLine($"  shot {name}");
}

// ---------------------------------------------------------------- recipes
Shot(window, "01-recipes-empty");
var cremeux = book.Recipes.First(r => r.Name == "Caramel choc cremeux M");
vm.Recipes.SelectRecipe(cremeux.Id).GetAwaiter().GetResult();
Shot(window, "02-recipe-open");
Check(decimal.Round(vm.Recipes.Editor!.TotalCost, 2) == 25.72m, "Caramel choc cremeux costs $25.72 as on the legacy screen");

var withSubs = book.Recipes.Where(r => r.Lines.Count(l => l.Kind == LineKind.SubRecipe) >= 2).OrderByDescending(r => r.Lines.Count).First();
vm.Recipes.SelectRecipe(withSubs.Id).GetAwaiter().GetResult();
Shot(window, "03-recipe-with-subrecipes");
Check(!vm.Recipes.Editor!.IsDirty, "opening a recipe does not mark it modified");

// Edit → save → reload persists.
var editor = vm.Recipes.Editor!;
var firstIngredient = editor.Lines.First(l => l.Kind == LineKind.Ingredient);
var before = editor.TotalCost;
firstIngredient.Amount += 100;
Check(editor.IsDirty && editor.TotalCost > before, "editing a quantity marks dirty and raises the cost live");
editor.Save().GetAwaiter().GetResult();
var reopened = RecipeBook.Open(db).FindRecipe(withSubs.Id)!;
Check(Math.Abs(reopened.Lines.First(l => l.Kind == LineKind.Ingredient).Amount - firstIngredient.Amount) < 1e-9, "saved quantity survives a reload");

// A price change reaches a parent through its sub-recipe without opening the sub-recipe.
var subLine = withSubs.Lines.First(l => l.Kind == LineKind.SubRecipe);
var sub = book.FindRecipe(subLine.SubRecipeId!.Value)!;
var subIngredient = book.FindIngredient(sub.Lines.First(l => l.Kind == LineKind.Ingredient).IngredientId!.Value)!.Clone();
var parentBefore = book.Calculator().Calculate(withSubs.Id)!.TotalCost;
subIngredient.PackPrice *= 2;
book.SaveIngredient(subIngredient);
var parentAfter = book.Calculator().Calculate(withSubs.Id)!.TotalCost;
Check(parentAfter > parentBefore, $"doubling '{subIngredient.Name}' (inside '{sub.Name}') raises '{withSubs.Name}' {parentBefore:C2} → {parentAfter:C2}");

// Editor tabs
var tabs = window.GetVisualDescendants().OfType<TabControl>().Skip(1).First(); // the editor's tab strip
foreach (var (i, name) in new[] { (1, "03b-method"), (2, "03c-label"), (3, "03d-pan-layers"), (4, "03e-tags") })
{
    tabs.SelectedIndex = i;
    Shot(window, name);
}
tabs.SelectedIndex = 0;

// Scale & preview window
var scale = new ScaleViewModel(vm.Recipes, withSubs) { Mode = ScaleMode.Factor, FactorInput = 2 };
var scaleWindow = new Window { Width = 760, Height = 760, Content = scale, DataContext = scale };
scaleWindow.Show();
Shot(scaleWindow, "08-scale-preview");
Check(Math.Abs(scale.Factor - 2) < 1e-9 && scale.Lines.Count > withSubs.Lines.Count, "scale ×2 previews with sub-recipe lines expanded");
scaleWindow.Close();

// ---------------------------------------------------------------- shopping from recipes
var items = ShoppingBuilder.FromRecipes(book, [(withSubs, 1.0), (cremeux, 2.0)]);
var list = book.SaveShoppingList(new ShoppingList { Date = DateTime.Today, Name = "Smoke test", Items = items });
Check(items.Count > 0 && items.Select(i => (i.IngredientId, i.Unit)).Distinct().Count() == items.Count, $"shopping list from 2 recipes: {items.Count} merged items");
vm.GoToShoppingList(list.Id);
Shot(window, "05-shopping");

// ---------------------------------------------------------------- other tabs
foreach (var (tab, name) in new[] { (1, "04-ingredients"), (3, "09-invoices"), (4, "06-companies"), (5, "07-tools") })
{
    vm.SelectedTab = tab;
    Shot(window, name);
}
vm.GoToIngredient(subIngredient.Id);
Shot(window, "04b-ingredient-selected");

// ---------------------------------------------------------------- invoices and companies
// (Invoice totals vs the legacy data are checked by LegacyParityTests.)
var nina = book.Companies.Where(c => c.IsCustomer).OrderByDescending(c => book.Invoices.Count(i => i.CompanyId == c.Id)).First();
Check(!book.DeleteCompany(nina.Id), "a customer with invoices can't be deleted");
var newInvoice = book.SaveInvoice(new Invoice
{
    Date = DateTime.Today, CompanyId = nina.Id, Discount = 10,
    Items = [new() { Name = "Caramel choc cremeux M", Price = 6.5m, Quantity = 12 }, new() { Name = "Delivery", Price = 15, Quantity = 1 }],
});
Check(newInvoice.Id == book.Invoices.Max(i => i.Id) && newInvoice.Total == 83m, $"new invoice numbered #{newInvoice.Id:0000}, total {newInvoice.Total:C2} (78 + 15 − 10)");
Pdf.Invoice(Path.Combine(outDir, "invoice.pdf"), newInvoice, nina, InvoicePrinting.Business(book));
var editorVm = new InvoiceEditorViewModel(book, vm.Dialogs, book.Invoices.Where(i => i.Id != newInvoice.Id).MaxBy(i => i.Id)!.Clone(), isNew: false);
var invoiceWindow = new Window { Width = 900, Height = 760, Content = editorVm, DataContext = editorVm };
invoiceWindow.Show();
Shot(invoiceWindow, "10-invoice-editor");
invoiceWindow.Close();
vm.SelectedTab = 3;
Shot(window, "09-invoices");

// Component edit mode: change a line's ingredient through the add bar.
vm.SelectedTab = 0;
vm.Recipes.SelectRecipe(cremeux.Id).GetAwaiter().GetResult();
var ed = vm.Recipes.Editor!;
ed.SelectedLine = ed.Lines.First(l => l.Name == "Sugar");
ed.EditLineCommand.Execute(null);
Check(ed.EditingLine is not null && ed.AddPick?.Name == "Sugar" && ed.AddPreview.StartsWith("≈"), $"Edit loads the line into the add bar with a cost preview ({ed.AddPreview})");
ed.AddRecipeMode = false;
ed.AddPick = ed.AddCandidates.First(p => p.Name == "Sugar Brown");
ed.AddAmount = "10";
ed.AddLineCommand.Execute(null);
Check(ed.Lines.Count == cremeux.Lines.Count && ed.Lines.Any(l => l.Name == "Sugar Brown") && ed.Lines.All(l => l.Name != "Sugar"), "Update replaces the line in place");
ed.Revert();

// ---------------------------------------------------------------- PDFs
var calc = book.Calculator();
Pdf.Recipe(Path.Combine(outDir, "recipe.pdf"), withSubs, null, calc.Calculate(withSubs),
    RecipeExpander.Expand(calc, withSubs, 1.5, book.UnitConverter), new RecipePrintOptions(1.5, true, true, true));
Pdf.Labels(Path.Combine(outDir, "labels.pdf"),
    book.Recipes.Where(r => r.LabelText is not null).Take(12).Select(r => (r, 1)).ToList(), new LabelLayout());
Pdf.ShoppingList(Path.Combine(outDir, "shopping.pdf"), list, groupByStore: true);
Pdf.Labels(Path.Combine(outDir, "labels-copies.pdf"), [(cremeux, 3)], new LabelLayout());
Check(new[] { "recipe.pdf", "labels.pdf", "shopping.pdf", "invoice.pdf", "labels-copies.pdf" }.All(f => new FileInfo(Path.Combine(outDir, f)).Length > 1000), "recipe, label (with copies), shopping and invoice PDFs generated");

Console.WriteLine(failures.Count == 0 ? "ALL PASSED" : $"{failures.Count} FAILED");
return failures.Count == 0 ? 0 : 1;
