namespace SvetaRecipes.Core.Model;

/// <summary>Working copies for editors, so an unsaved edit never leaks into the shared in-memory data.</summary>
public static class Cloning
{
    public static Recipe Clone(this Recipe r) => new()
    {
        Id = r.Id, Name = r.Name, CategoryId = r.CategoryId, Unit = r.Unit, LabourHours = r.LabourHours,
        Servings = r.Servings, WeightPerServing = r.WeightPerServing, CostMethod = r.CostMethod, SellPrice = r.SellPrice,
        PricePerServing = r.PricePerServing, Notes = r.Notes, Description = r.Description, Instructions = r.Instructions,
        LabelTitle = r.LabelTitle, LabelText = r.LabelText, ChargeCode = r.ChargeCode, LabelPrinted = r.LabelPrinted,
        Shape = r.Shape, Width = r.Width, Length = r.Length, Height = r.Height, CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt,
        Lines = r.Lines.OrderBy(l => l.Position).Select(l => l.Clone()).ToList(),
        Tags = [.. r.Tags],
        Layers = r.Layers.OrderBy(l => l.Position).Select(l => new RecipeLayer
            { Id = l.Id, RecipeId = l.RecipeId, Position = l.Position, Name = l.Name, BackColor = l.BackColor, ForeColor = l.ForeColor }).ToList(),
        Files = r.Files.Select(f => new RecipeFile
            { Id = f.Id, RecipeId = f.RecipeId, Kind = f.Kind, FileName = f.FileName, Caption = f.Caption, Data = f.Data }).ToList(),
    };

    public static RecipeLine Clone(this RecipeLine l) => new()
    {
        Id = l.Id, RecipeId = l.RecipeId, Position = l.Position, Kind = l.Kind, IngredientId = l.IngredientId,
        SubRecipeId = l.SubRecipeId, Amount = l.Amount, Unit = l.Unit, Notes = l.Notes,
    };

    public static Ingredient Clone(this Ingredient i) => new()
    {
        Id = i.Id, Name = i.Name, Unit = i.Unit, PackQty = i.PackQty, PackPrice = i.PackPrice, PerPiece = i.PerPiece,
        GroceryCategoryId = i.GroceryCategoryId, Comments = i.Comments, SupplierPrices = i.SupplierPrices,
    };

    public static Company Clone(this Company s) => new()
    {
        Id = s.Id, Name = s.Name, IsCustomer = s.IsCustomer, IsSupplier = s.IsSupplier, DateEntered = s.DateEntered, Contact = s.Contact, Street = s.Street, City = s.City, Province = s.Province,
        PostalCode = s.PostalCode, Country = s.Country, Phone = s.Phone, TollFree = s.TollFree, Fax = s.Fax,
        Website = s.Website, Email = s.Email, Notes = s.Notes, Categories = [.. s.Categories], Prices = s.Prices,
    };

    public static Invoice Clone(this Invoice i) => new()
    {
        Id = i.Id, Date = i.Date, CompanyId = i.CompanyId, Discount = i.Discount, Tax1 = i.Tax1, Tax2 = i.Tax2,
        AmountReceived = i.AmountReceived, IsPaid = i.IsPaid, Notes = i.Notes,
        Items = i.Items.OrderBy(x => x.Position).Select(x => new InvoiceItem
            { Id = x.Id, InvoiceId = x.InvoiceId, Position = x.Position, Name = x.Name, Price = x.Price, Quantity = x.Quantity }).ToList(),
    };

    public static ShoppingList Clone(this ShoppingList l) => new()
    {
        Id = l.Id, Date = l.Date, Name = l.Name,
        Items = l.Items.Select(i => new ShoppingItem
        {
            Id = i.Id, ShoppingListId = i.ShoppingListId, IngredientId = i.IngredientId, Name = i.Name, Quantity = i.Quantity,
            Unit = i.Unit, QuantityText = i.QuantityText, Store = i.Store, Notes = i.Notes, Done = i.Done,
        }).ToList(),
    };
}
