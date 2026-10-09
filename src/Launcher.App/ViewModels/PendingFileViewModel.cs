using PerforceLauncher.Core.Models;

namespace PerforceLauncher.App.ViewModels;

/// <summary>Строка вкладки Pending changes: открытый файл либо заглушка «(empty)» пустого changelist'а.</summary>
public sealed class PendingFileViewModel
{
    public PendingFileViewModel(PendingChangelist changelist, OpenedFile? file, string displayPath)
    {
        ChangelistId = changelist.Id;
        File = file;
        DisplayPath = displayPath;
        var desc = changelist.Description;
        var nl = desc.IndexOfAny(new[] { '\r', '\n' });
        var first = nl < 0 ? desc : desc[..nl];
        Group = changelist.IsDefault
            ? $"Default changelist ({changelist.Files.Count})"
            : $"{changelist.Title}: {first} ({changelist.Files.Count})";
    }

    public long ChangelistId { get; }
    public OpenedFile? File { get; }
    public bool IsPlaceholder => File == null;
    public string Group { get; }
    public string Action => File?.Action ?? "";
    public string Type => File?.Type ?? "";
    public string DisplayPath { get; }

    /// <summary>Файл под правилом P4IGNORE, но уже в депо: reconcile/edit его открывает, а коммитить обычно не нужно.</summary>
    public bool IsIgnored => File?.IsIgnored == true;
    public string Marker => IsIgnored ? "⚠" : "";
    public string MarkerTip => IsIgnored
        ? $"Matches .p4ignore ({File!.IgnoredBy}) but is already tracked in the depot.\nUsually a generated/local file: revert it or move it to a separate changelist instead of submitting."
        : "";
}
