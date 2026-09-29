# EfAssist

[![CI](https://github.com/matthewtoghill/EfAssist/actions/workflows/ci.yml/badge.svg)](https://github.com/matthewtoghill/EfAssist/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/matthewtoghill/EfAssist)](https://github.com/matthewtoghill/EfAssist/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A desktop GUI over the `dotnet ef` CLI, for managing Entity Framework Core migrations. Open a
solution, pick a `DbContext`, and add, apply, roll back, remove and script migrations without
needing to remember which project is `--project` and which is `--startup-project`.

**[EfAssist website](https://matthewtoghill.github.io/EfAssist/)** · **[Download the latest release](https://github.com/matthewtoghill/EfAssist/releases/latest)**

![The Migrations tab](docs/assets/migrations.png)

## What it does

- **Migrations.** Every migration with its applied or pending state, and what its `Up` and `Down`
  change, in source or as the SQL either direction would run. Add, apply, roll back to any point,
  remove, drop.
- **Script.** `migrations script` between any two migrations, idempotent or not, in a
  syntax-highlighted viewer with Copy and Save As.
- **Diagrams.** Your model drawn as an entity-relationship or class diagram, read from the EF model
  snapshot, so it needs no build and no database. Filter a large model down to the entities you
  pick, then widen it one relationship at a time. Exports to JSON, SVG, PNG, PDF or Mermaid, and
  exports follow the filter. Pick any migration to see the model as it stood then, with what that
  migration added, removed and changed marked against the one before it.
- **Tools.** Ask EF whether the model has changes not yet in a migration, see what this workspace is
  pointed at, build a migrations bundle for a machine with no SDK and no source, and reach the
  whole-database actions.
- **Plain-language errors.** The common EF failures explained, with the raw output still one click
  away. A "Copy diagnostics" button puts the command line, exit code, full output and tool versions
  on your clipboard.

Every command runs the real `dotnet ef` and streams its output live, cancellable. Nothing is hidden
behind the GUI, and nothing about your database is stored. EfAssist ships no database drivers at
all.

## Install

Download `EfAssist-win-Setup.exe` from the
[latest release](https://github.com/matthewtoghill/EfAssist/releases/latest) and run it. It installs
per-user into `%LocalAppData%\EfAssist`, with no administrator rights and no .NET runtime needed.

The installer is not code-signed, so SmartScreen will warn on first run.

`EfAssist-win-Portable.zip` is the same application with no installer and no updates.

`EfAssist-win.msi` is a machine-wide or single-user installer for deploying via Group Policy or
Intune. It needs administrator rights for a machine-wide installation. Updates still come from the
in-app updater.

You still need the `dotnet-ef` tool itself, which is what the app drives:

```
dotnet tool install --global dotnet-ef
```

The app checks for it at startup and shows the command to copy if it is missing.

Windows x64 is the only published target today. The codebase is cross-platform Avalonia. macOS and
Linux builds are parked in [`docs/dev/ROADMAP.md`](docs/dev/ROADMAP.md) rather than ruled out.

## Updates

An installed build checks GitHub Releases for a newer version shortly after launch and offers it in
a dismissible banner. There is also a "Check for updates" button on the home page. The check is a
read of the public releases feed, and it fails silently when offline.

## Building

You need the .NET 10 SDK and the `dotnet-ef` global tool.

```
dotnet build EfAssist.slnx
dotnet test EfAssist.slnx
dotnet run --project src/EfAssist.App
```

`samples/SampleEfApp` and `samples/SampleRichModel` are throwaway EF Core projects, deliberately
outside the solution, used by the tests and for manual testing. The tests drive the real CLI against
`SampleEfApp`, so they need `blog.db` in its fixture state. See that project's `README.md`.

See [`CONTRIBUTING.md`](CONTRIBUTING.md) if you want to change something.

## Development record

`docs/dev/` is the working record, kept as it was written during development:

- [`PLAN.md`](docs/dev/PLAN.md) holds the agreed scope and the decisions behind it.
- [`PROGRESS.md`](docs/dev/PROGRESS.md) lists what is built, verified, and deliberately not done.
- [`ROADMAP.md`](docs/dev/ROADMAP.md) parks ideas with a reason and a trigger to revisit.

## License

[MIT](LICENSE).
