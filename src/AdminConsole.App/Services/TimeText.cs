using System.Globalization;

namespace AdminConsole.App.Services;

/// <summary>Human time captions: "3 h ago", "2026-09-09 01:00 UTC". Local time for absolute stamps, since admins read their own clock.</summary>
public static class TimeText
{
    public static string Relative(DateTimeOffset? at, DateTimeOffset? now = null)
    {
        if (at is null) return "never";
        var age = (now ?? DateTimeOffset.UtcNow) - at.Value;
        if (age < TimeSpan.Zero) return "just now";
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(30)) return $"{(int)age.TotalDays} d ago";
        if (age < TimeSpan.FromDays(365)) return $"{(int)(age.TotalDays / 30)} mo ago";
        return $"{(int)(age.TotalDays / 365)} y ago";
    }

    public static string Absolute(DateTimeOffset? at) =>
        at is null ? "—" : at.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"3 h ago · 2026-09-09 01:00"</summary>
    public static string Both(DateTimeOffset? at) => at is null ? "—" : $"{Relative(at)} · {Absolute(at)}";
}
