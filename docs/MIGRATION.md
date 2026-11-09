# Azure Pipelines → GitHub Actions: PR validation migration

**Scope:** the PR-validation pipeline (`case-study-ado-pipeline.yaml`): an x86 .NET 8 / MSBuild (v143) solution, built on Windows, unit-tested with VSTest and coverage.
**Status:** linted (actionlint 1.7.12, zizmor 1.32.0, PowerShell parser 7.6). It has **not yet run on a Windows runner against the real solution**. That happens in Phase 1 below.

---

## 1. What's in the change

| File | Role |
|---|---|
| `.github/workflows/pr-validation.yml` | Entry point. `pull_request` → `main`, skips drafts, cancels superseded runs. Contains no logic; it only calls the reusable workflow. |
| `.github/workflows/_dotnet-windows-ci.yml` | **Reusable workflow** (`workflow_call`). Covers restore, build, test, coverage and artifacts. Every option is an input, so other x86/MSBuild repos can call it. |
| `.github/actions/setup-dotnet-x86/action.yml` | **Composite action**. Installs a side-by-side x86 SDK with a hash-verified installer and signature-checks the binaries. VC++ redist is optional. |
| `.github/workflows/pr-test-report.yml` | `workflow_run` reporter. Turns TRX results into a check run with annotations, and does it without giving PR code a write token. |
| `.github/workflows/workflow-lint.yml` | actionlint + zizmor on any `.github/**` change. Lints the pipeline itself. |
| `.github/dependabot.yml` | Keeps SHA-pinned actions and NuGet packages current, with a 7-day cooldown. |
| `.github/CODEOWNERS` | Platform team must review changes to pipeline, feed config and runsettings. |

Required status check to configure: **`validate / build-test`** (reusable-workflow checks are named `<caller job> / <called job>`).

## 2. Step mapping

| Azure Pipelines | GitHub Actions | Notes |
|---|---|---|
| `trigger: none` + `pr: main`, `drafts: false` | `on.pull_request.branches: [main]`, job `if: !draft`, `ready_for_review` type | `ready_for_review` re-runs when a draft is promoted. ADO did this implicitly. |
| (ADO PR auto-cancel) | `concurrency` per PR number, `cancel-in-progress` | |
| `vmImage: windows-latest` | `runs-on: windows-2022` (input) | **Deliberate pin.** `windows-latest` moved to Windows Server 2025 + **VS 2026** in June 2026. The v143 toolset is native to VS 2022. |
| PowerShell: unpinned `dot.net/v1/dotnet-install.ps1`, x86 runtime + SDK | Composite `setup-dotnet-x86` | Script pinned to a commit and checked by SHA-256. `dotnet.exe` Authenticode-verified. Redundant runtime install dropped (the SDK ships it). |
| PowerShell: VC++ redist from `aka.ms` | Composite input `install-vcredist` (default **off**) | GitHub's Windows 2022 image already has the VC++ 2022 x86 runtime. When enabled, the signature is verified and exit code 1638 ("already newer") is accepted. |
| `NuGetToolInstaller@1` + `NuGetCommand@2 restore` | `NuGet/setup-nuget` (6.x) + `nuget restore -ConfigFile` | Plus `actions/cache` on the global packages folder. Private feed auth is covered in §4. |
| `VSBuild@1` "Build solution" **and** `VSBuild@1` "Publish artifacts" | **One** `msbuild` call with `OutputPath=staging`, `msbuild-architecture: x86` | The solution was being built twice. §3 explains. |
| PowerShell: set `DOTNET_ROOT` | `env:` on the test step only | Same scope as the ADO task's `env:` block. Sets `DOTNET_ROOT`, `DOTNET_ROOT_X86` and `DOTNET_ROOT(x86)`. |
| `VSTest@3` (x86, coverage, runsettings, serial) | `vstest.console.exe` located via `vswhere` | Same assembly filter, `/Platform:x86`, `/Settings`, TRX logger, `/Collect:"Code Coverage;Format=Cobertura"`, no `/Parallel`. |
| ADO Tests / Code Coverage tabs | Check run (`pr-test-report.yml`), job summary, HTML coverage artifact | ReportGenerator merges all Cobertura files. Optional `min-line-coverage` gate. |
| Build output in `$(Build.ArtifactStagingDirectory)`, never published | `build-output-*`, `test-results`, `coverage`, `build-logs` (on failure only) | Short retention (7 days by default, 3 for logs). |

## 3. Defects found in the source pipeline

