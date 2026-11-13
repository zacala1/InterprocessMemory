# Instructions for agents

[CONTRIBUTING.md](CONTRIBUTING.md) holds the rules. This file lists what an agent must not get wrong.

- **Author.** Before the first commit, check `git config user.name` and `git config user.email`. They must be the
  identity in CONTRIBUTING.md. If they are not, set them for this repository (`git config`, never `--global`).
  Never commit as another identity.
- **No attribution.** Commit messages and pull request descriptions have no `Co-authored-by` line, no
  "Generated with" line, and no tool or model name.
- **Commits.** `<type>(<scope>): <subject>` with a lowercase subject of at most 72 characters. The types are
  `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`.
- **Branches and pull requests.** Work on `<type>/<topic>` (for example `fix/lock-owner`), never on `main`. Open a
  pull request with a Conventional Commit title and a Summary and a Verification section. It is squash-merged.
- **History.** Do not force-push `main`, and do not move or delete the tag of a release. Do not rewrite history
  that is already published.
- **Version.** Change `<Version>` and the assembly and file versions only in a release pull request
  (`chore: release X.Y.Z`).
- **Local files.** `.claude/settings.local.json`, `CLAUDE.local.md` and `.omo/` are per-machine state and are
  ignored. Shared agent files (`AGENTS.md`, `CLAUDE.md`, `.claude/settings.json`, `.claude/commands/`,
  `.claude/skills/`, `.claude/agents/`) are tracked.
- **Build and test.** `dotnet build InterprocessMemory.sln --configuration Release`, then
  `dotnet test InterprocessMemory.Tests/InterprocessMemory.Tests.csproj --configuration Release --no-build --filter "Category!=TimingSensitive&Category!=LongRunning"`.
  Both `net8.0` and `net10.0` runtimes are needed. Check a branch with
  `bash .github/scripts/check-conventions.sh commits origin/main HEAD`.
