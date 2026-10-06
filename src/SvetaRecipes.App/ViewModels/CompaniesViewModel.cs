using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.App.Services;
using SvetaRecipes.Core.Costing;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.ViewModels;

public partial class CategoryChoice(SupplierCategory category, bool selected, Action changed) : ObservableObject
{
    public SupplierCategory Category { get; } = category;
    public string Name => Category.Name;
    [ObservableProperty] private bool _isSelected = selected;
    partial void OnIsSelectedChanged(bool value) => changed();
}

public sealed record SupplierPriceLine(int IngredientId, string Ingredient, string Brand, string Pack, string Price, string PerKg);

public sealed record CompanyInvoiceLine(int Id, string Number, string Date, string Total, string Status);

public sealed record CompanyListItem(Company Company)
{
    public string Name => Company.Name;
    public string Kind => (Company.IsCustomer, Company.IsSupplier) switch
    {
        (true, true) => "customer · supplier",
        (true, false) => "customer",
        (false, true) => "supplier",
        _ => "",
    };
    public string Detail => string.Join("  ·  ", new[] { Kind, Company.Contact }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Customers and suppliers (legacy frmCompanies / frmNewCompany): one company can be both.</summary>
public partial class CompaniesViewModel : ViewModelBase, ISection
{
    private static readonly HashSet<string> Fields =
    [
        nameof(Name), nameof(IsCustomer), nameof(IsSupplier), nameof(DateEntered), nameof(Contact), nameof(Phone), nameof(TollFree),
        nameof(Fax), nameof(Email), nameof(Website), nameof(Street), nameof(City), nameof(Province), nameof(PostalCode), nameof(Country), nameof(Notes),
    ];

    private readonly MainViewModel _main;
    private int _seenVersion = -1;
    private bool _loading;
    private bool _reverting;
    private int _id;

    public CompaniesViewModel(MainViewModel main)
    {
        _main = main;
        _show = ShowOptions[0];
    }

    private RecipeBook Book => _main.Book;

    public IReadOnlyList<Option<int>> ShowOptions { get; } = [new(0, "Customers and suppliers"), new(1, "Customers"), new(2, "Suppliers")];

    public ObservableCollection<CompanyListItem> Items { get; } = [];
    public ObservableCollection<CategoryChoice> Categories { get; } = [];
    public ObservableCollection<SupplierPriceLine> Prices { get; } = [];
    public ObservableCollection<CompanyInvoiceLine> Invoices { get; } = [];

    [ObservableProperty] private Option<int> _show;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private CompanyListItem? _selected;
    [ObservableProperty] private bool _hasCompany;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _invoiceSummary = "";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isCustomer;
    [ObservableProperty] private bool _isSupplier;
    [ObservableProperty] private DateTime? _dateEntered;
    [ObservableProperty] private string? _contact;
    [ObservableProperty] private string? _phone;
    [ObservableProperty] private string? _tollFree;
    [ObservableProperty] private string? _fax;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _website;
    [ObservableProperty] private string? _street;
    [ObservableProperty] private string? _city;
    [ObservableProperty] private string? _province;
    [ObservableProperty] private string? _postalCode;
    [ObservableProperty] private string? _country;
    [ObservableProperty] private string? _notes;

    public IReadOnlyList<string> Provinces => Book.Companies.Select(c => c.Province).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim())
        .Concat(["AB", "BC", "MB", "NB", "NL", "NS", "NT", "NU", "ON", "PE", "QC", "SK", "YT"]).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    public void Activate()
    {
        if (_seenVersion == Book.Version) return;
        _seenVersion = Book.Version;
        RefreshList(Selected?.Company.Id ?? (_id == 0 ? null : _id));
        if (!IsDirty) Load(Book.Companies.FirstOrDefault(c => c.Id == _id) ?? Selected?.Company);
    }

    partial void OnShowChanged(Option<int> value) => RefreshList(_id);
    partial void OnSearchChanged(string value) => RefreshList(_id);

    private void RefreshList(int? select)
    {
        var term = Search.Trim();
        _reverting = true;
        Items.Clear();
        foreach (var c in Book.Companies.Where(c => Show.Value switch { 1 => c.IsCustomer, 2 => c.IsSupplier, _ => true })
                     .Where(c => term.Length == 0 || c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                                                  || (c.Contact?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            Items.Add(new CompanyListItem(c));
        Selected = Items.FirstOrDefault(i => i.Company.Id == select) ?? (select is null ? Items.FirstOrDefault() : null);
        _reverting = false;
        if (_id == 0 && !IsDirty) Load(Selected?.Company);
    }

    public void Select(int companyId)
    {
        _seenVersion = -1;
        Search = "";
        Show = ShowOptions[0];
        Activate();
        Selected = Items.FirstOrDefault(i => i.Company.Id == companyId);
    }

    partial void OnSelectedChanged(CompanyListItem? oldValue, CompanyListItem? newValue)
    {
        if (_reverting || newValue is null || newValue.Company.Id == _id) return;
        _ = Switch(newValue.Company, oldValue);
    }

    private async Task Switch(Company to, CompanyListItem? from)
    {
        if (!await ConfirmLeave())
        {
            _reverting = true;
            Selected = from;
            _reverting = false;
            return;
        }
        Load(to);
    }

    private void Load(Company? c)
    {
        _loading = true;
        _id = c?.Id ?? 0;
        HasCompany = c is not null;
        Name = c?.Name ?? "";
        IsCustomer = c?.IsCustomer ?? false;
        IsSupplier = c?.IsSupplier ?? false;
        DateEntered = c?.DateEntered;
        Contact = c?.Contact;
        Phone = c?.Phone;
        TollFree = c?.TollFree;
        Fax = c?.Fax;
        Email = c?.Email;
        Website = c?.Website;
        Street = c?.Street;
        City = c?.City;
        Province = c?.Province;
        PostalCode = c?.PostalCode;
        Country = c?.Country;
        Notes = c?.Notes;
        Categories.Clear();
        var selected = c?.Categories.Select(x => x.Id).ToHashSet() ?? [];
        foreach (var cat in Book.SupplierCategories) Categories.Add(new CategoryChoice(cat, selected.Contains(cat.Id), () => { if (!_loading) IsDirty = true; }));
        Prices.Clear();
        var units = Book.UnitConverter;
        foreach (var p in c?.Prices ?? [])
        {
            if (Book.FindIngredient(p.IngredientId) is not { } ing) continue;
            Prices.Add(new SupplierPriceLine(ing.Id, ing.Name, p.Brand ?? "", $"{p.PackQty:0.###} {p.Unit}", p.PackPrice.ToString("C2"),
                DensityConverter.PricePerKg(units, p.Unit, p.PackQty, p.PackPrice)?.ToString("C2") ?? ""));
        }
        Invoices.Clear();
        var invoices = Book.Invoices.Where(i => c is not null && i.CompanyId == c.Id).OrderByDescending(i => i.Id).ToList();
        foreach (var i in invoices)
            Invoices.Add(new CompanyInvoiceLine(i.Id, i.Id.ToString("0000"), i.Date.ToString("d", CultureInfo.CurrentCulture), i.Total.ToString("C2"),
                i.IsPaid ? "paid" : i.Balance > 0 ? $"due {i.Balance:C2}" : ""));
        InvoiceSummary = invoices.Count == 0 ? "No invoices." :
            $"{invoices.Count} invoices, {invoices.Sum(i => i.Total):C2} billed, {invoices.Sum(i => i.Balance):C2} outstanding";
        _loading = false;
        IsDirty = false;
        OnPropertyChanged(nameof(Provinces));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is { } n && Fields.Contains(n)) IsDirty = true;
    }

    public async Task<bool> ConfirmLeave()
    {
        if (!IsDirty) return true;
        switch (await _main.Dialogs.AskSave(string.IsNullOrWhiteSpace(Name) ? "the new company" : Name))
        {
            case SaveChoice.Save: return await SaveCurrent();
            case SaveChoice.Discard: IsDirty = false; return true;
            default: return false;
        }
    }

    private async Task<bool> SaveCurrent()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            await _main.Dialogs.Alert("Company name", "You must enter at least the company name.");
            return false;
        }
        static string? B(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var saved = Book.SaveCompany(new Company
        {
            Id = _id, Name = Name.Trim(), IsCustomer = IsCustomer, IsSupplier = IsSupplier, DateEntered = DateEntered,
            Contact = B(Contact), Phone = B(Phone), TollFree = B(TollFree), Fax = B(Fax), Email = B(Email), Website = B(Website),
            Street = B(Street), City = B(City), Province = B(Province), PostalCode = B(PostalCode), Country = B(Country), Notes = B(Notes),
            Categories = Categories.Where(c => c.IsSelected).Select(c => c.Category).ToList(),
        });
        IsDirty = false;
        _id = saved.Id;
        _seenVersion = Book.Version;
        RefreshList(saved.Id);
        Load(saved);
        return true;
    }

    [RelayCommand]
    private async Task Save() => await SaveCurrent();

    [RelayCommand]
    private void Revert() => Load(Book.Companies.FirstOrDefault(c => c.Id == _id));

    [RelayCommand]
    private async Task New()
    {
        if (!await ConfirmLeave()) return;
        _reverting = true;
        Selected = null;
        _reverting = false;
        Load(new Company { IsCustomer = Show.Value != 2, IsSupplier = Show.Value == 2, DateEntered = DateTime.Today });
        HasCompany = true;
        IsDirty = true;
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (_id == 0) { IsDirty = false; Load(Selected?.Company); return; }
        if (Invoices.Count > 0)
        {
            await _main.Dialogs.Alert("Company has invoices", $"You can't delete {Name}, because there are invoices linked to it.");
            return;
        }
        if (!await _main.Dialogs.Confirm("Delete company", $"You are about to delete {Name}. Are you sure?", "Delete")) return;
        Book.DeleteCompany(_id);
        IsDirty = false;
        _id = 0;
        _seenVersion = -1;
        Activate();
    }

    [RelayCommand]
    private async Task NewInvoice()
    {
        if (_id == 0 || !await ConfirmLeave()) return;
        if (!IsCustomer)
        {
            IsCustomer = true;
            await SaveCurrent();
        }
        await _main.Invoices.New(_id);
        Load(Book.Companies.FirstOrDefault(c => c.Id == _id));
    }

    [RelayCommand]
    private void OpenInvoice(CompanyInvoiceLine? line)
    {
        if (line is null) return;
        _main.SelectedTab = 3;
        _main.Invoices.Selected = _main.Invoices.Rows.FirstOrDefault(r => r.Id == line.Id);
        _main.Invoices.EditSelectedCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenIngredient(SupplierPriceLine? line)
    {
        if (line is not null) _main.GoToIngredient(line.IngredientId);
    }

    [RelayCommand]
    private void WriteEmail()
    {
        if (!string.IsNullOrWhiteSpace(Email)) Launcher.Open("mailto:" + Email.Trim());
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        if (string.IsNullOrWhiteSpace(Website)) return;
        var url = Website.Trim();
        Launcher.Open(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url);
    }
}
