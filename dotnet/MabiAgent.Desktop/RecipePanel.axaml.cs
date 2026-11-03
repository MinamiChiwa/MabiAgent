using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
namespace MabiAgent.Desktop;
public partial class RecipePanel:UserControl
{
    public RecipePanel()=>InitializeComponent();
    // Double-clicking a recipe row is a shortcut for the toolbar's create action.
    private void CreateFromRow(object? sender,TappedEventArgs e)
    {
        if(DataContext is not MainViewModel model||(e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().Any()!=true)return;
        if(model.CreateRecipeGoalCommand.CanExecute(null))model.CreateRecipeGoalCommand.Execute(null);
    }
}
