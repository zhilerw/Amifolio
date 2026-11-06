namespace Amifolio.Desktop;

internal sealed class DocumentEditState
{
    public string OriginalText { get; private set; } = "";
    public bool CanEdit { get; private set; }
    public bool IsEditing { get; private set; }

    public void Load(string text, bool canEdit)
    {
        OriginalText = text;
        CanEdit = canEdit;
        IsEditing = false;
    }

    public bool Begin()
    {
        if (!CanEdit) return false;
        IsEditing = true;
        return true;
    }

    public bool HasChanges(string text) => IsEditing && Normalize(text) != Normalize(OriginalText);
    public string Cancel() { IsEditing = false; return OriginalText; }

    public string TextForSave(string text)
    {
        if (!IsEditing) throw new InvalidOperationException("Document is not being edited.");
        if (!HasChanges(text)) return OriginalText;
        var originalLines = Normalize(OriginalText).Split('\n');
        var editedLines = Normalize(text).Split('\n');
        var endings = System.Text.RegularExpressions.Regex.Matches(OriginalText, "\\r\\n|\\r|\\n")
            .Select(x => x.Value).ToArray();
        var preferred = endings.FirstOrDefault() ?? "\n";
        var prefix = 0;
        while (prefix < Math.Min(originalLines.Length, editedLines.Length) && originalLines[prefix] == editedLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(originalLines.Length, editedLines.Length) - prefix && originalLines[^(suffix + 1)] == editedLines[^(suffix + 1)]) suffix++;
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < editedLines.Length; i++)
        {
            result.Append(editedLines[i]);
            if (i == editedLines.Length - 1) continue;
            var originalIndex = i < prefix ? i : i >= editedLines.Length - suffix ? originalLines.Length - editedLines.Length + i : -1;
            result.Append(originalIndex >= 0 && originalIndex < endings.Length ? endings[originalIndex] : preferred);
        }
        return result.ToString();
    }

    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
