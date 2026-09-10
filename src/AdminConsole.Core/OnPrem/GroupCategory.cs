namespace AdminConsole.Core.OnPrem;

/// <summary>Whether an AD group grants access or only distributes mail. A distribution group carries no rights.</summary>
public enum GroupCategory
{
    Security,
    Distribution,
}
