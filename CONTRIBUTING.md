# Contributing to KittyClaw

Thanks for helping improve KittyClaw. Contributions of all sizes are welcome.

## Before you start

- Search the existing issues and pull requests before opening a new one.
- Use an issue to discuss substantial behavior or architecture changes before investing in an implementation.
- Keep each pull request focused on one problem.

## Development setup

Install the .NET 10 SDK, clone the repository, then start the application from the repository root:

```text
run.bat        (Windows)
./run.sh       (macOS / Linux)
```

The application is served at `http://localhost:5230`.

## Validate your change

Run the same build and test commands used by continuous integration:

```text
dotnet build KittyClaw.Web -c Release --nologo
dotnet build KittyClaw.ClaudeMock -c Release --nologo
dotnet build KittyClaw.Core.Tests -c Release --nologo
dotnet test KittyClaw.Core.Tests -c Release --no-build --nologo --logger "console;verbosity=minimal"
```

Add or update tests when behavior changes. For user-interface changes, include a screenshot or short recording in the pull request when it helps reviewers understand the result.

## Pull requests

- Explain the problem and the chosen solution.
- Link the relevant issue with `Closes #123` when applicable.
- Describe how the change was verified.
- Update documentation when public behavior, configuration, or workflows change.
- Do not include unrelated formatting or generated-file changes.

By contributing, you agree that your contribution is licensed under the repository's [AGPL-3.0-or-later license](LICENSE) and its [additional terms](NOTICE.md).

All contributors must follow the [Code of Conduct](CODE_OF_CONDUCT.md).
