# EfMigrateHub

A cross-platform desktop GUI over the `dotnet ef` CLI, for managing Entity Framework Core
migrations. Open a solution, pick a `DbContext`, and add, apply, roll back, remove and script
migrations without remembering which project is `--project` and which is `--startup-project`.

- `PLAN.md` — the agreed scope and the decisions behind it.
- `PROGRESS.md` — what is actually built, verified, and deliberately not done.
- `ROADMAP.md` — ideas parked with a reason and a trigger to revisit.

## Install

Download `EfMigrateHub-win-Setup.exe` from the
[latest release](https://github.com/matthewtoghill/EfMigrateHub/releases) and run it. It installs
per-user into `%LocalAppData%\EfMigrateHub` — no administrator rights, no .NET runtime needed.

The installer is not code-signed, so SmartScreen will warn on first run.

`EfMigrateHub-win-Portable.zip` is the same application with no installer and no updates.

You still need the `dotnet-ef` tool itself, which is what the app drives:

```
dotnet tool install --global dotnet-ef
```

The app checks for it at startup and shows the command to copy if it is missing.

## Updates

An installed build checks GitHub Releases for a newer version shortly after launch and offers it in
a dismissible banner. There is also a "Check for updates" button on the home page. The check is a read
of the public releases feed, and it fails silently when offline.

## Building

```
dotnet build EfMigrateHub.slnx
dotnet test EfMigrateHub.slnx
dotnet run --project src/EfMigrateHub.App
```

`samples/SampleEfApp` is a throwaway SQLite EF Core project, deliberately outside the solution, used
by the tests and for manual testing.

## Releasing

```powershell
dotnet tool restore
./build/release.ps1                              # build into releases/
./build/release.ps1 -Version 1.1.0 -Upload       # and publish to GitHub Releases
```

`-Upload` needs a GitHub token with `repo` scope, in `-Token` or `GITHUB_TOKEN`. The release number
lives in `<Version>` in `src/EfMigrateHub.App/EfMigrateHub.App.csproj`; `-Version` overrides it.

Keep the contents of `releases/` from the previous release around when building a new one — Velopack
uses them to produce a delta package, so users download a patch rather than the whole application.
