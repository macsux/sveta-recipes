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
    public ChatViewModel Assistant { get; }
    /// <summary>Release / Development mode and the build-and-apply bar.</summary>
    public DevViewModel Dev { get; }

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isAssistantOpen;
    /// <summary>A downloaded update's version while the "restart to update" bar is up.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsUpdateReady))] private string? _updateReady;
    public bool IsUpdateReady => UpdateReady is not null;

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
        Dev = new DevViewModel(this);
        Assistant = new ChatViewModel(this);
        _isAssistantOpen = book.GetSetting(SettingKeys.AssistantOpen) == "1";
        Recipes.Activate();
        Updates.Ready += () => UpdateReady = Updates.ReadyVersion;
        _updateReady = Updates.ReadyVersion;
    }

    /// <summary>"Later": the bar goes away; the update still installs when the app closes.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DismissUpdate() => UpdateReady = null;

    partial void OnIsAssistantOpenChanged(bool value) => Book.SetSetting(SettingKeys.AssistantOpen, value ? "1" : "0");

    /// <summary>The assistant wrote to the database (the book is already reloaded): bring the open screens up to date.</summary>
    public void DataChangedOutside()
    {
        Recipes.Reloaded();
        Section(SelectedTab).Activate();
    }

    /// <summary>What she is looking at, for the assistant.</summary>
    public string DescribeView()
    {
        string[] tabs = ["Recipes", "Ingredients", "Shopping lists", "Invoices", "Customers & suppliers", "Tools & settings"];
        var tab = tabs[Math.Clamp(SelectedTab, 0, tabs.Length - 1)] + " tab";
        var view = SelectedTab == 0 && Recipes.Editor is { Id: > 0 } e ? $"{tab}, recipe {e.Id} \"{e.Name}\"{(e.IsDirty ? " (unsaved edits)" : "")}" : tab;
        return Dev.Describe() is { } dev ? $"{view}; {dev}" : view;
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