1. **`/p:PlatformToolset=v1`** in the "Publish artifacts" step is a typo (no such toolset). Native projects either failed or quietly used whatever the second build resolved. Fixed: v143 in a single build.
2. **Double build.** The second VSBuild rebuilt the whole solution just to change `OutputPath`, and the tests ran against that second build. The migrated version builds once into the same flattened staging layout, which keeps test behaviour the same and saves roughly one full build per PR.
3. **Unverified downloads executed as admin** (`dotnet-install.ps1`, `vc_redist`). Fixed with hash or signature checks.
4. **Floating image.** `windows-latest` already moved under this pipeline to VS 2026. Pinned now.
5. **Coverage config ambiguity.** `codeCoverageEnabled: true` (Microsoft collector) was combined with a file called `coverlet.runsettings` (presumably coverlet). Both are still honoured, and ReportGenerator merges whatever Cobertura comes out. Pick one in Phase 1 (§8).
6. **"No tests found" passed silently** (VSTest@3 default). The migrated version fails it (`fail-on-no-tests: true`), so a broken glob can't produce a green PR.

## 4. Security model

**Token permissions (least privilege)**
- Every workflow declares `permissions: {}`, and each job requests only what it needs. The build job gets `contents: read` and nothing else. Caching and artifact upload use the runner's internal token, which needs no scopes.
- `actions/checkout` uses `persist-credentials: false`, so no token sits in `.git/config` while PR code runs.
- Writing results (`checks: write`) happens only in `pr-test-report.yml`. That workflow runs from the default branch's definition, **never checks out or executes PR code**, and parses the TRX artifact as data. This is the documented safe `workflow_run` pattern. The zizmor `dangerous-triggers` finding is suppressed inline with that justification.
- **`pull_request_target` is deliberately not used.**

**Supply chain**
- All third-party actions are pinned to full commit SHAs with a version comment. Dependabot bumps them, and the 7-day cooldown lowers the risk of pulling a compromised release on day zero.
- dotnet-install is pinned to commit `da3ce11` (tag v2026.07.21) and checked by SHA-256. Validation caught a real issue here: the installer repo's `.gitattributes` checks the file out with CRLF, while the raw URL serves LF. The hash has to be taken from the served bytes.
- `dotnet.exe` and `vc_redist` must have a valid Microsoft Authenticode signature before they run.
- Untrusted values (`inputs.*`, secrets) reach scripts **only through `env:`** and are never interpolated into `run:` blocks. This closes off template-injection (zizmor-clean).

**Secrets and private feeds**
- Feed credentials are set as process-scoped `NuGetPackageSourceCredentials_<source>` environment variables inside the restore step only. Nothing is written to `nuget.config`, and the token never reaches the build step or the binlog.
- **Fork PRs receive no secrets.** If `nuget-auth-sources` is set, restore fails fast with a clear message instead of hitting a confusing 401. Options: mirror the packages to a public or upstream-proxy feed, or accept that fork PRs need a maintainer to re-run them from a branch.
- Target state: replace the PAT with **OIDC (Entra workload identity federation) → Azure Artifacts**, giving short-lived tokens and nothing to rotate.

**Artifacts**
- MSBuild binlogs can contain environment and property values, so they upload **only on failure** and are kept for 3 days.

## 5. Governance

- **Branch ruleset on `main`:** required check `validate / build-test`, PR review, code-owner review, no bypass for the CI check. Add `pr-test-report` as informational only, because it never gates.
- **CODEOWNERS** gives the platform team ownership of `.github/`, `nuget.config` and `coverlet.runsettings`. Then a PR can't weaken its own gate (lower coverage, skip tests, add a step) without platform review.
- **Org/enterprise Actions policy:** allow only GitHub-owned, verified and explicitly listed actions (`microsoft/setup-msbuild`, `NuGet/setup-nuget`, `dorny/test-reporter`, `danielpalme/ReportGenerator-GitHub-Action`). Require SHA pinning. Default `GITHUB_TOKEN` to read-only. Require approval to run workflows from first-time or outside contributors.
- **Standardisation:** once proven, move `_dotnet-windows-ci.yml` and the composite action to a central platform repo (e.g. `org/platform-workflows`). Consumers pin it by SHA, and Dependabot proposes upgrades. Inside the central repo, switch the composite reference to GitHub's self-repository syntax (`uses: $/.github/actions/setup-dotnet-x86`), which resolves to the same commit as the reusable workflow. It is kept as `./` here only because actionlint 1.7.12 rejects `$/` and the lint gate would fail. `$/` is github.com only (not GHES) and needs runner ≥ 2.336.
- **Change management:** the workflow changes through PRs like any code. `workflow-lint.yml` runs on every `.github/**` change.

## 6. Migration plan

