# hello-ado-to-gha

A sample repository that shows a complete Azure DevOps → GitHub Actions migration for PR validation, plus the Claude skill that repeats it across many pipelines.

- **App:** a .NET 8 hello-world console app with xUnit tests, built as **x86**. That matches the case-study pipeline.
- **Before:** `azure-pipelines.yml`, the original ADO pipeline. It stays unchanged during the shadow period.
- **After:** `.github/`. This contains a thin PR workflow, a reusable workflow (the golden path), a composite action, a privileged-but-safe test reporter, and workflow linting.
- **Platform contract:** `platform/catalog.yaml` lists archetypes, approved actions with pinned SHAs, and runner labels.
- **Automation:** `.claude/skills/ado-to-gha-migration/` holds the skill that migrates one ADO pipeline per run onto the catalog.

## Layout

```
.
├── HelloWorld.sln                      # Debug/Release × Any CPU/x86 (CI builds Release|x86)
├── Directory.Build.props               # net8.0, nullable, warnings-as-errors, x86 → PlatformTarget + X86_BUILD
├── global.json                         # SDK 8.0.100, rollForward latestMajor
├── nuget.config                        # nuget.org only (add private feeds here)
├── coverlet.runsettings                # team-owned test settings (serial, coverlet Cobertura)
├── build.ps1                           # local loop: ./build.ps1 [-Platform x86]
├── src/HelloWorld/                     # Greeter + Program
├── tests/HelloWorld.Tests/             # xUnit tests, incl. an x86-only parity test
├── azure-pipelines.yml                 # BEFORE (ADO)
├── .github/
│   ├── workflows/
│   │   ├── pr-validation.yml           # AFTER: trigger + concurrency + call to the golden path
│   │   ├── _dotnet-windows-ci.yml      # reusable workflow (platform-owned)
│   │   ├── pr-test-report.yml          # workflow_run → check run with annotations
│   │   └── workflow-lint.yml           # actionlint + zizmor on .github/** changes
│   ├── actions/setup-dotnet-x86/       # composite action: verified x86 SDK install
│   ├── dependabot.yml                  # SHA-pinned actions + NuGet, 7-day cooldown
│   └── CODEOWNERS                      # platform review for pipeline/feed/runsettings
├── platform/catalog.yaml               # golden-path catalog the skill reads
├── .claude/skills/ado-to-gha-migration/SKILL.md
└── docs/
    ├── MIGRATION.md                    # security, governance, migration, validation, rollback, trade-offs
    └── examples/migration-report.json  # what the skill emits for this repo
```

## Run it locally

```powershell
./build.ps1                 # AnyCPU — any OS with the .NET 8 SDK
./build.ps1 -Platform x86   # x86 — Windows with an x86 .NET 8 runtime
dotnet run --project src/HelloWorld -- Ada
# Hello, Ada!
# Running as X64 on .NET 8.x
```

The x86 build includes `X86_build_runs_in_a_32_bit_process`, which is compiled only when `Platform=x86`. The test proves that CI really ran the 32-bit test host, the same guarantee the ADO `VSTest@3 platform: x86` step gave. CI runs 7 tests on x86; AnyCPU runs 6.

## What runs on a pull request

1. **`pr-validation.yml`** runs on PRs to `main`. It skips drafts, cancels superseded runs, and grants `permissions: {}` by default.
2. It calls **`_dotnet-windows-ci.yml`**, which runs the `build-test` job on `windows-2022` with `contents: read`:
   - set up the x86 SDK, MSBuild (x86) and NuGet, with a NuGet cache
   - `nuget restore`
   - one `msbuild` (Release|x86, v143) into a flattened staging folder
   - `vstest.console /Platform:x86` with TRX output and coverage
   - ReportGenerator, then the job summary and an optional coverage gate
   - upload artifacts
3. **`pr-test-report.yml`** (`workflow_run`, `checks: write`) turns the `test-results` artifact into a check with per-test annotations. It never runs PR code.

Required status check for the `main` ruleset: **`validate / build-test`**.

## Using the migration skill

Claude Code loads skills from `.claude/skills/` automatically. To migrate another repo's pipeline, open that repo with the skill available (copy the folder or install it at user or org level), make sure the catalog is reachable, and ask:

> Migrate azure-pipelines.yml to GitHub Actions using the ado-to-gha-migration skill.

Each run produces a branch `migrate/ado-to-gha/<pipeline-id>`, a thin caller workflow, a PR description, and `migration-report.json`. Its status is one of `ready`, `ready-with-flags`, `needs-human` or `off-path`. `docs/examples/migration-report.json` is the report for this repo's own migration, so use it as the expected output when you evaluate the skill.

At programme scale (hundreds of pipelines):

1. Use GitHub Actions Importer `audit`/`forecast` to inventory pipelines and group them into archetypes.
2. The platform team publishes each archetype in the catalog, in the central platform repo.
3. Run the skill once per repo, in batch.
4. A shadow-comparison bot runs ADO and GitHub Actions side by side and checks parity.
5. The reports feed the migration dashboard.

See `docs/MIGRATION.md`.

## Moving the platform pieces out of this repo

This sample keeps everything in one repo so it is self-contained. In production:

- `_dotnet-windows-ci.yml`, `setup-dotnet-x86` and `catalog.yaml` move to `org/ci-platform`. They are released with semver and consumed as `org/ci-platform/.github/workflows/dotnet-windows.yml@<sha> # vX.Y.Z`.
- Set `platform_repo` and each archetype's `sha` in the catalog. The skill then generates SHA-pinned references instead of `./`.
- `dependabot.yml`, CODEOWNERS for `.github/`, rulesets and the actions allow-list become org-level settings.
