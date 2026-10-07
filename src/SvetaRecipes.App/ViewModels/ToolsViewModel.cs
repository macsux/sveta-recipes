using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.App.Services;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.ViewModels;

public partial class UnitRow(MeasureUnit unit) : ObservableObject
{
    public MeasureUnit Unit { get; } = unit;
    [ObservableProperty] private string _code = unit.Code;
    [ObservableProperty] private string _description = unit.Description;
    [ObservableProperty] private bool _isVolume = unit.IsVolume;
}

public partial class ConversionRow(UnitConversion c) : ObservableObject
{
    public (string From, string To) Key { get; set; } = (c.From, c.To);
    [ObservableProperty] private string _from = c.From;
    [ObservableProperty] private string _to = c.To;
    [ObservableProperty] private double _factor = c.Factor;
}

public partial class NamedRow(int id, string name, int uses) : ObservableObject
{
    public int Id { get; set; } = id;
    [ObservableProperty] private string _name = name;
    public int Uses { get; } = uses;
}

public partial class SubstanceRow(Substance s) : ObservableObject
{
    public Substance Substance { get; } = s;
    [ObservableProperty] private string _name = s.Name;
    [ObservableProperty] private double _density = s.Density;
}

public sealed record BackupFile(string Path, string Name, string When, string Size);

public enum LookupList { RecipeCategories, Tags, GroceryCategories, SupplierCategories }

public partial class ToolsViewModel : ViewModelBase, ISection
{
    private readonly MainViewModel _main;
    private int _seenVersion = -1;
    private bool _loading;

    public ToolsViewModel(MainViewModel main)
    {
        _main = main;
        _lookup = LookupLists[0];
    }

    private RecipeBook Book => _main.Book;
    /// <summary>Development mode's settings (the source folder) on the Assistant page.</summary>
    public DevViewModel Dev => _main.Dev;
    private IDialogs Dialogs => _main.Dialogs;

    public void Activate()
    {
        if (_seenVersion == Book.Version) return;
        _seenVersion = Book.Version;
        _loading = true;
        LabourRate = Book.LabourRate;
        Theme = Book.GetSetting(SettingKeys.Theme) ?? "Light";
        LabelColumns = int.TryParse(Book.GetSetting(SettingKeys.LabelColumns), out var c) ? c : 2;
        LabelRows = int.TryParse(Book.GetSetting(SettingKeys.LabelRows), out var r) ? r : 5;
        BackupFolder = Book.GetSetting(SettingKeys.BackupFolder) is { Length: > 0 } f ? f : AppPaths.DefaultBackupFolder;
        PrintFontSize = double.TryParse(Book.GetSetting(SettingKeys.PrintFontSize), NumberStyles.Float, CultureInfo.InvariantCulture, out var fs) ? fs : 10.5;
        BusinessName = Book.GetSetting(SettingKeys.BusinessName);
        BusinessAddress1 = Book.GetSetting(SettingKeys.BusinessAddress1);
        BusinessAddress2 = Book.GetSetting(SettingKeys.BusinessAddress2);
        BusinessCity = Book.GetSetting(SettingKeys.BusinessCity);
        BusinessProvince = Book.GetSetting(SettingKeys.BusinessProvince);
        BusinessPostal = Book.GetSetting(SettingKeys.BusinessPostal);
        BusinessPhone = Book.GetSetting(SettingKeys.BusinessPhone);
        BusinessEmail = Book.GetSetting(SettingKeys.BusinessEmail);
        AssistantInstructions = Assistant.Instructions(Book);
        _loading = false;
        LoadUnits();
        LoadLookup();
        LoadSubstances();
        LoadBackups();
        OnPropertyChanged(nameof(UpdateStatus));
    }

    // ------------------------------------------------------------------ settings

    public IReadOnlyList<string> Themes { get; } = ["Light", "Dark", "System"];
    [ObservableProperty] private decimal _labourRate;
    [ObservableProperty] private string _theme = "Light";
    [ObservableProperty] private int _labelColumns = 2;
    [ObservableProperty] private int _labelRows = 5;
    [ObservableProperty] private string _backupFolder = "";
    public string DatabasePath => Book.DbPath;
    public string? BackupError => BackupStatus.LastError;

    partial void OnLabourRateChanged(decimal value)
    {
        if (!_loading && value >= 0) { Book.LabourRate = value; _seenVersion = Book.Version; }
    }

    partial void OnThemeChanged(string value)
    {
        if (_loading) return;
        App.ApplyTheme(value);
        Book.SetSetting(SettingKeys.Theme, value);
        _seenVersion = Book.Version;
    }

