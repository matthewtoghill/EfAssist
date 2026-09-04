# Contributing to EfAssist

Thanks for taking a look. EfAssist is a desktop GUI over the `dotnet ef` CLI, so almost every
change is either "make the app easier to drive" or "explain an EF failure better".

## Getting set up

You need the .NET 10 SDK and the `dotnet-ef` global tool — the app shells out to it, and so do the
tests:

```
dotnet tool install --global dotnet-ef
```

Then:

```
dotnet build EfAssist.slnx
dotnet test EfAssist.slnx
dotnet run --project src/EfAssist.App
```

`samples/SampleEfApp` and `samples/SampleRichModel` are throwaway EF Core projects, deliberately
outside the solution. The tests build and migrate `SampleEfApp` for real, so a first `dotnet test`
takes a minute or so. Each sample has a `README.md` explaining what it is there to prove; read it
before changing one, because several tests assert on its exact migration list.

## Before you open a pull request

- `dotnet build EfAssist.slnx` — no new warnings.
- `dotnet test EfAssist.slnx` — all green. Add tests for what you changed; `EfAssist.Core` is
  covered by unit tests and by fixtures captured from the real CLI, in
  `tests/EfAssist.Core.Tests/Fixtures/`.
- If your change touches how `dotnet ef` output is parsed, add a fixture rather than a hand-written
  string. The fixtures are the only defence against EF changing its output format.
- Anything that changes the UI wants a manual pass as well as tests. Say in the pull request what
  you clicked.

## How the code is laid out

| Project | What lives there |
| --- | --- |
| `src/EfAssist.Core` | Everything with no UI in it: process launching, `dotnet ef` output parsing, EF error diagnostics, model-snapshot parsing, diagram layout, settings. |
| `src/EfAssist.App` | Avalonia views and the view models. |
| `tests/EfAssist.Core.Tests` | Tests for both of the above — the shell view models hold real logic and touch no Avalonia type, so they are tested here rather than in a second project. |

`docs/dev/` holds the development record: `PLAN.md` for the agreed scope and the reasoning behind
it, `PROGRESS.md` for what is built and verified, `ROADMAP.md` for ideas parked with a reason and a
trigger to revisit. If you are about to propose something big, check `ROADMAP.md` first — it may
already be there with an explanation of why it is waiting.

## Conventions

- Commits: `type: summary` — `feat`, `fix`, `docs`, `refactor`, `test`, `chore`.
- Nullable reference types are on everywhere. Keep them on.
- Comments explain *why*, not what. The codebase is fairly heavily commented where a decision would
  otherwise look arbitrary; match that where you are recording a decision, and skip it where the
  code already says it.

## Bugs and ideas

Open an issue. For a bug, the app's **Copy diagnostics** button puts the command line, working
directory, exit code, full output and the tool and SDK versions on your clipboard — paste that in.
It saves a round trip.
