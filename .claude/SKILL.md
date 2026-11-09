---
name: ado-to-gha-migration
description: Migrate an Azure DevOps YAML pipeline to GitHub Actions on the org's golden path: inventory, classify archetype, map, generate a thin caller, lint, and emit a PR body plus a JSON migration report.
---

# Azure DevOps -> GitHub Actions pipeline migration

Migrates ONE Azure DevOps (ADO) YAML pipeline per run onto the organisation's CI golden path
(versioned reusable workflows + composite actions in the platform repo). Designed to run
unattended in batch across hundreds of repos, so every run must be deterministic, additive,
and end with a machine-readable report.

The skill is the judgment layer. GitHub Actions Importer (audit / forecast / dry-run) is the
inventory layer; the platform repo is the template layer. Do not re-invent either.

Worked example in this repo: `azure-pipelines.yml` (before) -> `.github/workflows/pr-validation.yml`
(after), with the decisions recorded in `docs/MIGRATION.md`. Use it as the reference output.

## Configuration (read, never guess)

- `PLATFORM_CATALOG` - `platform/catalog.yaml` in this repo. In production it lives in the
  central platform repo (e.g. `org/ci-platform/catalog.yaml`); the schema is the same.
- The catalog lists archetypes (reusable workflow path, version, SHA, required inputs and where
  each input comes from in ADO), approved external actions with SHAs, and curated runner labels.
- `platform_repo: local` / `sha: local` means the platform workflow lives in the same repo:
  reference it with `./<path>` and no SHA. Otherwise use `<platform_repo>/<path>@<sha> # v<version>`.
- If the catalog is unavailable or does not parse, stop with status `needs-human`
  (reason `catalog-missing`).

## Hard rules

