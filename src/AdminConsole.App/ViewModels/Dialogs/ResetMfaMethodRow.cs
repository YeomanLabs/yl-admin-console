using CommunityToolkit.Mvvm.ComponentModel;
using AdminConsole.Core.Models;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>One method in the Reset MFA preview, ticked to go.</summary>
public sealed partial class ResetMfaMethodRow : ObservableObject
{
    private readonly Action _changed;

    public ResetMfaMethodRow(AuthMethod method, Action changed)
    {
        Method = method;
        _changed = changed;
    }

    public AuthMethod Method { get; }

    public string Label => Method.Detail is null
        ? $"{Method.KindLabel} · {Method.DisplayName}"
        : $"{Method.KindLabel} · {Method.DisplayName} · {Method.Detail}";

    [ObservableProperty]
    private bool _isChecked = true;

    partial void OnIsCheckedChanged(bool value) => _changed();

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => IsChecked ? $"{Label}, will be removed" : $"{Label}, kept";
}
