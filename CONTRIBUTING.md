# Contributing

## Getting set up

You need Docker and the .NET 11 SDK pinned in `global.json`. If it is not installed system-wide, put it in
`.dotnet/` without touching anything else:

```bash
./install-dotnet.sh        # or ./install-dotnet.ps1 on Windows
```

`global.json` looks in `.dotnet/` first, so `dotnet` then resolves to that SDK from the repository root.

```bash
dotnet build Viatica.slnx
dotnet run --project tests/Viatica.ArchitectureTests
```

Test projects are executables on Microsoft.Testing.Platform, so they run with `dotnet run --project`, not
`dotnet test`. Projects ending in `.UnitTests` need nothing else. Projects ending in `.IntegrationTests` start
real Postgres, Kafka, OpenSearch and Valkey containers through Testcontainers and need Docker running.

For the whole local stack, see [docs/operations.md](docs/operations.md):

```bash
docker compose up -d --wait
bash deploy/check-stack.sh
```

## Gates

CI runs these on every pull request, and a pull request merges only when all of them pass. Run them before you
push:

1. `dotnet build Viatica.slnx` with zero warnings. `TreatWarningsAsErrors` is on for every project.
2. Unit tests, integration tests, and `dotnet run --project tests/Viatica.ArchitectureTests`.
3. `dotnet format whitespace Viatica.slnx --verify-no-changes` and
   `dotnet format style Viatica.slnx --verify-no-changes`.
4. The licence audit: `dotnet run --project tools/Viatica.LicenseAudit -- .`. It fails on any package, at any
   depth, that is not permissively licensed. If it fails, replace or downgrade the package; do not add an
   exception.
5. The compose job: the stack in `compose.yaml` comes up healthy and `deploy/check-stack.sh` passes.

## Ground rules

**`docs/requirements.md` is the contract,** and `docs/design.md` holds the event catalogue and the invariants. A
change that moves ownership, adds an event or changes a cross-service rule starts there, usually with an ADR in
`docs/adr/`.

**A service never reads another service's database.** Each one has its own Postgres role that cannot connect to
any other database, and the architecture tests check that no service references another service's projects.

**A change to a guarantee comes with the test that would fail without it.** No overselling, all-or-nothing
trips and the search deadline are the point of the project.

**Packages are centrally managed.** Versions live in `Directory.Packages.props` and nowhere else.

## Branches and pull requests

- Every change is a branch and a pull request against `main`; nobody pushes to `main` directly.
- One branch per issue, named after its milestone and topic (`m1/stays-catalogue`). The pull request body starts
  with `Closes #N.` and says what changed and why.
- Conventional commits (`feat`, `fix`, `docs`, `test`, `build`, `ci`, `refactor`), one finished unit per commit,
  each passing the gates on its own.
- A pull request merges once CI is green and it has been reviewed. Keep it to one issue; a second concern is a
  second pull request.
- Add a line under `Unreleased` in [CHANGELOG.md](CHANGELOG.md) for anything a user of the system would notice.
