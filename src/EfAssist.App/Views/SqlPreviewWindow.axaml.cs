using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using EfAssist.App.ViewModels;

namespace EfAssist.App.Views;

/// <summary>
/// Shows generated SQL read-only, over the confirmation that asked for it. Its own window rather
/// than a panel inside the dialog: a confirmation is a fixed-size question, and SQL needs room,
/// scrolling and Ctrl+F to be worth reading at all.
/// </summary>
public partial class SqlPreviewWindow : Window
{
    private readonly Func<string, Task>? _openFileAsync;
    private readonly string _path;

    /// <summary>Parameterless constructor exists only for the XAML previewer.</summary>
    public SqlPreviewWindow() : this(new SqlPreviewRequest("SQL preview", "SELECT 1;", ""), null)
    {
    }

    public SqlPreviewWindow(SqlPreviewRequest request, Func<string, Task>? openFileAsync)
    {
        ArgumentNullException.ThrowIfNull(request);

        _openFileAsync = openFileAsync;
        _path = request.Path;

        InitializeComponent();
        DataContext = request;

        SearchPanel.Install(Editor);
        Editor.Document = new TextDocument(request.Sql);

        // A definition holds literal colours, so switching variant needs a different definition
        // rather than a repaint. Same reason as the Script tab's editor.
        Editor.ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        ApplyHighlighting();

        CopyButton.Click += async (_, _) =>
        {
            IClipboard? clipboard = Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(request.Sql);
            }
        };

        OpenButton.IsEnabled = _openFileAsync is not null && _path.Length > 0;
        OpenButton.Click += async (_, _) =>
        {
            if (_openFileAsync is not null)
            {
                await _openFileAsync(_path);
            }
        };

        CloseButton.Click += (_, _) => Close();
    }

    private void ApplyHighlighting() =>
        Editor.SyntaxHighlighting = SyntaxHighlighting.Sql(Editor.ActualThemeVariant);
}