1. Never invent action names, inputs, versions or commit SHAs. Take them from the catalog. If
   something is not in the catalog, resolve it from the action's own repo (`git ls-remote <repo>
   refs/tags/<tag>` plus the peeled `refs/tags/<tag>^{}` for annotated tags), read its
   `action.yml` for inputs, and mark it `not-in-catalog` in the report for platform review.
2. Prefer the golden path: the output is a thin caller of a catalog reusable workflow.
   Hand-written steps only when no archetype fits - and then mark the report `off-path`.
3. Every workflow starts with `permissions: {}`; each job requests the minimum it needs.
4. Never use `pull_request_target` with a checkout of PR code. Never interpolate `${{ }}` into
   `run:` scripts - pass values through `env:`. `actions/checkout` uses `persist-credentials: false`.
5. Never copy secret values. List required secrets/variables by name; prefer OIDC over PATs.
6. Additive only: do not delete or edit the ADO pipeline, other workflows, or unrelated files.
   Migration runs in shadow mode until a human flips the required check.
7. Preserve behaviour. Fix only unambiguous defects, and list every fix and every
   behavioural difference in the report. If parity is uncertain, keep parity and flag it.
8. Use the catalog's runner labels, never `*-latest`.
9. Idempotent: if branch `migrate/ado-to-gha/<pipeline-id>` exists, update it in place.

## Workflow

### 1. Intake
- Locate the pipeline file(s) (`azure-pipelines*.yml`, `.azure/**`, or the path given).
- Resolve every `template:` / `extends:` reference (local and `resources.repositories`).
- If an Importer audit or dry-run exists for this pipeline, read it; treat it as a draft, not truth.
- Inventory: triggers, pr, schedules, variables, variable groups, pools/agents, stages, jobs,
  steps/tasks, conditions, matrices, service connections, environments, approvals, artifacts.

### 2. Classify
- Match against catalog archetypes using their `match_hints`. Record confidence
  (high/medium/low) and the evidence.
- Low confidence or no match -> continue but mark `off-path`; never force a bad fit.
- Anything that deploys (deployment jobs, environments, approvals, release stages) is out of
  scope for automatic migration: generate the CI part only and mark `needs-human` for deploy.

### 3. Map
Use the archetype's `inputs` (each says which ADO value it comes from) and the reference table
below. Values equal to the catalog default may still be written explicitly for readability.
Anything without a mapping goes to `unmapped[]` in the report.

### 4. Detect defects and smells
Check and report (fix only if unambiguous):
- downloads executed without hash/signature check (`Invoke-WebRequest`, `curl | sh`)
- floating images (`*-latest`) or floating tool versions
- duplicated build steps / rebuilding to change output paths
- typos or invalid values in MSBuild properties (e.g. `PlatformToolset=v1`)
- `continueOnError: true` hiding failures; tests that pass when zero tests run
- secrets in plain `variables:`; PATs where OIDC is possible
- environment variables set globally that should be step-scoped

### 5. Generate
- `.github/workflows/<pipeline-name>.yml`: trigger + concurrency + one job calling the catalog
  reusable workflow (rules in Configuration), `secrets:` passed explicitly.
- If inline scripts hold real build logic and the archetype expects a build script, extract to
  `build.ps1` / `build.sh` unchanged in behaviour; otherwise leave scripts alone.
- Comment every external `uses:` with a card: Source / Type / Does / Access / Notes.
- Do not add Dependabot, CODEOWNERS or rulesets per repo - those are platform-level.

### 6. Validate (must pass before reporting success)
- `actionlint` on all changed workflows (needs a git repo; `git init` in a scratch copy if not).
- `zizmor --min-severity medium .github/`; any inline suppression needs a written reason.
- Parse every embedded PowerShell (PowerShell parser) and shell (`bash -n`, shellcheck if present).
- Re-read the generated caller against the catalog: every required input present, ref matches.
- On failure: fix and re-run. After 3 failed attempts -> `needs-human` with the lint output.

### 7. Output
1. The changed files on the branch.
2. PR description using the template below.
3. `migration-report.json` at the repo root of the branch (schema below) - this feeds the
   programme dashboard, so keep field names exact.

## Stop and escalate (`needs-human`)
Classic (UI) build or release pipelines; marketplace/unknown tasks with no catalog mapping;
self-hosted agent pools with no matching runner label; deployment stages, environments or
approvals; service connections with no OIDC mapping; variable groups linked to Key Vault
where the vault/identity is unknown; `resources.pipelines` triggers between pipelines;
catalog missing or a ref unresolvable. Always still produce the report.

## Reference: construct mapping

| Azure DevOps | GitHub Actions |
|---|---|
| `trigger: none` | no `push` trigger |
| `trigger.branches/paths` | `on.push.branches/paths` |
| `pr.branches` | `on.pull_request.branches` |
| `pr.drafts: false` | `types: [opened, synchronize, reopened, ready_for_review]` + job `if: !github.event.pull_request.draft` |
| PR `autoCancel` (default) | `concurrency: { group: <wf>-${{ github.event.pull_request.number }}, cancel-in-progress: true }` |
| `schedules[].cron` | `on.schedule[].cron` (both UTC). `always: false` has no equivalent - flag |
| `variables` (plain) | `env:` at workflow/job/step scope (narrowest that works) |
| variable groups / secret vars | org/repo/environment variables + secrets; Key Vault -> `azure/login` (OIDC) + `az keyvault` |
| `pool.vmImage` | `runs-on:` catalog runner label |
| `pool.name` (self-hosted) | `runs-on:` runner group label from catalog |
| stages / jobs / `dependsOn` | jobs + `needs:` |
| `condition: succeeded()` / `failed()` / `always()` | `if: success()` / `failure()` / `!cancelled()` (avoid `always()`) |
| `eq(variables['Build.Reason'], 'PullRequest')` | `github.event_name == 'pull_request'` |
| `strategy.matrix` / `parallel` | `strategy.matrix` |
| `template:` steps/jobs | composite action / reusable workflow (catalog first) |
| `extends:` (mandated template) | org ruleset 'require workflows to pass' - platform-level, note only |
| `checkout: self` | `actions/checkout` + `persist-credentials: false` (map fetchDepth, submodules, lfs) |
| `UseDotNet@2` | `actions/setup-dotnet` (x86: catalog composite) |
| `NuGetToolInstaller@1` | `NuGet/setup-nuget` |
| `NuGetCommand@2 restore` | `nuget restore -ConfigFile` (+ `actions/cache`) |
| `DotNetCoreCLI@2` | `dotnet <cmd>` in `run:` |
| `VSBuild@1` / `MSBuild@1` | `microsoft/setup-msbuild` (+ `msbuild-architecture`) + `msbuild` |
| `VSTest@2/3` | `vstest.console.exe` found via vswhere; TRX logger; `/Platform`, `/Settings`, `/Collect` |
| `PublishTestResults@2` | upload TRX/JUnit artifact + `workflow_run` reporter (no write token in PR job) |
| `PublishCodeCoverageResults@1/2` | ReportGenerator -> job summary + artifact (+ optional gate) |
| `PublishPipelineArtifact` / `PublishBuildArtifacts` | `actions/upload-artifact` (unique names, short PR retention) |
| `DownloadPipelineArtifact` | `actions/download-artifact` |
| `Cache@2` | `actions/cache` |
| `AzureCLI@2` / `AzurePowerShell@5` | `azure/login` with OIDC (`id-token: write` on that job only) |
| `Docker@2` | `docker/login-action` + `docker/build-push-action` (catalog SHAs) |
| `PowerShell@2` / `Bash@3` / `script` | `run:` with `shell: pwsh` / `bash` / `cmd` |
| `##vso[task.setvariable]` | `>> $GITHUB_ENV` (or `$GITHUB_OUTPUT` for outputs) |
| `##vso[task.prependpath]` | `>> $GITHUB_PATH` |
| `##vso[task.logissue type=warning/error]` | `::warning::` / `::error::` |

Predefined variables: `Build.SourcesDirectory`/`System.DefaultWorkingDirectory` -> `github.workspace`;
`Build.ArtifactStagingDirectory` -> `${{ runner.temp }}/staging` (set via step, not job env);
`Agent.TempDirectory` -> `runner.temp`; `Build.BuildId` -> `github.run_id`;
`Build.SourceBranch` -> `github.ref`; `Build.SourceVersion` -> `github.sha`;
`Build.Reason` -> `github.event_name`; `System.PullRequest.PullRequestNumber` ->
`github.event.pull_request.number`. Note: `runner` context is not available in job-level `env:`.

## migration-report.json schema

```json
{
  "pipeline": { "repo": "", "path": "", "ado_id": "" },
  "status": "ready | ready-with-flags | needs-human | off-path",
  "archetype": { "id": "", "confidence": "high | medium | low", "reason": "" },
  "platform": { "repo": "", "workflow": "", "version": "", "sha": "" },
  "files_changed": [],
  "mapped": [ { "ado": "", "gha": "" } ],
  "unmapped": [ { "ado": "", "reason": "" } ],
  "defects_fixed": [ { "what": "", "where": "" } ],
  "behaviour_changes": [ { "what": "", "why": "", "risk": "low | medium | high" } ],
  "secrets_required": [ { "name": "", "purpose": "", "oidc_possible": true } ],
  "escalations": [ { "reason": "", "detail": "" } ],
  "validation": { "actionlint": "pass | fail", "zizmor": "pass | fail", "script_parse": "pass | fail" },
  "required_check_name": "",
  "parity_checks": []
}
```

## PR description template

```
## Migrate <pipeline> from Azure DevOps to GitHub Actions (shadow mode)

**Archetype:** <id> (confidence) - calls <platform workflow>@<version>
**Status:** <status>. ADO pipeline is unchanged and remains the required check.

### What changed
- <files>

### Defects fixed from the ADO pipeline
- <list or 'none'>

### Behaviour differences
- <list with risk, or 'none'>

### Needs a human
- <escalations, unmapped items, secrets to create>

### Parity checklist (before making `<required check>` required)
- [ ] Same test count and outcomes as the matching ADO run
- [ ] Coverage within +/-0.5 pp
- [ ] Duration p50 <= ADO
- [ ] Negative test: a failing test turns the check red

Rollback: disable this workflow; ADO remains authoritative until cut-over.
```

## Final answer to the caller
One line: status, archetype, branch name, count of flags/escalations. The report and PR body
carry the detail.
