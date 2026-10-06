using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SvetaRecipes.App.Services;
using SvetaRecipes.Core.Model;
using SvetaRecipes.Core.Services;
using SvetaRecipes.Reports;

namespace SvetaRecipes.App.ViewModels;

/// <summary>A row of the invoice list. Received and Paid are edited in place, as in the legacy list.</summary>
public partial class InvoiceRow : ObservableObject
{
    private readonly InvoicesViewModel _owner;
    private bool _loading = true;

    public InvoiceRow(InvoicesViewModel owner, Invoice invoice, string customer)
    {
        _owner = owner;
        Invoice = invoice;
        Customer = customer;
        _received = Math.Round(invoice.AmountReceived, 2);
        _isPaid = invoice.IsPaid;
        _loading = false;
    }

    public Invoice Invoice { get; }
    public int Id => Invoice.Id;
    public string Number => Invoice.Id.ToString("0000");
    public DateTime Date => Invoice.Date;
    public string DateText => Invoice.Date.ToString("d", CultureInfo.CurrentCulture);
    public string Customer { get; }
    public decimal Total => Invoice.Total;
    public string TotalText => Total.ToString("C2", CultureInfo.CurrentCulture);
    public string Items => string.Join(", ", Invoice.Items.OrderBy(i => i.Position).Select(i => i.Name));

    [ObservableProperty] private decimal _received;
    [ObservableProperty] private bool _isPaid;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is nameof(Received) or nameof(IsPaid)) _owner.SavePayment(this);
    }
}

public partial class InvoicesViewModel : ViewModelBase, ISection
{
    private readonly MainViewModel _main;
    private int _seenVersion = -1;

    public InvoicesViewModel(MainViewModel main)
    {
        _main = main;
        _show = ShowOptions[0];
    }

    private RecipeBook Book => _main.Book;

    public ObservableCollection<InvoiceRow> Rows { get; } = [];
    [ObservableProperty] private InvoiceRow? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Option<int> _show;
    [ObservableProperty] private string _billed = "";
    [ObservableProperty] private string _received = "";
    [ObservableProperty] private string _balance = "";
    [ObservableProperty] private string _summary = "";

    public IReadOnlyList<Option<int>> ShowOptions { get; } = [new(0, "All invoices"), new(1, "Not paid"), new(2, "Paid")];

    public void Activate()
    {
        if (_seenVersion == Book.Version) return;
        _seenVersion = Book.Version;
        Reload();
    }

    partial void OnSearchChanged(string value) => Reload();
    partial void OnShowChanged(Option<int> value) => Reload();

    private void Reload()
    {
        var keep = Selected?.Id;
        var names = Book.Companies.ToDictionary(c => c.Id, c => c.Name);
        var term = Search.Trim();
        var rows = Book.Invoices
            .Where(i => Show.Value switch { 1 => !i.IsPaid, 2 => i.IsPaid, _ => true })
            .Where(i => term.Length == 0
                        || i.Items.Any(x => x.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
                        || names.GetValueOrDefault(i.CompanyId, "").Contains(term, StringComparison.CurrentCultureIgnoreCase)
                        || i.Id.ToString().Contains(term))
            .OrderByDescending(i => i.Id)
            .Select(i => new InvoiceRow(this, i, names.GetValueOrDefault(i.CompanyId, "?")))
            .ToList();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Selected = Rows.FirstOrDefault(r => r.Id == keep) ?? Rows.FirstOrDefault();
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var billed = Rows.Sum(r => r.Total);
        var received = Rows.Sum(r => r.Received);
        Billed = billed.ToString("C2", CultureInfo.CurrentCulture);
        Received = received.ToString("C2", CultureInfo.CurrentCulture);
        Balance = (billed - received).ToString("C2", CultureInfo.CurrentCulture);
        Summary = Rows.Count == Book.Invoices.Count ? $"{Rows.Count} invoices" : $"{Rows.Count} of {Book.Invoices.Count} invoices";
    }

    public void SavePayment(InvoiceRow row)
    {
        Book.SetInvoicePayment(row.Id, row.Received, row.IsPaid);
        _seenVersion = Book.Version;
        UpdateTotals();
    }

    /// <summary>Opens the editor for a new invoice, optionally for a given customer.</summary>
    [RelayCommand]
    public async Task New(int? customerId = null)
    {
        var invoice = new Invoice { Date = DateTime.Today, CompanyId = customerId ?? 0 };
        await Edit(invoice, isNew: true);
    }

    [RelayCommand]
    private async Task EditSelected()
    {
        if (Selected is { } row) await Edit(row.Invoice.Clone(), isNew: false);
    }

    private async Task Edit(Invoice invoice, bool isNew)
    {
        var vm = new InvoiceEditorViewModel(Book, _main.Dialogs, invoice, isNew);
        var title = isNew ? "New invoice" : $"Invoice {invoice.Id:0000}";
        if (await _main.Dialogs.Show(vm, title, 900, 760) is int id)
        {
            _seenVersion = -1;
            Activate();
            Selected = Rows.FirstOrDefault(r => r.Id == id);
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { } row) return;
        if (!await _main.Dialogs.Confirm("Delete invoice", $"You are about to delete invoice #{row.Number} for {row.Customer}. Are you sure?", "Delete")) return;
        Book.DeleteInvoice(row.Id);
        _seenVersion = -1;
        Activate();
    }

    [RelayCommand]
    private void Print()
    {
        if (Selected is not { } row) return;
        InvoicePrinting.Print(Book, row.Invoice);
    }
}

public static class InvoicePrinting
{
    public static BusinessInfo Business(RecipeBook book) => new(
        book.GetSetting(SettingKeys.BusinessName), book.GetSetting(SettingKeys.BusinessAddress1), book.GetSetting(SettingKeys.BusinessAddress2),
        book.GetSetting(SettingKeys.BusinessCity), book.GetSetting(SettingKeys.BusinessProvince), book.GetSetting(SettingKeys.BusinessPostal),
        book.GetSetting(SettingKeys.BusinessPhone), book.GetSetting(SettingKeys.BusinessEmail));

