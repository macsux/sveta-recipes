using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Controls;
using SvetaRecipes.App.Services;
using SvetaRecipes.Core.Services;

namespace SvetaRecipes.App.ViewModels;

/// <summary>A top-level tab. Activated whenever it is shown, so it can refresh numbers other tabs changed.</summary>
public interface ISection
{
    void Activate();
}

public partial class MainViewModel : ViewModelBase
{
    public RecipeBook Book { get; }
    public IDialogs Dialogs { get; }

    public RecipesViewModel Recipes { get; }
    public IngredientsViewModel Ingredients { get; }
    public ShoppingViewModel Shopping { get; }
    public InvoicesViewModel Invoices { get; }
    public CompaniesViewModel Companies { get; }
    public ToolsViewModel Tools { get; }

    [ObservableProperty] private int _selectedTab;

    public MainViewModel(RecipeBook book, Window window)
    {
        Book = book;
        Dialogs = new WindowDialogs(window);
        Recipes = new RecipesViewModel(this);
        Ingredients = new IngredientsViewModel(this);
        Shopping = new ShoppingViewModel(this);
        Invoices = new InvoicesViewModel(this);
        Companies = new CompaniesViewModel(this);
        Tools = new ToolsViewModel(this);
        Recipes.Activate();
    }

    private ISection Section(int index) => index switch
    {
        0 => Recipes,
        1 => Ingredients,
        2 => Shopping,
        3 => Invoices,
        4 => Companies,
        _ => Tools,
    };

    partial void OnSelectedTabChanged(int value) => Section(value).Activate();

    public async void GoToRecipe(int recipeId)
    {
        SelectedTab = 0;
        await Recipes.SelectRecipe(recipeId);
    }

    public void GoToIngredient(int ingredientId)
    {
        SelectedTab = 1;
        Ingredients.Select(ingredientId);
    }

    public async void GoToShoppingList(int listId)
    {
        SelectedTab = 2;
        Shopping.Select(listId);
        await Task.CompletedTask;
    }

    public void GoToCompany(int companyId)
    {
        SelectedTab = 4;
        Companies.Select(companyId);
    }

    /// <summary>Asks about unsaved edits before the app closes. False = stay open.</summary>
    public async Task<bool> CanClose() => await Recipes.ConfirmLeave() && await Companies.ConfirmLeave();
}
