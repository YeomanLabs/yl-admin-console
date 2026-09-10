using System.Windows;
using System.Windows.Controls;

namespace AdminConsole.App.Views;

/// <summary>
/// The chrome every list page shares (M9.2–9.4): heading, count, filter box, sort, chips, empty state and paging.
/// Each page supplies only its rows, through <see cref="RowsContent"/>.
/// </summary>
/// <remarks>
/// This started as a <c>ControlTemplate</c>, which rendered correctly but put nothing in the automation tree: WPF does
/// not create automation peers for <c>TextBlock</c>s that are template children, so the heading, the result count and
/// the empty-state text were invisible to a screen reader — including the live region that announces the count. A
/// UserControl's children are ordinary visual children, so they are exposed like any other text on the page.
/// </remarks>
public partial class ListPageView : UserControl
{
    public ListPageView() => InitializeComponent();

    public static readonly DependencyProperty RowsContentProperty =
        DependencyProperty.Register(nameof(RowsContent), typeof(object), typeof(ListPageView), new PropertyMetadata(null));

    /// <summary>The page's rows. Set by each page's DataTemplate; everything around it is shared.</summary>
    public object? RowsContent
    {
        get => GetValue(RowsContentProperty);
        set => SetValue(RowsContentProperty, value);
    }
}