    partial void OnLabelColumnsChanged(int value)
    {
        if (!_loading && value is > 0 and <= 6) { Book.SetSetting(SettingKeys.LabelColumns, value.ToString()); _seenVersion = Book.Version; }
    }

    partial void OnLabelRowsChanged(int value)
    {
        if (!_loading && value is > 0 and <= 20) { Book.SetSetting(SettingKeys.LabelRows, value.ToString()); _seenVersion = Book.Version; }
    }

    // ------------------------------------------------------------------ your business (printed on invoices)

    [ObservableProperty] private string? _businessName;
    [ObservableProperty] private string? _businessAddress1;
    [ObservableProperty] private string? _businessAddress2;
    [ObservableProperty] private string? _businessCity;
    [ObservableProperty] private string? _businessProvince;
    [ObservableProperty] private string? _businessPostal;
    [ObservableProperty] private string? _businessPhone;
    [ObservableProperty] private string? _businessEmail;

    partial void OnBusinessNameChanged(string? value) => SaveSetting(SettingKeys.BusinessName, value);
    partial void OnBusinessAddress1Changed(string? value) => SaveSetting(SettingKeys.BusinessAddress1, value);
    partial void OnBusinessAddress2Changed(string? value) => SaveSetting(SettingKeys.BusinessAddress2, value);
    partial void OnBusinessCityChanged(string? value) => SaveSetting(SettingKeys.BusinessCity, value);
    partial void OnBusinessProvinceChanged(string? value) => SaveSetting(SettingKeys.BusinessProvince, value);
    partial void OnBusinessPostalChanged(string? value) => SaveSetting(SettingKeys.BusinessPostal, value);
    partial void OnBusinessPhoneChanged(string? value) => SaveSetting(SettingKeys.BusinessPhone, value);
    partial void OnBusinessEmailChanged(string? value) => SaveSetting(SettingKeys.BusinessEmail, value);

    private void SaveSetting(string key, string? value)
    {
        if (_loading) return;
        Book.SetSetting(key, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        _seenVersion = Book.Version;
    }

    // ------------------------------------------------------------------ assistant instructions

    /// <summary>The assistant's system prompt, minus the generated schema. Stored only when it differs from the default.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCustomInstructions))] private string _assistantInstructions = "";
    public bool IsCustomInstructions => AssistantInstructions != Assistant.DefaultInstructions;

    partial void OnAssistantInstructionsChanged(string value)
    {
        if (_loading) return;
        SaveSetting(SettingKeys.AssistantPrompt, value == Assistant.DefaultInstructions ? null : value);
        _ = _main.Assistant.InstructionsChanged();
    }

    [RelayCommand]
    private void ResetInstructions() => AssistantInstructions = Assistant.DefaultInstructions;

    // ------------------------------------------------------------------ printing, tools, about

    [ObservableProperty] private double _printFontSize = 10.5;

    partial void OnPrintFontSizeChanged(double value)
    {
        if (!_loading && value is >= 7 and <= 20) SaveSetting(SettingKeys.PrintFontSize, value.ToString(CultureInfo.InvariantCulture));
    }

    public string UpdateStatus => Services.Updates.Status;

    public string Version => "Version " + Services.Updates.VersionText + (Services.DevMode.RunningBuild is { } b ? $", development build {b}" : "");

    /// <summary>CHANGELOG.md (built into the app) without its title: one section per release.</summary>
    public string ReleaseNotes { get; } = LoadReleaseNotes();

    private static string LoadReleaseNotes()
    {
        using var stream = typeof(ToolsViewModel).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return "";
        var text = new StreamReader(stream).ReadToEnd().ReplaceLineEndings("\n");
        return text.StartsWith("# ") ? text[(text.IndexOf('\n') + 1)..].TrimStart() : text;
    }

    [RelayCommand]
    private void OpenCalculator()
    {
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("calc.exe") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", "-a Calculator");
        }
        catch { /* no calculator available; nothing to do */ }
    }

    /// <summary>The legacy "online conversions" page, now in the normal browser.</summary>
    [RelayCommand]
    private void OpenOnlineConverter() => Launcher.Open("https://www.onlineconversion.com/weight_volume_cooking.htm");

    [RelayCommand]
    private async Task CopyConversion()
    {
        if (TestAmount is not { } a || TestFrom is null || TestTo is null) return;
        var c = Book.UnitConverter.Get(TestFrom, TestTo);
        if (c.IsMissing) return;
        await Dialogs.CopyText((a * c.Factor).ToString("0.####", CultureInfo.CurrentCulture));
    }

