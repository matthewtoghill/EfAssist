# EfAssist

A cross-platform desktop GUI over the `dotnet ef` CLI, for managing Entity Framework Core
migrations. Open a solution, pick a `DbContext`, and add, apply, roll back, remove and script
migrations without remembering which project is `--project` and which is `--startup-project`.

The Diagrams tab draws your model as an entity-relationship or class diagram — read from the EF model
snapshot, so it needs no build and no database — and exports it as JSON, SVG, PNG, PDF or Mermaid.
Pick any migration to see the model as of that point in the history, with what it added, removed and
changed marked up against the migration before it.

- `PLAN.md` — the agreed scope and the decisions behind it.
- `PROGRESS.md` — what is actually built, verified, and deliberately not done.
- `ROADMAP.md` — ideas parked with a reason and a trigger to revisit.

## Install

Download `EfAssist-win-Setup.exe` from the
[latest release](https://github.com/matthewtoghill/EfAssist/releases) and run it. It installs
per-user into `%LocalAppData%\EfAssist` — no administrator rights, no .NET runtime needed.

The installer is not code-signed, so SmartScreen will warn on first run.

`EfAssist-win-Portable.zip` is the same application with no installer and no updates.

`EfAssist-win.msi` is a machine-wide installer for deploying via Group Policy or Intune.
It needs administrator rights and installs the app for every user on the machine; updates still
come from the in-app updater.

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
dotnet build EfAssist.slnx
dotnet test EfAssist.slnx
dotnet run --project src/EfAssist.App
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
lives in `<Version>` in `src/EfAssist.App/EfAssist.App.csproj`; `-Version` overrides it.

Keep the contents of `releases/` from the previous release around when building a new one — Velopack
uses them to produce a delta package, so users download a patch rather than the whole application.
