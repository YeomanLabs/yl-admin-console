namespace AdminConsole.Core.Users.BulkEdit;

/// <summary>One property a bulk edit can set (M18.3): a Graph property name and the label the sheet shows.</summary>
public sealed record BulkField(string Property, string Label)
{
    /// <summary>
    /// The properties it makes sense to give many people the same value of. Names, sign-in names and phone numbers are
    /// absent on purpose: the same value on twenty accounts is a mistake, not an edit.
    /// </summary>
    public static IReadOnlyList<BulkField> All { get; } =
    [
        new("jobTitle", "Job title"),
        new("department", "Department"),
        new("companyName", "Company"),
        new("officeLocation", "Office"),
        new("employeeType", "Employee type"),
        new("streetAddress", "Street"),
        new("city", "City"),
        new("state", "State or province"),
        new("postalCode", "Postal code"),
        new("country", "Country or region"),
        new("usageLocation", "Usage location"),
    ];

    public override string ToString() => Label;
}
