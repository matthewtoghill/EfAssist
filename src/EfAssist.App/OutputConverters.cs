using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;
using EfAssist.App.ViewModels;
using EfAssist.Core;

namespace EfAssist.App;

/// <summary>
/// Small bindings for the output console and the migrations list. <see cref="FuncValueConverter{TIn,TOut}"/>
/// keeps these to one line each instead of an <c>IValueConverter</c> class apiece.
/// </summary>
/// <remarks>
/// The colour-producing converters that used to live here are gone on purpose. A converter returning
/// a <see cref="SolidColorBrush"/> bakes one theme variant into the visual tree and does not re-run
/// when the theme changes, so switching light to dark left stale colours behind. These converters now
/// only answer "which case is this?" as a bool; the colour comes from a style class bound to a
/// <c>DynamicResource</c>, which does follow the theme.
/// </remarks>
public static class OutputConverters
{
    /// <summary>
    /// An enum member's name with spaces at the case boundaries, so <c>HighContrast</c> reads as
    /// "High contrast" in a dropdown without a parallel list of display strings to keep in step.
    /// </summary>
    public static readonly FuncValueConverter<object?, string> SpacedName = new(Spaced);

    private static string Spaced(object? value)
    {
        var name = value?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        var split = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");

        // Sentence case, so a two-word member does not read as a proper noun halfway through.
        return char.ToUpperInvariant(split[0]) + split[1..].ToLowerInvariant();
    }

    /// <summary>
    /// A colour as a brush, for the settings screen's theme preview. This is the one place a
    /// brush-producing converter is right rather than wrong: the tile has to show the palette being
    /// edited, not the theme the window is painted with, and it re-runs whenever that palette changes.
    /// </summary>
    public static readonly FuncValueConverter<Color, IBrush> Brush =
        new(colour => new SolidColorBrush(colour));

    public static readonly FuncValueConverter<bool, TextWrapping> Wrapping =
        new(wrap => wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);

    /// <summary>Sideways scrolling is pointless once lines wrap, and the bar just wastes height.</summary>
    public static readonly FuncValueConverter<bool, ScrollBarVisibility> HorizontalScrollBar =
        new(wrap => wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

    public static readonly FuncValueConverter<OutputChannel, bool> IsErrorChannel =
        new(channel => channel == OutputChannel.Error);

    public static readonly FuncValueConverter<OutputChannel, bool> IsWarnChannel =
        new(channel => channel == OutputChannel.Warn);

    public static readonly FuncValueConverter<OutputChannel, bool> IsInfoChannel =
        new(channel => channel == OutputChannel.Info);

    public static readonly FuncValueConverter<MigrationState, bool> IsApplied =
        new(state => state == MigrationState.Applied);

    public static readonly FuncValueConverter<MigrationState, bool> IsPending =
        new(state => state == MigrationState.Pending);

    /// <summary>
    /// Unknown deliberately does not borrow Pending's colour. It means "we did not ask the database",
    /// which is a different thing from "not applied", and the badge must not blur the two.
    /// </summary>
    public static readonly FuncValueConverter<MigrationState, bool> IsUnknownState =
        new(state => state is not (MigrationState.Applied or MigrationState.Pending));

    public static readonly FuncValueConverter<MigrationState, string> StateLabel = new(state => state switch
    {
        MigrationState.Applied => "Applied",
        MigrationState.Pending => "Pending",
        _ => "Unknown",
    });

    public static readonly FuncValueConverter<ModelCheckState, bool> IsModelUpToDate =
        new(state => state == ModelCheckState.UpToDate);

    public static readonly FuncValueConverter<ModelCheckState, bool> IsModelPending =
        new(state => state == ModelCheckState.Pending);

    public static readonly FuncValueConverter<ModelCheckState, bool> IsModelUnknown =
        new(state => state == ModelCheckState.Unknown);

    public static readonly FuncValueConverter<ModelCheckState, string> ModelCheckLabel = new(state => state switch
    {
        ModelCheckState.UpToDate => "Up to date",
        ModelCheckState.Pending => "Pending changes",
        _ => "Not checked",
    });
}
