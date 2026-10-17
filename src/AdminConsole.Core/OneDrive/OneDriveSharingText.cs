namespace AdminConsole.Core.OneDrive;

/// <summary>Words and order for <see cref="OneDriveSharing"/>, as the SharePoint admin centre puts them.</summary>
public static class OneDriveSharingText
{
    public static string Describe(OneDriveSharing s) => s switch
    {
        OneDriveSharing.Disabled => "Only people in the organisation",
        OneDriveSharing.ExistingExternalUserSharingOnly => "Existing guests",
        OneDriveSharing.ExternalUserSharingOnly => "New and existing guests",
        _ => "Anyone",
    };

    /// <summary>Least open first: 0 organisation only, 1 existing guests, 2 new guests, 3 anyone.</summary>
    public static int Rank(OneDriveSharing s) => s switch
    {
        OneDriveSharing.Disabled => 0,
        OneDriveSharing.ExistingExternalUserSharingOnly => 1,
        OneDriveSharing.ExternalUserSharingOnly => 2,
        _ => 3,
    };

    /// <summary>Every setting no more open than <paramref name="ceiling"/>, the tenant's own — a site can't exceed it.</summary>
    public static IReadOnlyList<OneDriveSharing> UpTo(OneDriveSharing ceiling) =>
        [.. Enum.GetValues<OneDriveSharing>().Where(s => Rank(s) <= Rank(ceiling)).OrderBy(Rank)];
}
