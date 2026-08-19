using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;
using EfMigrateHub.Core;

namespace EfMigrateHub.App;

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
}
