using System.Data;
using System.Windows.Controls;
using SqlHelper.App.ViewModels;

namespace SqlHelper.App.Views;

public partial class SelectQueryView : UserControl
{
    public SelectQueryView() => InitializeComponent();

    /// <summary>
    /// Puts the real column name back in the header.
    ///
    /// Result columns are held under safe internal names (see
    /// <see cref="QueryResultPresentation.InternalName"/>) because the DataGrid binds a generated
    /// column to its name as a property path, which an unaliased <c>COUNT(*)</c> breaks. The
    /// operator should still see the name the server gave it.
    /// </summary>
    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        DataTable? table = (sender as DataGrid)?.ItemsSource is DataView view ? view.Table : null;
        e.Column.Header = QueryResultPresentation.HeaderTextFor(table, e.PropertyName);
    }
}
