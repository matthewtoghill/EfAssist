using EfMigrateHub.Core;

namespace EfMigrateHub.App.ViewModels;

/// <summary>
/// One row of the migrations list: a migration plus its position in EF's chronological order.
/// </summary>
/// <param name="Index">
/// 1-based position in the order migrations are applied. Deliberately fixed to the chronological
/// order, not the display order, so reversing the sort renumbers nothing — the first migration is
/// always 1 whether it is at the top or the bottom.
/// </param>
public sealed record MigrationRow(int Index, MigrationInfo Info)
{
    public string Id => Info.Id;

    public string Name => Info.Name;

    public MigrationState State => Info.State;
}
