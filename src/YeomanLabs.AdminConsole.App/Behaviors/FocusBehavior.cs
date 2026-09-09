using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace YeomanLabs.AdminConsole.App.Behaviors;

/// <summary>
/// <c>behaviors:FocusBehavior.FocusWhen="{Binding IsOpen}"</c>: focuses the element (and selects its text) whenever the bound
/// value becomes true. Lets the palette take keyboard focus without code in the view's code-behind.
/// </summary>
public static class FocusBehavior
{
    public static readonly DependencyProperty FocusWhenProperty = DependencyProperty.RegisterAttached(
        "FocusWhen", typeof(bool), typeof(FocusBehavior), new PropertyMetadata(false, OnFocusWhenChanged));

    public static bool GetFocusWhen(DependencyObject obj) => (bool)obj.GetValue(FocusWhenProperty);

    public static void SetFocusWhen(DependencyObject obj, bool value) => obj.SetValue(FocusWhenProperty, value);

    private static void OnFocusWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || e.NewValue is not true) return;

        // Defer one frame: the element may be collapsed until the same binding pass makes its host visible.
        element.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            element.Focus();
            Keyboard.Focus(element);
            if (element is TextBox tb) tb.SelectAll();
        });
    }
}
