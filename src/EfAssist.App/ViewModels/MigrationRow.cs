using EfAssist.Core;

namespace EfAssist.App.ViewModels;

/// <summary>
/// One row of the migrations list: a migration plus its position in EF's chronological order.
/// </summary>
/// <param name="Index">
/// 1-based position in the order migrations are applied. Deliberately fixed to the chronological
/// order, not the display order, so reversing the sort renumbers nothing — the first migration is
/// always 1 whether it is at the top or the bottom.
/// </param>
/// <param name="IsDatabaseHead">
/// This is the newest applied migration — where the database actually is. Drawn as a marker between
/// the rows, so applied and pending are read as a position in the list rather than by comparing
/// badges. False for every row when applied state is unknown, which is what Offline leaves it as:
/// the app cannot point at a head it did not ask the database about.
/// </param>
public sealed record MigrationRow(int Index, MigrationInfo Info, bool IsDatabaseHead = false)
{
    public string Id => Info.Id;

    public string Name => Info.Name;

    public MigrationState State => Info.State;
}
