using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SvetaRecipes.App.ViewModels;

namespace SvetaRecipes.App;

/// <summary>Maps FooViewModel to FooView.</summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;
        var name = param.GetType().FullName!.Replace("ViewModels", "Views").Replace("ViewModel", "View");
        return Type.GetType(name) is { } type ? (Control)Activator.CreateInstance(type)! : new TextBlock { Text = "Missing view: " + name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