    public static void Print(RecipeBook book, Invoice invoice)
    {
        var customer = book.Companies.FirstOrDefault(c => c.Id == invoice.CompanyId) ?? new Company { Name = "" };
        var path = Launcher.ExportPath($"Invoice {invoice.Id:0000} {customer.Name}");
        Pdf.Invoice(path, invoice, customer, Business(book));
        Launcher.Open(path);
    }
}

public partial class InvoiceItemRow(InvoiceItem item, Action changed) : ObservableObject
{
    [ObservableProperty] private string _name = item.Name;
    [ObservableProperty] private decimal _price = Math.Round(item.Price, 2);
    [ObservableProperty] private double _quantity = item.Quantity;

    public string AmountText => (Price * (decimal)Quantity).ToString("C2", CultureInfo.CurrentCulture);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Price) or nameof(Quantity)) OnPropertyChanged(nameof(AmountText));
        if (e.PropertyName is nameof(Name) or nameof(Price) or nameof(Quantity)) changed();
    }

    public InvoiceItem ToItem(int position) => new() { Position = position, Name = Name, Price = Price, Quantity = Quantity };
}

/// <summary>A recipe offered on an invoice line, priced at its price per serving (as the legacy item list was).</summary>
public sealed record InvoicePick(string Name, decimal? Price)
{
    public string Detail => Price is { } p ? p.ToString("C2", CultureInfo.CurrentCulture) : "";
    public override string ToString() => Name;
}

public partial class InvoiceEditorViewModel : DialogViewModel
{
    private readonly RecipeBook _book;
    private readonly IDialogs _dialogs;
    private readonly bool _isNew;
    private bool _loading = true;

    public InvoiceEditorViewModel(RecipeBook book, IDialogs dialogs, Invoice invoice, bool isNew)
    {
        _book = book;
        _dialogs = dialogs;
        _isNew = isNew;
        Id = invoice.Id;
        Number = isNew ? $"{book.NextInvoiceNumber:0000} (new)" : invoice.Id.ToString("0000");
        LoadCustomers(invoice.CompanyId);
        _date = invoice.Date;
        _discount = Math.Round(invoice.Discount, 2);
        _tax1 = Math.Round(invoice.Tax1, 2);
        _tax2 = Math.Round(invoice.Tax2, 2);
        _amountReceived = Math.Round(invoice.AmountReceived, 2);
        _isPaid = invoice.IsPaid;
        _notes = invoice.Notes;
        foreach (var item in invoice.Items.OrderBy(i => i.Position)) Items.Add(new InvoiceItemRow(item, Recalculate));
        Picks = book.Recipes.Select(r => new InvoicePick(r.Name, r.PricePerServing ?? r.SellPrice)).ToList();
        _loading = false;
        Recalculate();
    }

    public int Id { get; }
    public string Number { get; }
    public ObservableCollection<Option<int>> Customers { get; } = [];
    public ObservableCollection<InvoiceItemRow> Items { get; } = [];
    public IReadOnlyList<InvoicePick> Picks { get; }

    [ObservableProperty] private Option<int>? _customer;
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private decimal _discount;
    [ObservableProperty] private decimal _tax1;
    [ObservableProperty] private decimal _tax2;
    [ObservableProperty] private decimal _amountReceived;
    [ObservableProperty] private bool _isPaid;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private InvoiceItemRow? _selectedItem;

    [ObservableProperty] private string _netText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _balanceText = "";

    // add bar
    [ObservableProperty] private InvoicePick? _newPick;
    [ObservableProperty] private string? _newName;
    [ObservableProperty] private decimal? _newPrice;
    [ObservableProperty] private double? _newQuantity = 1;