| Phase | What happens | Exit criteria |
|---|---|---|
| **0. Prep** | Inventory: are there private feeds in `nuget.config`, and what is in `coverlet.runsettings`? Does any test depend on the flattened output layout? Create `NUGET_FEED_TOKEN` (read-only, expiring), the ruleset draft and CODEOWNERS. | Assumptions in §9 answered. |
| **1. Shadow** (1–2 weeks) | Merge the workflows. Both systems run on every PR; **ADO stays the required check**, GitHub is informational. | ≥ 20 PRs with the same pass/fail as ADO, test counts within ±0, coverage within ±0.5 pp, p50 duration ≤ ADO. |
| **2. Cut-over** | Make `validate / build-test` required and remove ADO from required. Keep the ADO pipeline definition, disabled. | One week with no unexplained failures. |
| **3. Decommission** | Delete the ADO pipeline and its service connection, revoke the ADO PAT, archive the YAML. Optionally centralise the reusable workflow (§5). | ADO pipeline removed, secrets revoked. |

## 7. Validation

**Static (done):** actionlint, zizmor (no findings at medium or above), PowerShell parse of every embedded script, solution-glob logic tested against a mock tree, installer hash checked against the live pinned URL.

**Dynamic (Phase 1 checklist):**
- [ ] SDKs/runtimes printed by the composite action include 8.0 x86. `vstest` logs show `testhost.x86`.
- [ ] Same test count and outcomes as the matching ADO run (compare TRX totals).
- [ ] Coverage within ±0.5 pp of ADO. Decide on one collector (§3.5).
- [ ] Native projects show `v143` in the binlog (download it from a deliberately failed run).
- [ ] Negative tests on a throwaway branch: a failing test → red check with annotations; a renamed test project → "No test assemblies" failure; a fork PR → still builds (public feeds) or fails fast (private feeds); a draft PR → skipped.
- [ ] Cache hit on the second run of the same PR; restore time drops.
- [ ] `pr-test-report` posts a check on a fork PR without that PR's code ever getting a write token.

## 8. Rollback

The setup is designed so rollback is a settings change, not a code change.
- **During Phase 1:** nothing to roll back. ADO is still the gate. Disable the workflow (`gh workflow disable "PR validation"`) if it's noisy.
- **After cut-over:** in the ruleset, set the ADO check back as required and remove `validate / build-test`, then re-enable the ADO pipeline. This takes about 5 minutes and needs no PR. Keep the ADO definition and its service connection until Phase 3 for exactly this reason.
- **Bad workflow change later:** revert the PR. If the workflow is centralised, consumers stay pinned to the previous SHA, so a bad release only reaches repos that accept the Dependabot bump.

## 9. Trade-offs and assumptions

| Decision | Chosen | Alternative | Why |
|---|---|---|---|
| Reusable workflow + composite action | Both | Only one | The reusable workflow standardises the whole job and its permissions. The composite action packages the one awkward step (x86 SDK) so it can be reused anywhere, including outside this job. |
| x86 SDK install | Pinned dotnet-install into `Program Files (x86)\dotnet`, no PATH change | `actions/setup-dotnet` with `architecture: x86` | setup-dotnet is maintained and simpler. But it installs to `Program Files\dotnet\x86` and **puts x86 first on PATH and `DOTNET_ROOT` for the rest of the job**, which changes the SDK MSBuild resolves for the build. Keeping the original layout gives parity. Revisit after Phase 1. |
| Test runner | `vstest.console` (VS-bundled) | `dotnet test` | `dotnet test` can't run legacy .NET Framework or mixed native test assemblies the way VSTest@3 did. Parity wins. |
| Output layout | Flattened `OutputPath` (as before) | Per-project bin folders | Flattening can cause "last writer wins" DLL collisions, but the tests were already passing that way. Changing two things at once makes parity failures hard to read. A follow-up can drop `OutputPath`. |
| Runner | `windows-2022`, pinned | `windows-latest` | Determinism and v143 availability, at the cost of a scheduled move to VS 2026 (v145) later. That move is now a deliberate, testable PR. |
| Test reporting | Separate `workflow_run` workflow | Reporter step inside the PR job | In-job reporting needs `checks: write` on a job that runs PR code, and it breaks for forks. The cost is a second workflow and a delay of about 30 seconds. |
| Coverage gate | Off (`0`) | On from day 1 | No existing threshold to match. Set it from the Phase 1 baseline (e.g. baseline − 1 pp) so it doesn't block PRs on the first day. |
| VC++ redist | Off | On (parity) | It's already on the image, and the download can't be hash-pinned. One input flips it back. |
| Private feed auth | PAT secret via env var | OIDC now | OIDC is the target, but it needs Entra setup. A PAT keeps the cut-over independent of that work. |

**Assumptions to confirm in Phase 0:**
1. `nuget.exe` 6.x honours `NuGetPackageSourceCredentials_<name>` for the configured feeds. Source names must be valid environment-variable names.
2. `coverlet.runsettings` doesn't enable parallel execution or an x64-only collector.
3. Every test assembly matches `*Tests.dll`, as in ADO.
4. The solution defines an `x86` solution platform.
