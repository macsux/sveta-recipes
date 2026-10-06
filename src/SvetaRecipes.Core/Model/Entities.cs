namespace SvetaRecipes.Core.Model;

// Ids are carried over from the legacy Access database so imported rows stay traceable to the original.

public enum LineKind
{
    Separator = 0,
    Ingredient = 1,
    SubRecipe = 2,
}

/// <summary>How cost per serving is derived (legacy <c>PriceCalcMethod</c>).</summary>
public enum ServingCostMethod
{
    /// <summary>Total cost ÷ number of servings.</summary>
    ByServingCount = 1,
    /// <summary>Total cost × weight per serving ÷ total weight.</summary>
    ByServingWeight = 2,
}

public enum PanShape
{
    None = 0,
    Round = 1,
    Rectangle = 2,
}

public enum RecipeFileKind
{
    Image = 0,
    Attachment = 1,
}

public class Recipe
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? CategoryId { get; set; }
    /// <summary>Unit the recipe's yield is measured in; every line is converted into it.</summary>
    public string Unit { get; set; } = "g";
    public double LabourHours { get; set; }
    public int? Servings { get; set; }
    public double? WeightPerServing { get; set; }
    public ServingCostMethod CostMethod { get; set; } = ServingCostMethod.ByServingCount;
    public decimal? SellPrice { get; set; }
    public decimal? PricePerServing { get; set; }
    /// <summary>The short note under the header ("270F 30min").</summary>
    public string? Notes { get; set; }
    public string? Description { get; set; }
    public string? Instructions { get; set; }
    public string? LabelTitle { get; set; }
    public string? LabelText { get; set; }
    public string? ChargeCode { get; set; }
    public bool LabelPrinted { get; set; }
    public PanShape Shape { get; set; } = PanShape.Round;
    public double? Width { get; set; }
    /// <summary>Diameter for a round pan, length for a rectangle.</summary>
    public double? Length { get; set; }
    public double? Height { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public List<RecipeLine> Lines { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<RecipeLayer> Layers { get; set; } = [];
    public List<RecipeFile> Files { get; set; } = [];
}

public class RecipeLine
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public int Position { get; set; }
    public LineKind Kind { get; set; }
    public int? IngredientId { get; set; }
    public int? SubRecipeId { get; set; }
    public double Amount { get; set; }
    public string Unit { get; set; } = "g";
    public string? Notes { get; set; }
}

public class Ingredient
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Unit the pack is priced in.</summary>
    public string Unit { get; set; } = "g";
    /// <summary>Pack size in <see cref="Unit"/>; for a per-piece ingredient, the weight of one piece.</summary>
    public double PackQty { get; set; }
    /// <summary>Pack price; for a per-piece ingredient, the price of one piece.</summary>
    public decimal PackPrice { get; set; }
    public bool PerPiece { get; set; }
    public int? GroceryCategoryId { get; set; }
    public string? Comments { get; set; }

    public List<SupplierPrice> SupplierPrices { get; set; } = [];
}

public class RecipeCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Recipe> Recipes { get; set; } = [];
}

public class GroceryCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class SupplierCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Company> Companies { get; set; } = [];
}

/// <summary>A customer, a supplier, or both (legacy tblClient).</summary>
public class Company
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }
    public DateTime? DateEntered { get; set; }
    public string? Contact { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Phone { get; set; }
    public string? TollFree { get; set; }
    public string? Fax { get; set; }
    public string? Website { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }

    public List<SupplierCategory> Categories { get; set; } = [];
    public List<SupplierPrice> Prices { get; set; } = [];
}

public class Invoice
{
    /// <summary>The invoice number (legacy InvoiceNo, kept on import).</summary>
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public int CompanyId { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax1 { get; set; }
    public decimal Tax2 { get; set; }
    public decimal AmountReceived { get; set; }
    public bool IsPaid { get; set; }
    public string? Notes { get; set; }
    public List<InvoiceItem> Items { get; set; } = [];

    public decimal NetAmount => Items.Sum(i => i.Amount);
    /// <summary>Net − discount + taxes. (Legacy computed net − discount; its tax fields were never used.)</summary>
    public decimal Total => NetAmount - Discount + Tax1 + Tax2;
    public decimal Balance => Total - AmountReceived;
}

public class InvoiceItem
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public double Quantity { get; set; }
    public decimal Amount => Price * (decimal)Quantity;
}

public class SupplierPrice
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public int IngredientId { get; set; }
    public string? Brand { get; set; }
    public string Unit { get; set; } = "g";
    public double PackQty { get; set; }
    public decimal PackPrice { get; set; }
    public string? Comments { get; set; }
}

public class MeasureUnit
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsVolume { get; set; }
}

public class UnitConversion
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>1 <see cref="From"/> = Factor × <see cref="To"/>.</summary>
    public double Factor { get; set; }
}

/// <summary>A substance with a density, for volume ↔ weight conversion.</summary>
public class Substance
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>g/ml.</summary>
    public double Density { get; set; }
}

/// <summary>A unit for the substance converter. Positive = grams per unit (weight), negative = −ml per unit (volume).</summary>
public class DensityConversionUnit
{
    public string Name { get; set; } = "";
    public double Coefficient { get; set; }
}

public class ShoppingList
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public string? Name { get; set; }
    public List<ShoppingItem> Items { get; set; } = [];
}

public class ShoppingItem
{
    public int Id { get; set; }
    public int ShoppingListId { get; set; }
    public int? IngredientId { get; set; }
    public string Name { get; set; } = "";
    public double? Quantity { get; set; }
    public string? Unit { get; set; }
    /// <summary>Free-text quantity ("2 bags") when a number does not fit.</summary>
    public string? QuantityText { get; set; }
    public string? Store { get; set; }
    public string? Notes { get; set; }
    public bool Done { get; set; }
}

public class RecipeLayer
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = "";
    /// <summary>#RRGGBB.</summary>
    public string BackColor { get; set; } = "#FFFFFF";
    public string ForeColor { get; set; } = "#000000";
}

public class RecipeFile
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public RecipeFileKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public string? Caption { get; set; }
    public byte[] Data { get; set; } = [];
}

public class AppSetting
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}
