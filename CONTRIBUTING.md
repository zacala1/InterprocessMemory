# Contributing

These rules apply to everyone who changes this repository, people and tools alike. Where a machine can check a
rule, CI does (the [Conventions](.github/workflows/conventions.yml) workflow); the rest is up to the author.

## Author

This repository has one author. Every commit has this author and this committer:

```
zacala1 <73224434+zacala1@users.noreply.github.com>
```

Set it for the repository before the first commit, not globally:

```shell
git config user.name "zacala1"
git config user.email "73224434+zacala1@users.noreply.github.com"
```

Commit messages and pull request descriptions carry no `Co-authored-by` line, no "Generated with" line, and no tool
or model name.

## Commits

A commit is one logical change that builds. Its message is a Conventional Commit:

```
<type>(<scope>): <subject>

<body: why the change is made>
```

| Type       | Use it for                                                         |
| ---------- | ------------------------------------------------------------------ |
| `feat`     | a new public type or member, or new behavior                       |
| `fix`      | a bug fix                                                          |
| `perf`     | a change that makes something faster or smaller                    |
| `refactor` | a change that alters neither behavior nor API                      |
| `docs`     | README, comments, and this file                                    |
| `test`     | tests only                                                         |
| `build`    | the projects, packaging, dependencies                              |
| `ci`       | workflows and the scripts they run                                 |
| `chore`    | anything else, including `chore: release X.Y.Z`                    |
| `revert`   | undoing an earlier commit                                          |

The scope is optional (`fix(queue): release the slot on dispose`). The subject is lowercase, in the imperative,
without a final period, and the whole first line has at most 72 characters. A `!` after the type (`feat!: ...`) and
a `BREAKING CHANGE:` paragraph mark a change that breaks the API or the region format.

## Branches and pull requests

- Work on a branch named `<type>/<topic>` with the types above, for example `feat/ack-queue` or `fix/lock-owner`.
  Never commit to `main`.
- Every change is a pull request. Its title is a Conventional Commit line, because the pull request is
  squash-merged and the title becomes the commit message on `main` (GitHub adds the number:
  `feat: add ack queues (#12)`). Its description has two sections: Summary (what changed and why) and Verification
  (how it was checked, and what was not).
- One pull request is one change. There is no changelog file: the title of the pull request is the line that
  GitHub lists in the notes of the release, so write it for a reader of those notes.
- `main` has a linear history, and the head branch is deleted when the pull request is merged. Do not force-push
  `main`, and do not move or delete the tag of a release.

Run the checks of a pull request before you open it:

```shell
bash .github/scripts/check-conventions.sh commits origin/main HEAD
dotnet build InterprocessMemory.sln --configuration Release
dotnet test InterprocessMemory.Tests/InterprocessMemory.Tests.csproj --configuration Release --no-build --filter "Category!=TimingSensitive&Category!=LongRunning"
```

## Versions and releases

Versions follow SemVer. While the major version is 0, a new minor version may break the API and the shared-memory
format, and a patch version only fixes. The version lives in `InterprocessMemory/InterprocessMemory.csproj`:
`<Version>` is `X.Y.Z` (or `X.Y.Z-suffix`), and `<AssemblyVersion>` and `<FileVersion>` are `X.Y.Z.0`. They change
together and only in a release pull request. The region format number (version 3) is not the package version.

To release a version:

1. Open a pull request `chore: release X.Y.Z`. It sets the three version fields and `PackageReleaseNotes` in the
   project file. `PackageReleaseNotes` is the text at the top of the release notes (what the version is about, and
   any warning about compatibility), and it is also the release notes of the NuGet package.
2. After it is merged, tag the merge commit on `main` and push the tag:

   ```shell
   git tag vX.Y.Z <commit>
   git push origin vX.Y.Z
   ```

   The [Release](.github/workflows/release.yml) workflow checks that the tag and the project agree and that
   `PackageReleaseNotes` was rewritten since the previous release, builds, runs the tests of both frameworks, packs
   the library, and creates a GitHub release with the packages. Its notes are `PackageReleaseNotes` followed by the
   list of the pull requests merged since the previous release, which GitHub generates from their titles. When a
   check fails nothing is released: delete the tag, fix the cause in a pull request, and tag again.
3. Publishing to NuGet.org is off. To turn it on, set the repository variable `NUGET_PUBLISH` to `true` and store
   the API key as the secret `NUGET_API_KEY` of the `nuget` environment. The Release workflow then pushes the
   package as well.

## Automation

| Workflow    | When                          | What it does                                                          |
| ----------- | ----------------------------- | --------------------------------------------------------------------- |
| CI          | pull request, push to `main`  | builds, tests both frameworks on Linux and Windows, packs once        |
| Conventions | pull request                  | title, branch name, description, and author and message of each commit |
| Release     | tag `vX.Y.Z`                  | see Versions and releases                                             |

The repository settings that go with these rules: only squash merging (the title and the description of the pull
request become the commit message), head branches are deleted after the merge, and a ruleset on `main` that
forbids deleting it and force-pushing it, requires a linear history, and requires a pull request whose checks (CI
and Conventions) passed. The owner of the repository can bypass the ruleset.
