# SampleEfApp

A small EF Core project used for two things:

1. Capturing `dotnet ef` output fixtures for the parser tests in `tests/EfAssist.Core.Tests/Fixtures/`.
2. A manual test target for EfAssist itself — a real solution to open in the GUI.

Deliberately **not** part of `EfAssist.slnx`. Building the app must not build this, and the app under test needs to be a foreign project anyway.

## Shape

- `BlogContext` — SQLite (`blog.db`), two migrations: `InitialCreate` (applied) and `AddBlogUrl` (pending).
- `AuditContext` — SQLite (`audit.db`), no migrations. Exists so `dbcontext list` returns more than one entry and so there's an empty-migration-list case to parse.

The mixed applied/pending state is the point. Don't run `dotnet ef database update` to latest without recreating the pending migration afterwards, or the fixtures stop reflecting reality.

## Resetting to the fixture state

```
rm blog.db
dotnet ef database update InitialCreate --context BlogContext
```

## Note on relative paths

The connection strings are relative (`Data Source=blog.db`), so they resolve against the working directory. `dotnet ef` anchors its working directory to this project's folder, so EF tooling always finds the same file. `dotnet run` from elsewhere will not — run it from this directory.

## SQLitePCLRaw pin

`Microsoft.EntityFrameworkCore.Sqlite` 10.0.10 pulls in `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which carries a high-severity advisory (NU1903 / GHSA-2m69-gcr7-jv3q). `SQLitePCLRaw.bundle_e_sqlite3` is pinned to 3.0.5 here to override it. Builds and runs clean. The shipped app references no database drivers at all, so it never pulls this in.
