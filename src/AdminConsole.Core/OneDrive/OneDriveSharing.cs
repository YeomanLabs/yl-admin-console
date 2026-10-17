namespace AdminConsole.Core.OneDrive;

/// <summary>
/// Who a OneDrive's owner may share with — SharePoint's <c>SharingCapability</c>, with the numbers it stores (M16).
/// The numbers are not in order of openness, so <see cref="OneDriveSharingText.Rank"/> is what to compare.
/// </summary>
public enum OneDriveSharing
{
    /// <summary>Only people in the organisation.</summary>
    Disabled = 0,

    /// <summary>New and existing guests, who must sign in.</summary>
    ExternalUserSharingOnly = 1,

    /// <summary>Anyone, including links that need no sign-in.</summary>
    ExternalUserAndGuestSharing = 2,

    /// <summary>Only guests already in the directory.</summary>
    ExistingExternalUserSharingOnly = 3,
}
