using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace EfAssist.App.Views;

/// <summary>
/// The search over the settings screen. Rows carry their own search terms as an attached property,
/// and this hides the ones that do not match.
/// </summary>
/// <remarks>
/// In the view rather than the view model because the rows are declared in XAML: a parallel list of
/// them in a view model would be a second copy to keep in step, and the first thing to rot would be
/// exactly the setting nobody could find. The terms are the words a person would type that are not
/// already in the row's own text — flags like <c>--no-build</c>, or synonyms like "colour" for the
/// theme — since the labels and hints are searched automatically.
/// </remarks>
public static class SettingsSearch
{
    /// <summary>
    /// Extra words this row should match, space-separated. Set on the row's outermost element; the
    /// row is what gets hidden, so a setting and its explanation disappear together.
    /// </summary>
    /// <remarks>
    /// A row carrying this must not also bind its own <c>IsVisible</c>: <see cref="Apply"/> assigns
    /// that property, and a local value beats a binding, so the row would be forced visible the first
    /// time a search ran — which is how the dotnet-ef update failure appeared with no failure behind
    /// it. A conditional row puts the condition on a wrapper around it instead.
    /// </remarks>
    public static readonly AttachedProperty<string?> TermsProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Terms", typeof(SettingsSearch));

    public static void SetTerms(Control element, string? value) => element.SetValue(TermsProperty, value);

    public static string? GetTerms(Control element) => element.GetValue(TermsProperty);

    /// <summary>
    /// Hides the rows in one pane that do not match <paramref name="query"/>, and reports how many
    /// were left showing. An empty query shows everything.
    /// </summary>
    public static (int Visible, int Total) Apply(Control pane, string query)
    {
        var rows = Rows(pane);
        var trimmed = query.Trim();

        if (trimmed.Length == 0)
        {
            ShowAll(pane);
            return (rows.Count, rows.Count);
        }

        var visible = 0;
        foreach (var row in rows)
        {
            var matches = Matches(row, trimmed);
            row.IsVisible = matches;

            if (matches)
            {
                visible++;
            }
        }

        return (visible, rows.Count);
    }

    /// <summary>
    /// Puts every row back. Used when the query matches the category itself rather than any one row —
    /// searching "diagrams" should show the Diagrams pane, not an empty one.
    /// </summary>
    public static void ShowAll(Control pane)
    {
        foreach (var row in Rows(pane))
        {
            row.IsVisible = true;
        }
    }

    /// <summary>
    /// The searchable rows of a pane: every element carrying <see cref="TermsProperty"/>. The logical
    /// tree rather than the visual one, so a pane that has never been shown still has rows to filter.
    /// </summary>
    private static List<Control> Rows(Control pane) =>
        [.. pane.GetLogicalDescendants().OfType<Control>().Where(c => c.IsSet(TermsProperty))];

    /// <summary>
    /// Whether a row matches. Its declared terms plus the text it displays, so a label never has to be
    /// repeated in the terms and a hint mentioning the flag counts on its own.
    /// </summary>
    private static bool Matches(Control row, string query)
    {
        if (Contains(GetTerms(row), query))
        {
            return true;
        }

        foreach (var text in row.GetLogicalDescendants().OfType<TextBlock>())
        {
            if (Contains(text.Text, query))
            {
                return true;
            }
        }

        // A checkbox carries its label as content rather than as a child TextBlock.
        foreach (var box in row.GetSelfAndLogicalDescendants().OfType<ContentControl>())
        {
            if (box.Content is string label && Contains(label, query))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string? haystack, string query) =>
        haystack is not null && haystack.Contains(query, System.StringComparison.OrdinalIgnoreCase);
}
