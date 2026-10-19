using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>How a starter is set up: from a saved template, or by copying someone who already has the role.</summary>
public enum OnboardChoice
{
    None,
    Template,
    CopyPerson,
}

/// <summary>
/// Onboard someone (Home, Ctrl+N, the palette): the one question before the create form — a template, or a copy of a
/// person's setup. Copying uses the template snapshot rules, so the same things are copied either way and nothing
/// personal ever is.
/// </summary>
public sealed partial class OnboardDialogViewModel : DialogViewModelBase
{
    public OnboardDialogViewModel(int templateCount)
    {
        TemplateCount = templateCount;
    }

    public override string Title => "Onboard someone";

    public override bool ShowFooter => false;

    public override double Width => 600;

    public int TemplateCount { get; }

    public string TemplateDetail => TemplateCount switch
    {
        0 => "No templates yet — make one on the Templates page, or copy someone instead.",
        1 => "1 saved template: the role's attributes, licences and groups, ready to fill in.",
        _ => $"{TemplateCount} saved templates: pick a role, then fill in the person.",
    };

    public bool HasTemplates => TemplateCount > 0;

    public string CopyDetail =>
        "Pick someone who already has the role. Their job details, direct licences, cloud groups and manager are copied — never their name, sign-in or anything personal — and you see it all before anything is created.";

    [ObservableProperty]
    private OnboardChoice _choice;

    [RelayCommand]
    private void UseTemplate()
    {
        Choice = OnboardChoice.Template;
        ConfirmCommand.Execute(null);
    }

    [RelayCommand]
    private void CopyPerson()
    {
        Choice = OnboardChoice.CopyPerson;
        ConfirmCommand.Execute(null);
    }
}
