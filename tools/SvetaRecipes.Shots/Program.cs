using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SvetaRecipes.App;
using SvetaRecipes.App.Services;
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
// Or:    SvetaRecipes.Shots check <database to COPY> <output folder>   (any data: every screen, fails on errors; ScreenCheck.cs)
if (args[0] == "check") return ScreenCheck.Run(args[1], args[2]);
// Or:    SvetaRecipes.Shots devmode <database to COPY> <work folder> [--live]   (development mode end to end; DevModeTest.cs)
if (args[0] == "devmode") return DevModeTest.Run(args[1], args[2], args.Contains("--live"));
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
if (Directory.Exists(Path.Combine(outDir, "assistant"))) Directory.Delete(Path.Combine(outDir, "assistant"), recursive: true);   // saved chats
var failures = new List<string>();
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "  ok  " : "  FAIL")} {what}"); if (!ok) failures.Add(what); }

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
App.ApplyTheme(args.Length > 2 ? args[2] : "Light");

var book = RecipeBook.Open(db);
book.SetSetting(SettingKeys.BackupFolder, Path.Combine(outDir, "backups"));   // not her real Documents folder
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
var toolTabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("large"));
toolTabs.SelectedIndex = toolTabs.ItemCount - 2;
Shot(window, "07c-assistant-instructions");
// Development mode's source folder: the dialog (default suggested; a checkout like this repo is used as it is).
foreach (var (folder, name) in new[] { (DevMode.DefaultSourceDir, "07d-source-folder-default"), (Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")), "07e-source-folder-existing") })
{
    var folderVm = new SourceFolderViewModel(vm.Dialogs, folder, "Where development mode keeps the app's source code.", "Switch to development");
    var folderWindow = new Window { Width = 620, Height = 340, Content = folderVm, DataContext = folderVm };
    folderWindow.Show();
    Shot(folderWindow, name);
    Check(name.EndsWith("existing") ? folderVm.Note.Contains("used as it is") : folderVm.IsUsable, $"source folder dialog: {folderVm.Note}");
    folderWindow.Close();
}
vm.Tools.AssistantInstructions = "Always answer in French.";
Check(Assistant.SystemPrompt(book).StartsWith("Always answer in French.") && Assistant.SystemPrompt(book).Contains("RecipeLines(")
      && vm.Tools.IsCustomInstructions, "edited assistant instructions are used, with the schema still appended");
vm.Tools.ResetInstructionsCommand.Execute(null);
Check(book.GetSetting(SettingKeys.AssistantPrompt) is null && Assistant.SystemPrompt(book).StartsWith(Assistant.DefaultInstructions),
    "Reset to default goes back to the built-in instructions");
toolTabs.SelectedIndex = toolTabs.ItemCount - 1;
Shot(window, "07b-about");
Check(vm.Tools.ReleaseNotes.Contains("## 1.0.0") && !vm.Tools.ReleaseNotes.StartsWith("# "), "About shows the release history (CHANGELOG.md is built in)");
toolTabs.SelectedIndex = 0;

// A downloaded update: the bar offers a restart; "Later" hides it.
Updates.MarkReady("9.9.9");
Pump();
Check(vm.IsUpdateReady && vm.UpdateReady == "9.9.9", "a downloaded update shows the restart bar");
Shot(window, "08-update-ready");
vm.DismissUpdateCommand.Execute(null);
Check(!vm.IsUpdateReady, "Later hides the update bar");
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

// ---------------------------------------------------------------- assistant
vm.SelectedTab = 0;
vm.Recipes.SelectRecipe(cremeux.Id).GetAwaiter().GetResult();
vm.IsAssistantOpen = true;
Shot(window, "11-assistant-empty");
Check(vm.DescribeView().Contains($"recipe {cremeux.Id}"), $"the assistant is told what's open ({vm.DescribeView()})");

// "Point at part of the app": hover outlines the element, the wheel widens it, a click attaches a marked screenshot.
var servingsLabel = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Servings");
var servingsBox = ((Grid)servingsLabel.GetVisualParent()!).Children.OfType<TextBox>().First(t => Grid.GetRow(t) == 2 && Grid.GetColumn(t) == 1);
var at = servingsBox.TranslatePoint(new Point(servingsBox.Bounds.Width / 2, servingsBox.Bounds.Height / 2), window)!.Value;
var picking = window.PickAsync(vm.Assistant.PicksFolder);
Pump();
window.MouseMove(at);
Shot(window, "17-pick-hover");
var overlay = window.GetVisualDescendants().OfType<PickOverlay>().Single();
window.MouseWheel(at, new Vector(0, 1));
Shot(window, "17b-pick-wider");
window.MouseWheel(at, new Vector(0, -1));
window.MouseDown(at, MouseButton.Left);
window.MouseUp(at, MouseButton.Left);
Pump();
var picked = picking.IsCompletedSuccessfully ? picking.Result : null;
Check(picked is not null && File.Exists(picked.ImagePath) && File.Exists(picked.CloseUpPath), "clicking captures a marked screenshot and a close-up");
Check(picked?.Summary == "TextBox \"Servings\"", $"the picked element is named by its label ({picked?.Summary})");
Check(picked?.Description.Contains("In view: RecipeEditorView") == true && picked.Description.Contains("Servings"), "the description says which view and field");
Console.WriteLine(string.Join("\n", (picked?.Description ?? "").Split('\n').Select(l => "      " + l)));
Check(!window.GetVisualDescendants().OfType<PickOverlay>().Any(), "the overlay goes away after the click");
if (picked is not null)
{
    File.Copy(picked.ImagePath, Path.Combine(outDir, "17c-pick-marked.png"), overwrite: true);
    File.Copy(picked.CloseUpPath, Path.Combine(outDir, "17d-pick-closeup.png"), overwrite: true);
}
if (picked is not null) vm.Assistant.Attach(picked);
// A second pick: several parts can go with one message.
var second = window.PickAsync(vm.Assistant.PicksFolder);
Pump();
var saveButton = window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Save");
var saveAt = saveButton.TranslatePoint(new Point(saveButton.Bounds.Width / 2, saveButton.Bounds.Height / 2), window)!.Value;
window.MouseMove(saveAt);
window.MouseDown(saveAt, MouseButton.Left);
window.MouseUp(saveAt, MouseButton.Left);
Pump();
if (second.IsCompletedSuccessfully && second.Result is { } secondPick) vm.Assistant.Attach(secondPick);
Check(vm.Assistant.Attachments.Count == 2 && vm.Assistant.Attachments[1].Area.Summary == "Button \"Save\"",
    $"a second pick is added, not replacing the first ({string.Join(", ", vm.Assistant.Attachments.Select(a => a.Area.Summary))})");
Shot(window, "18-pick-attached");
var cancelled = window.PickAsync(vm.Assistant.PicksFolder);
Pump();
window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
Pump();
Check(cancelled.IsCompletedSuccessfully && cancelled.Result is null, "Esc cancels pointing");

if (args.Contains("--live"))
{
    // Real conversations through the Claude Code CLI (needs it installed and logged in; costs a little).
    string Ask(string question)
    {
        vm.Assistant.Draft = question;
        var send = vm.Assistant.SendCommand.ExecuteAsync(null);
        var deadline = DateTime.Now.AddMinutes(4);
        while (!send.IsCompleted && DateTime.Now < deadline) { Pump(); Thread.Sleep(50); }
        Pump();
        var reply = string.Join("\n", vm.Assistant.Items.Reverse().TakeWhile(i => i is not UserChatItem).Reverse()
            .Select(i => i switch { AssistantChatItem a => a.Text, ToolChatItem t => $"[{t.Tool}{(t.Failed ? " FAILED" : "")}] {t.Input}\n      → {(t.Result ?? "(no result)").Split('\n').FirstOrDefault()}", ErrorChatItem e => "ERROR " + e.Text, _ => "" }));
        Console.WriteLine($"  > {question}\n{string.Join("\n", reply.Split('\n').Select(l => "    " + l))}");
        return reply;
    }

    var pointReply = Ask("What are the two things I'm pointing at? One short sentence each.");
    Check(pointReply.Contains("servings", StringComparison.OrdinalIgnoreCase) && pointReply.Contains("save", StringComparison.OrdinalIgnoreCase),
        "live: the assistant sees both parts of the app she pointed at");
    Shot(window, "12a-assistant-pointed");

    var costReply = Ask("What does this recipe cost per serving?");
    Check(costReply.Contains("25.72"), "live: the open recipe's cost per serving comes from get_costing ($25.72)");
    Shot(window, "12-assistant-cost");

    var recipesBefore = book.Recipes.Count;
    var importReply = Ask("""
        Please add this recipe:
        Caramel chocolate cremeux
        100 g whipping cream 35%
        20 g egg yolks
        10 g white sugar
        1 g fish gelatin
        70 g milk chocolate
        """);
    Check(book.Recipes.Count == recipesBefore && importReply.Contains("Caramel choc cremeux M", StringComparison.OrdinalIgnoreCase),
        "live: a pasted copy of an existing recipe is recognised as a duplicate and not imported");
    Check(importReply.Contains("[find_similar]"), "live: the duplicate check used find_similar");
    foreach (var t in vm.Assistant.Items.OfType<ToolChatItem>().Where(t => t.Tool == "find_similar")) t.IsExpanded = true;
    Shot(window, "13-assistant-duplicate");

    var pasted = Ask("""
        Passion fruit shortbread
        200 g butter
        100 g icing sugar
        300 g all-purpose flour
        60 g passion fruit purée
        1 pinch salt
        """);
    Check(book.Recipes.Count == recipesBefore, "live: a recipe pasted without asking is checked but not added");

    var variantReply = Ask("""
        Please add this one, from a magazine:
        Passion fruit shortbread
        200 g butter
        100 g icing sugar
        300 g all-purpose flour
        60 g passion fruit purée
        1 pinch salt
        """);
    Check(book.Recipes.Count == recipesBefore + 1 && book.Tags.Any(t => t.Name == "Imported"), "live: a new recipe is imported, tagged Imported");
    Shot(window, "14-assistant-import");

    // Saved and reopened: a fresh panel shows the same conversation and continues the same Claude session.
    var saved = vm.Assistant.Items.Count;
    vm.Assistant.ShowToolCalls = false;
    Shot(window, "15-assistant-tools-hidden");
    Check(vm.Assistant.Items.OfType<ToolChatItem>().All(t => !t.IsShown), "live: the Tool calls toggle hides tool calls");
    vm.Assistant.ShowToolCalls = true;
    var reopened2 = new ChatViewModel(vm);
    Check(reopened2.Items.Count == saved, $"live: the conversation is saved and reopens ({reopened2.Items.Count} of {saved} items)");
    var closing = vm.Assistant.DisposeAsync().AsTask();   // as on window close; the client needs the dispatcher to shut down
    while (!closing.IsCompleted) { Pump(); Thread.Sleep(50); }
    var remember = Ask("What was the name of the recipe you just added? Answer with the name only.");
    Check(remember.Contains("shortbread", StringComparison.OrdinalIgnoreCase), "live: a follow-up remembers the conversation");

    Ask("Search the web: what temperature range is usually given for tempering dark chocolate? One line.");
    Check(vm.Assistant.Items.OfType<ToolChatItem>().Any(t => t.Tool is "WebSearch" or "WebFetch"), "live: the assistant can search the web");
    Shot(window, "16-assistant-web");
}

Console.WriteLine(failures.Count == 0 ? "ALL PASSED" : $"{failures.Count} FAILED");
return failures.Count == 0 ? 0 : 1;
