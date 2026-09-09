using System.Windows;

namespace YeomanLabs.AdminConsole.App.Behaviors;

/// <summary>
/// <c>b:HoverBehavior.Track="True" b:HoverBehavior.IsHovered="{Binding IsHovered, Mode=TwoWay}"</c> on any element:
/// mirrors mouse-over into a ViewModel property (toasts pause their dismiss timer while hovered, UX §10).
/// <c>IsMouseOver</c> itself cannot be bound to source.
/// </summary>
public static class HoverBehavior
{
    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered", typeof(bool), typeof(HoverBehavior),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static bool GetIsHovered(DependencyObject obj) => (bool)obj.GetValue(IsHoveredProperty);

    public static void SetIsHovered(DependencyObject obj, bool value) => obj.SetValue(IsHoveredProperty, value);

    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(HoverBehavior), new PropertyMetadata(false, OnTrackChanged));

    public static bool GetTrack(DependencyObject obj) => (bool)obj.GetValue(TrackProperty);

    public static void SetTrack(DependencyObject obj, bool value) => obj.SetValue(TrackProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || e.NewValue is not true) return;
        element.MouseEnter += (_, _) => element.SetCurrentValue(IsHoveredProperty, true);
        element.MouseLeave += (_, _) => element.SetCurrentValue(IsHoveredProperty, false);
    }
}
