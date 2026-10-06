using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SvetaRecipes.App.Views;

public partial class RecipesView : UserControl
{
    public RecipesView()
    {
        InitializeComponent();
        // Recipes opened from elsewhere (a sub-recipe line, "Used in") must be visible in the tree, not just selected.
        Tree.SelectionChanged += (_, _) =>
        {
            if (Tree.SelectedItem is not { } item) return;
            Dispatcher.UIThread.Post(() =>
                Tree.GetVisualDescendants().OfType<TreeViewItem>().FirstOrDefault(c => ReferenceEquals(c.DataContext, item))?.BringIntoView(),
                DispatcherPriority.Background);
        };
    }
}