    partial void OnNewPickChanged(InvoicePick? value)
    {
        if (value?.Price is { } p) NewPrice = Math.Round(p, 2);
    }

    partial void OnDiscountChanged(decimal value) => Recalculate();
    partial void OnTax1Changed(decimal value) => Recalculate();
    partial void OnTax2Changed(decimal value) => Recalculate();
    partial void OnAmountReceivedChanged(decimal value) => Recalculate();

    private void LoadCustomers(int selectId)
    {
        Customers.Clear();
        foreach (var c in _book.Companies.Where(c => c.IsCustomer || c.Id == selectId)) Customers.Add(new Option<int>(c.Id, c.Name));
        Customer = Customers.FirstOrDefault(c => c.Value == selectId);
    }

    private Invoice ToInvoice() => new()
    {
        Id = _isNew ? 0 : Id, Date = Date ?? DateTime.Today, CompanyId = Customer?.Value ?? 0, Discount = Discount, Tax1 = Tax1, Tax2 = Tax2,
        AmountReceived = AmountReceived, IsPaid = IsPaid, Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
        Items = Items.Select((i, n) => i.ToItem(n)).ToList(),
    };

    private void Recalculate()
    {
        if (_loading) return;
        var inv = ToInvoice();
        NetText = inv.NetAmount.ToString("C2", CultureInfo.CurrentCulture);
        TotalText = inv.Total.ToString("C2", CultureInfo.CurrentCulture);
        BalanceText = inv.Balance.ToString("C2", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private void AddItem()
    {
        var name = NewPick?.Name ?? NewName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        var row = new InvoiceItemRow(new InvoiceItem { Name = name, Price = NewPrice ?? 0, Quantity = NewQuantity ?? 1 }, Recalculate);
        Items.Add(row);
        SelectedItem = row;
        NewPick = null;
        NewName = null;
        NewPrice = null;
        NewQuantity = 1;
        Recalculate();
    }

    [RelayCommand]
    private void RemoveItem()
    {
        if (SelectedItem is null) return;
        Items.Remove(SelectedItem);
        Recalculate();
    }

    [RelayCommand]
    private async Task NewCustomer()
    {
        if (await _dialogs.Show(new QuickCompanyViewModel(_book, null, customer: true), "New customer", 560, 560) is int id)
            LoadCustomers(id);
    }

    [RelayCommand]
    private async Task EditCustomer()
    {
        if (Customer is null) return;
        var company = _book.Companies.First(c => c.Id == Customer.Value);
        if (await _dialogs.Show(new QuickCompanyViewModel(_book, company, customer: true), "Edit customer", 560, 560) is int id)
            LoadCustomers(id);
    }

    private async Task<Invoice?> SaveCore()
    {
        if (Customer is null)
        {
            await _dialogs.Alert("Customer", "Please choose the customer (or add a new one).");
            return null;
        }
        if (Items.Count == 0)
        {
            await _dialogs.Alert("Items", "Add at least one item.");
            return null;
        }
        return _book.SaveInvoice(ToInvoice());
    }

    [RelayCommand]
    private async Task Save()
    {
        if (await SaveCore() is { } saved) Close(saved.Id);
    }

    [RelayCommand]
    private async Task SaveAndPrint()
    {
        if (await SaveCore() is not { } saved) return;
        InvoicePrinting.Print(_book, saved);
        Close(saved.Id);
    }

    [RelayCommand]
    private void Cancel() => Close();
}

/// <summary>Add or edit a customer/supplier from inside another dialog (legacy frmNewCompany).</summary>
public partial class QuickCompanyViewModel : DialogViewModel
{
    private readonly RecipeBook _book;
    private readonly Company _company;

    public QuickCompanyViewModel(RecipeBook book, Company? company, bool customer)
    {
        _book = book;
        _company = company?.Clone() ?? new Company { IsCustomer = customer, IsSupplier = !customer, DateEntered = DateTime.Today };
        _name = _company.Name;
        _contact = _company.Contact;
        _phone = _company.Phone;
        _email = _company.Email;
        _street = _company.Street;
        _city = _company.City;
        _province = _company.Province;
        _postalCode = _company.PostalCode;
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _contact;
    [ObservableProperty] private string? _phone;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _street;
    [ObservableProperty] private string? _city;
    [ObservableProperty] private string? _province;
    [ObservableProperty] private string? _postalCode;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "You must enter at least the company name."; return; }
        static string? B(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        _company.Name = Name.Trim();
        _company.Contact = B(Contact);
        _company.Phone = B(Phone);
        _company.Email = B(Email);
        _company.Street = B(Street);
        _company.City = B(City);
        _company.Province = B(Province);
        _company.PostalCode = B(PostalCode);
        Close(_book.SaveCompany(_company).Id);
    }

    [RelayCommand]
    private void Cancel() => Close();
}
