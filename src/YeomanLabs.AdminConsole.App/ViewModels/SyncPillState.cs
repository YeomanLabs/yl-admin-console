namespace YeomanLabs.AdminConsole.App.ViewModels;

/// <summary>Colour of the title-bar sync pill (UX §2).</summary>
public enum SyncPillState
{
    /// <summary>Not signed in yet.</summary>
    Offline,

    /// <summary>A cycle is running (initial walk or delta).</summary>
    Syncing,

    /// <summary>Last cycle succeeded; cache is current.</summary>
    Ok,

    /// <summary>Last cycle had at least one entity failure.</summary>
    Error,
}
