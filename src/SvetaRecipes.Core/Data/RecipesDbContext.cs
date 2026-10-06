using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SvetaRecipes.Core.Model;

namespace SvetaRecipes.Core.Data;

public class RecipesDbContext(DbContextOptions<RecipesDbContext> options) : DbContext(options)
{
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeLine> RecipeLines => Set<RecipeLine>();
    public DbSet<RecipeLayer> RecipeLayers => Set<RecipeLayer>();
    public DbSet<RecipeFile> RecipeFiles => Set<RecipeFile>();
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<RecipeCategory> RecipeCategories => Set<RecipeCategory>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<GroceryCategory> GroceryCategories => Set<GroceryCategory>();
    public DbSet<SupplierCategory> SupplierCategories => Set<SupplierCategory>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<SupplierPrice> SupplierPrices => Set<SupplierPrice>();
    public DbSet<MeasureUnit> Units => Set<MeasureUnit>();
    public DbSet<UnitConversion> UnitConversions => Set<UnitConversion>();
    public DbSet<Substance> Substances => Set<Substance>();
    public DbSet<DensityConversionUnit> DensityConversionUnits => Set<DensityConversionUnit>();
    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();
    public DbSet<ShoppingItem> ShoppingItems => Set<ShoppingItem>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    public static RecipesDbContext Open(string dbPath) =>
        new(new DbContextOptionsBuilder<RecipesDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Recipe>(e =>
        {
            e.Property(x => x.Name).IsRequired();
            e.HasIndex(x => x.Name);
            e.HasOne<RecipeCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Layers).WithOne().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Files).WithOne().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tags).WithMany(x => x.Recipes).UsingEntity("RecipeTags");
        });

        b.Entity<RecipeLine>(e =>
        {
            e.HasIndex(x => new { x.RecipeId, x.Position });
            // A line may not dangle: deleting a used ingredient or sub-recipe is refused, not silently orphaned.
            e.HasOne<Ingredient>().WithMany().HasForeignKey(x => x.IngredientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Recipe>().WithMany().HasForeignKey(x => x.SubRecipeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Ingredient>(e =>
        {
            e.Property(x => x.Name).IsRequired();
            e.HasIndex(x => x.Name);
            e.HasOne<GroceryCategory>().WithMany().HasForeignKey(x => x.GroceryCategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.SupplierPrices).WithOne().HasForeignKey(x => x.IngredientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Company>(e =>
        {
            e.Property(x => x.Name).IsRequired();
            e.HasMany(x => x.Prices).WithOne().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Categories).WithMany(x => x.Companies).UsingEntity("SupplierToCategories");
        });

        b.Entity<Invoice>(e =>
        {
            e.Ignore(x => x.NetAmount);
            e.Ignore(x => x.Total);
            e.Ignore(x => x.Balance);
            e.HasIndex(x => x.Date);
            // A customer with invoices can't be deleted (as in the legacy app).
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<InvoiceItem>().Ignore(x => x.Amount);

        b.Entity<MeasureUnit>().HasKey(x => x.Code);
        b.Entity<UnitConversion>().HasKey(x => new { x.From, x.To });
        b.Entity<DensityConversionUnit>().HasKey(x => x.Name);
        b.Entity<AppSetting>().HasKey(x => x.Key);

        b.Entity<ShoppingList>()
            .HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ShoppingListId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ShoppingItem>()
            .HasOne<Ingredient>().WithMany().HasForeignKey(x => x.IngredientId).OnDelete(DeleteBehavior.SetNull);

        // Case-insensitive names everywhere the user types them (legacy Access compared text case-insensitively).
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties().Where(p => p.ClrType == typeof(string)))
            prop.SetCollation("NOCASE");
    }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without the app.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<RecipesDbContext>
{
    public RecipesDbContext CreateDbContext(string[] args) => RecipesDbContext.Open("design.db");
}