    // ------------------------------------------------------------------ backups

    public ObservableCollection<BackupFile> BackupFiles { get; } = [];
    [ObservableProperty] private BackupFile? _selectedBackup;

    private void LoadBackups()
    {
        BackupFiles.Clear();
        foreach (var f in Backups.List(BackupFolder).Take(60))
            BackupFiles.Add(new BackupFile(f.FullName, f.Name, f.LastWriteTime.ToString("f", CultureInfo.CurrentCulture), $"{f.Length / 1024:#,##0} KB"));
        OnPropertyChanged(nameof(BackupError));
    }

    [RelayCommand]
    private async Task ChangeBackupFolder()
    {
        var folder = await Dialogs.PickFolder("Where should backups go? (A OneDrive or Dropbox folder keeps a copy off this PC.)");
        if (folder is null) return;
        Book.SetSetting(SettingKeys.BackupFolder, folder);
        _seenVersion = Book.Version;
        BackupFolder = folder;
        await BackupNow();
    }

    [RelayCommand]
    private async Task BackupNow()
    {
        try
        {
            Backups.Snapshot(Book.DbPath, BackupFolder);
            BackupStatus.LastError = null;
        }
        catch (Exception e)
        {
            BackupStatus.LastError = e.Message;
            await Dialogs.Alert("Backup failed", e.Message);
        }
        LoadBackups();
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(BackupFolder);
        Launcher.Open(BackupFolder);
    }

    [RelayCommand]
    private void OpenDataFolder() => Launcher.Open(Path.GetDirectoryName(Book.DbPath)!);

    [RelayCommand]
    private async Task Restore()
    {
        if (SelectedBackup is not { } b) return;
        if (!await _main.Recipes.ConfirmLeave()) return;
        if (!await Dialogs.Confirm("Restore backup",
                $"Replace everything with the backup from {b.When}?\n\nA copy of the current data is saved first, so this can be undone.", "Restore")) return;
        Backups.Snapshot(Book.DbPath, BackupFolder);
        Backups.Restore(b.Path, Book.DbPath);
        Book.Reload();
        _main.Recipes.Reset();
        _seenVersion = -1;
        Activate();
        await Dialogs.Alert("Restored", $"The data is now as it was on {b.When}.");
    }

    // ------------------------------------------------------------------ units and conversions

    public ObservableCollection<UnitRow> UnitRows { get; } = [];
    public ObservableCollection<ConversionRow> ConversionRows { get; } = [];
    public List<string> UnitCodes { get; private set; } = [];
    [ObservableProperty] private UnitRow? _selectedUnit;
    [ObservableProperty] private ConversionRow? _selectedConversion;
    [ObservableProperty] private double? _testAmount = 1;
    [ObservableProperty] private string? _testFrom;
    [ObservableProperty] private string? _testTo;
    [ObservableProperty] private string _testResult = "";

    private void LoadUnits()
    {
        UnitRows.Clear();
        foreach (var u in Book.Units) UnitRows.Add(new UnitRow(u));
        ConversionRows.Clear();
        foreach (var c in Book.Conversions) ConversionRows.Add(new ConversionRow(c));
        UnitCodes = Book.Units.Select(u => u.Code).ToList();
        OnPropertyChanged(nameof(UnitCodes));
        TestFrom ??= UnitCodes.FirstOrDefault(u => u == "cup") ?? UnitCodes.FirstOrDefault();
        TestTo ??= UnitCodes.FirstOrDefault(u => u == "g") ?? UnitCodes.LastOrDefault();
        UpdateTest();
    }

    partial void OnTestAmountChanged(double? value) => UpdateTest();
    partial void OnTestFromChanged(string? value) => UpdateTest();
    partial void OnTestToChanged(string? value) => UpdateTest();

    private void UpdateTest()
    {
        if (TestAmount is not { } a || TestFrom is null || TestTo is null) { TestResult = ""; return; }
        var c = Book.UnitConverter.Get(TestFrom, TestTo);
        TestResult = c.IsMissing
            ? $"No way to convert {TestFrom} to {TestTo} — add a conversion."
            : $"{a:0.###} {TestFrom} = {a * c.Factor:#,##0.####} {TestTo}" + (c.Kind == ConversionKind.Chained ? "  (in two steps)" : "");
    }

