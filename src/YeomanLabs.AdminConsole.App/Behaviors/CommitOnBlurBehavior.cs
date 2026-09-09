using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace YeomanLabs.AdminConsole.App.Behaviors;

/// <summary>
/// <c>b:CommitOnBlur.Command="{Binding CommitCommand}"</c> on a TextBox: runs the command when focus leaves and on
/// Enter, and runs <c>RevertCommand</c> on Esc. UX §4 tab 1 is edit-on-blur with no Save button.
/// </summary>
public static class CommitOnBlur
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(CommitOnBlur), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject obj) => (ICommand?)obj.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject obj, ICommand? value) => obj.SetValue(CommandProperty, value);

    public static readonly DependencyProperty RevertCommandProperty = DependencyProperty.RegisterAttached(
        "RevertCommand", typeof(ICommand), typeof(CommitOnBlur), new PropertyMetadata(null));

    public static ICommand? GetRevertCommand(DependencyObject obj) => (ICommand?)obj.GetValue(RevertCommandProperty);

    public static void SetRevertCommand(DependencyObject obj, ICommand? value) => obj.SetValue(RevertCommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.LostKeyboardFocus -= OnLostFocus;
        box.PreviewKeyDown -= OnKeyDown;
        if (e.NewValue is null) return;
        box.LostKeyboardFocus += OnLostFocus;
        box.PreviewKeyDown += OnKeyDown;
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // LostKeyboardFocus runs before LostFocus, which is what UpdateSourceTrigger=LostFocus waits for, so the
        // ViewModel would still hold the pre-edit text. Push it first, then commit.
        if (sender is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Run((DependencyObject)sender, CommandProperty);
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                // Push the text into the binding first, then commit; the binding is LostFocus-triggered by default.
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Run(box, CommandProperty);
                Keyboard.ClearFocus();
                break;
            case Key.Escape:
                e.Handled = true;
                Run(box, RevertCommandProperty);
                break;
        }
    }

    private static void Run(DependencyObject target, DependencyProperty property)
    {
        if (target.GetValue(property) is ICommand command && command.CanExecute(null)) command.Execute(null);
    }
}
