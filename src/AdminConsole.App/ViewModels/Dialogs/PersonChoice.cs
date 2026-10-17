using AdminConsole.Core.Models;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>A candidate for an ambiguous line, labelled with enough to tell two people of one name apart.</summary>
public sealed record PersonChoice(DirectoryUser User)
{
    public string Label
    {
        get
        {
            var parts = new[] { User.DisplayName, User.UserPrincipalName, User.JobTitle, User.Department, User.AccountEnabled ? null : "disabled" }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            return string.Join(" · ", parts);
        }
    }

    public override string ToString() => Label;
}