    public void CommitUnit(UnitRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Code)) return;
        Book.SaveUnit(new MeasureUnit { Code = row.Code.Trim(), Description = row.Description, IsVolume = row.IsVolume });
        _seenVersion = Book.Version;
        UnitCodes = Book.Units.Select(u => u.Code).ToList();
        OnPropertyChanged(nameof(UnitCodes));
    }

    public void CommitConversion(ConversionRow row)
    {
        if (string.IsNullOrWhiteSpace(row.From) || string.IsNullOrWhiteSpace(row.To) || row.Factor <= 0) return;
        Book.SaveConversion(row.From.Trim(), row.To.Trim(), row.Factor, row.Key);
        row.Key = (row.From.Trim(), row.To.Trim());
        _seenVersion = Book.Version;
        UpdateTest();
    }

    [RelayCommand]
    private void AddUnit()
    {
        var row = new UnitRow(new MeasureUnit { Code = "", Description = "" });
        UnitRows.Add(row);
        SelectedUnit = row;
    }

    [RelayCommand]
    private async Task DeleteUnit()
    {
        if (SelectedUnit is not { } u) return;
        var used = Book.Recipes.Count(r => r.Unit.Equals(u.Code, StringComparison.OrdinalIgnoreCase) || r.Lines.Any(l => l.Unit.Equals(u.Code, StringComparison.OrdinalIgnoreCase)))
                   + Book.Ingredients.Count(i => i.Unit.Equals(u.Code, StringComparison.OrdinalIgnoreCase));
        if (used > 0)
        {
            await Dialogs.Alert("Unit in use", $"“{u.Code}” is used by {used} recipes or ingredients, so it stays.");
            return;
        }
        if (u.Code.Length > 0) Book.DeleteUnit(u.Code);
        _seenVersion = Book.Version;
        UnitRows.Remove(u);
    }

    [RelayCommand]
    private void AddConversion()
    {
        var row = new ConversionRow(new UnitConversion { From = TestFrom ?? "", To = TestTo ?? "", Factor = 1 }) { Key = ("", "") };
        ConversionRows.Add(row);
        SelectedConversion = row;
    }

    [RelayCommand]
    private void DeleteConversion()
    {
        if (SelectedConversion is not { } c) return;
        Book.DeleteConversion(c.Key.From, c.Key.To);
        _seenVersion = Book.Version;
        ConversionRows.Remove(c);
        UpdateTest();
    }

    // ------------------------------------------------------------------ substance (density) conversion

    public ObservableCollection<SubstanceRow> SubstanceRows { get; } = [];
    public IReadOnlyList<DensityConversionUnit> DensityUnits => Book.DensityUnits;
    [ObservableProperty] private string _substanceSearch = "";
    [ObservableProperty] private SubstanceRow? _substance;
    [ObservableProperty] private DensityConversionUnit? _densityFrom;
    [ObservableProperty] private DensityConversionUnit? _densityTo;
    [ObservableProperty] private double? _densityQty = 1;
    [ObservableProperty] private string _densityResult = "";

    private void LoadSubstances()
    {
        var keep = Substance?.Substance.Id;
        SubstanceRows.Clear();
        foreach (var s in Book.Substances.Where(s => s.Name.Contains(SubstanceSearch.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            SubstanceRows.Add(new SubstanceRow(s));
        Substance = SubstanceRows.FirstOrDefault(s => s.Substance.Id == keep);
        OnPropertyChanged(nameof(DensityUnits));
        DensityFrom ??= Book.DensityUnits.FirstOrDefault(u => u.Name == "cup [US]");
        DensityTo ??= Book.DensityUnits.FirstOrDefault(u => u.Name == "gram");
    }

    partial void OnSubstanceSearchChanged(string value) => LoadSubstances();
    partial void OnSubstanceChanged(SubstanceRow? value) => UpdateDensity();
    partial void OnDensityFromChanged(DensityConversionUnit? value) => UpdateDensity();
    partial void OnDensityToChanged(DensityConversionUnit? value) => UpdateDensity();
    partial void OnDensityQtyChanged(double? value) => UpdateDensity();

    private void UpdateDensity()
    {
        if (Substance is null) { DensityResult = "Pick a substance on the left."; return; }
        if (DensityFrom is null || DensityTo is null || DensityQty is not { } q) { DensityResult = ""; return; }
        var r = DensityConverter.Convert(q, DensityFrom, DensityTo, Substance.Density);
        DensityResult = $"{q:0.###} {DensityFrom.Name} of {Substance.Name} = {r:#,##0.##} {DensityTo.Name}";
    }

    public void CommitSubstance(SubstanceRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Name) || row.Density <= 0) return;
        row.Substance.Name = row.Name.Trim();
        row.Substance.Density = row.Density;
        Book.SaveSubstance(row.Substance);
        _seenVersion = Book.Version;
        UpdateDensity();
    }

    [RelayCommand]
    private void AddSubstance()
    {
        SubstanceSearch = "";
        var row = new SubstanceRow(new Substance { Name = "New substance", Density = 1 });
        CommitSubstance(row);
        SubstanceRows.Insert(0, row);
        Substance = row;
    }

    [RelayCommand]
    private void DeleteSubstance()
    {
        if (Substance is not { } s) return;
        Book.DeleteSubstance(s.Substance.Id);
        _seenVersion = Book.Version;
        SubstanceRows.Remove(s);
    }

    // ------------------------------------------------------------------ lookup lists

    public IReadOnlyList<Option<LookupList>> LookupLists { get; } =
    [
        new(LookupList.RecipeCategories, "Recipe categories"),
        new(LookupList.Tags, "Recipe tags"),
        new(LookupList.GroceryCategories, "Grocery categories (ingredients)"),
        new(LookupList.SupplierCategories, "Company categories"),
    ];

    [ObservableProperty] private Option<LookupList> _lookup;
    [ObservableProperty] private NamedRow? _selectedEntry;
    public ObservableCollection<NamedRow> Entries { get; } = [];

    partial void OnLookupChanged(Option<LookupList> value) => LoadLookup();

    private void LoadLookup()
    {
        Entries.Clear();
        IEnumerable<NamedRow> rows = Lookup.Value switch
        {
            LookupList.RecipeCategories => Book.Categories.Select(c => new NamedRow(c.Id, c.Name, Book.Recipes.Count(r => r.CategoryId == c.Id))),
            LookupList.Tags => Book.Tags.Select(t => new NamedRow(t.Id, t.Name, Book.Recipes.Count(r => r.Tags.Any(x => x.Id == t.Id)))),
            LookupList.GroceryCategories => Book.GroceryCategories.Select(c => new NamedRow(c.Id, c.Name, Book.Ingredients.Count(i => i.GroceryCategoryId == c.Id))),
            _ => Book.SupplierCategories.Select(c => new NamedRow(c.Id, c.Name, Book.Companies.Count(s => s.Categories.Any(x => x.Id == c.Id)))),
        };
        foreach (var r in rows) Entries.Add(r);
    }

    public void CommitEntry(NamedRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Name)) return;
        var name = row.Name.Trim();
        switch (Lookup.Value)
        {
            case LookupList.RecipeCategories:
                var c = Book.Categories.FirstOrDefault(x => x.Id == row.Id) ?? new RecipeCategory();
                c.Name = name;
                row.Id = Book.SaveCategory(c).Id;
                break;
            case LookupList.Tags:
                var t = Book.Tags.FirstOrDefault(x => x.Id == row.Id) ?? new Tag();
                t.Name = name;
                row.Id = Book.SaveTag(t).Id;
                break;
            case LookupList.GroceryCategories:
                var g = Book.GroceryCategories.FirstOrDefault(x => x.Id == row.Id) ?? new GroceryCategory();
                g.Name = name;
                row.Id = Book.SaveGroceryCategory(g).Id;
                break;
            default:
                var sc = Book.SupplierCategories.FirstOrDefault(x => x.Id == row.Id) ?? new SupplierCategory();
                sc.Name = name;
                row.Id = Book.SaveSupplierCategory(sc).Id;
                break;
        }
        _seenVersion = Book.Version;
    }

    [RelayCommand]
    private void AddEntry()
    {
        var row = new NamedRow(0, "New entry", 0);
        CommitEntry(row);
        Entries.Insert(0, row);
        SelectedEntry = row;
    }

    [RelayCommand]
    private async Task DeleteEntry()
    {
        if (SelectedEntry is not { } e) return;
        var what = Lookup.Value switch
        {
            LookupList.RecipeCategories => "recipes", LookupList.Tags => "recipes", LookupList.GroceryCategories => "ingredients", _ => "suppliers",
        };
        if (e.Uses > 0 && !await Dialogs.Confirm("Delete entry", $"“{e.Name}” is used by {e.Uses} {what}. They will be left without it. Delete anyway?", "Delete"))
            return;
        switch (Lookup.Value)
        {
            case LookupList.RecipeCategories: Book.DeleteCategory(e.Id); break;
            case LookupList.Tags: Book.DeleteTag(e.Id); break;
            case LookupList.GroceryCategories: Book.DeleteGroceryCategory(e.Id); break;
            default: Book.DeleteSupplierCategory(e.Id); break;
        }
        _seenVersion = Book.Version;
        Entries.Remove(e);
    }
}
