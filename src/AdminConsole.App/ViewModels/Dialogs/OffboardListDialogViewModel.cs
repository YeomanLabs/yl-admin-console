using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AdminConsole.Core.Cache;
using AdminConsole.Core.Models;
using AdminConsole.Core.Offboarding;
using AdminConsole.Core.Search;

namespace AdminConsole.App.ViewModels.Dialogs;

/// <summary>
/// Offboard a list (M15.5): paste names or sign-in names, or import them from a CSV file, see who each one is, settle
/// every line that is not exactly one person, then offboard them one at a time — each with its own preview, in the
/// ordinary offboard dialog. An imported file only fills the list; it never skips the per-person preview.
/// </summary>
public sealed partial class OffboardListDialogViewModel : DialogViewModelBase
{
    private readonly CacheDb _db;
    private readonly SearchService _search;

    // An imported file whose column the admin still has to choose, and the text the last import put in the list.
    private CsvTable? _pendingTable;
    private string _pendingFile = string.Empty;
    private string? _importedText;

    public OffboardListDialogViewModel(CacheDb db, SearchService search)
    {
        _db = db;
        _search = search;
    }

    public override string Title => "Offboard a list";

    public override double Width => 720;

    /// <summary>
    /// Asks for the CSV to import; null when the admin cancels. The shell sets it to the Windows open dialog. It is a
    /// delegate so a harness can hand over a file, because that dialog cannot be driven by UI Automation.
    /// </summary>
    public Func<string?>? PickCsvFile { get; init; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    private string _text = string.Empty;

    public ObservableCollection<OffboardListRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The people to offboard, in the order they were pasted.</summary>
    public IReadOnlyList<DirectoryUser> Chosen => [.. Rows.Select(r => r.Chosen).OfType<DirectoryUser>()];

    public int Unresolved => Rows.Count(r => !r.IsResolved);

    public override bool CanConfirm => HasRows && Unresolved == 0 && Chosen.Count > 0;

    public override string ConfirmText => Chosen.Count == 1 ? "Offboard 1 person" : $"Offboard {Chosen.Count} people";

    public override bool IsDestructive => true;

    public string? StatusText => !HasRows ? null
        : Unresolved > 0 ? $"{Unresolved} line{(Unresolved == 1 ? " needs" : "s need")} a choice before anything can run."
        : Chosen.Count == 0 ? "Every line is skipped."
        : $"Each person gets their own preview, one after another. Closing a preview stops the list there.";

    /// <summary>Which file and column the list came from, and how many rows — cleared once the admin edits the list.</summary>
    [ObservableProperty]
    private string? _importNote;

    [ObservableProperty]
    private string? _importError;

    /// <summary>Set while an imported file waits for the admin to say which column holds the people.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingColumn))]
    private string? _columnPrompt;

    public bool IsChoosingColumn => ColumnPrompt is not null;

    public ObservableCollection<CsvColumnChoice> ColumnChoices { get; } = [];

    private bool CanFind() => Text.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanFind))]
    private void Find()
    {
        Rows.Clear();
        foreach (var entry in OffboardList.Resolve(OffboardList.Parse(Text), _db, _search)) Rows.Add(new OffboardListRow(entry, Changed));
        Changed();
    }

    /// <summary>
    /// Reads a CSV and puts the people column in the list, one per line, then finds them exactly as if they had been
    /// pasted. The column is chosen by its header; when no header is one the console knows, the admin is asked.
    /// </summary>
    [RelayCommand]
    private async Task ImportCsvAsync(CancellationToken cancellationToken)
    {
        var path = PickCsvFile?.Invoke();
        if (path is null) return;

        // A new file: whatever was said about the last one no longer applies.
        var file = Path.GetFileName(path);
        ClearColumnChoice();
        ImportError = null;
        ImportNote = null;

        CsvTable table;
        try
        {
            table = await Task.Run(() => LeaverCsv.Parse(LeaverCsv.ReadFile(path)), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ImportError = $"{file} could not be read. {ex.Message}";
            return;
        }

        if (table.Rows.Count == 0)
        {
            ImportError = $"{file} is empty.";
        }
        else if (LeaverCsv.FindColumn(table.Rows[0]) is int column)
        {
            Apply(table, LeaverCsv.Take(table, column, hasHeader: true), file);
        }
        else if (table.ColumnCount == 1)
        {
            Apply(table, LeaverCsv.Take(table, 0, hasHeader: false), file);
        }
        else
        {
            _pendingTable = table;
            _pendingFile = file;
            for (var i = 0; i < table.ColumnCount; i++) ColumnChoices.Add(new CsvColumnChoice(i, ColumnLabel(table, i)));
            ColumnPrompt = $"No column in {file} has a name the console knows ({string.Join(", ", LeaverCsv.KnownColumns)}). "
                + "Which column holds the people to offboard?";
        }
    }

    [RelayCommand]
    private void UseColumn(CsvColumnChoice? choice)
    {
        if (_pendingTable is { } table && choice is not null) Apply(table, LeaverCsv.Take(table, choice.Index, hasHeader: false), _pendingFile);
    }

    [RelayCommand]
    private void CancelImport() => ClearColumnChoice();

    private void Apply(CsvTable table, CsvColumn column, string file)
    {
        ClearColumnChoice();
        if (column.Values.Count == 0)
        {
            ImportError = column.Header is { } name ? $"{file} has a \"{name}\" column, but nothing in it." : $"{file} has nothing in column {column.Index + 1}.";
            return;
        }

        var note = new StringBuilder($"Read {Count(column.Values.Count, "row")} from {file}, ");
        note.Append(column.Header is { } header ? $"column \"{header}\"."
            : table.ColumnCount == 1 ? "its only column."
            : $"column {column.Index + 1}.");
        if (column.Header is null)
        {
            note.Append(" No column name was recognised, so the first row is read as a person too — if it is a heading, skip it below.");
        }

        if (column.EmptyRows > 0)
        {
            note.Append($" {Count(column.EmptyRows, "row")} with nothing in that column {(column.EmptyRows == 1 ? "was" : "were")} left out.");
        }

        if (table.HasUnclosedQuote)
        {
            note.Append(" A quote in the file is never closed, so its last lines may have been read as one — check the end of the list.");
        }

        _importedText = string.Join("\r\n", column.Values);
        Text = _importedText;
        ImportNote = note.ToString();
        Find();
    }

    private void ClearColumnChoice()
    {
        _pendingTable = null;
        ColumnChoices.Clear();
        ColumnPrompt = null;
    }

    /// <summary>"Column 2: Employee · Adele Vance" — the first two things in it, so the admin can tell the columns apart.</summary>
    private static string ColumnLabel(CsvTable table, int index)
    {
        var samples = table.Rows
            .Select(r => index < r.Count ? r[index].Trim() : string.Empty)
            .Where(v => v.Length > 0)
            .Take(2)
            .Select(v => v.Length > 40 ? v[..39] + "…" : v);
        var text = string.Join(" · ", samples);
        return text.Length == 0 ? $"Column {index + 1} (empty)" : $"Column {index + 1}: {text}";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    partial void OnTextChanged(string value)
    {
        if (value != _importedText) ImportNote = null;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(Unresolved));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(StatusText));
    }
}
