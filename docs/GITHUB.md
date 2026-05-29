# GitHub Plan

Example target account:

```text
https://github.com/<owner>
```

Recommended repository name:

```text
codex-monitor
```

## What Should Be Published

Publish:

- source code;
- Rainmeter skin and presets;
- PowerShell install/switch/watch scripts;
- documentation;
- `config.json` public defaults;
- `config.local.example.json` local override template;
- example `temps.txt`;
- reinstall scripts.

Do not publish:

- large runtime logs;
- personal Rainmeter.ini;
- `config.local.json`;
- generated `bin`/`obj` folders unless a release artifact is intentionally attached;
- local backups;
- zip archives unless using GitHub Releases;
- machine-specific screenshots unless intentionally documenting layout.

## Recommended First Commit

Before first commit, add:

- `.gitignore`
- `README.md`
- `docs\*.md`
- `CHANGELOG.md`

Then initialize:

```powershell
cd C:\CodexMonitor
git init
git add README.md CHANGELOG.md docs .gitignore CodexBridge Deploy Presets Watch-PrimaryDisplay.ps1 CodexMonitor.ini
git commit -m "Initial CodexMonitor project handoff"
```

Create GitHub repo:

```powershell
gh repo create <owner>/codex-monitor --public --source C:\CodexMonitor --remote origin --push
```

Use `--private` instead of `--public` only if the repository still contains local/private details.

## Local Archive Policy

After GitHub is live, local restore zip archives are not required as the source of truth.

Recommended policy:

- GitHub repo is canonical for source, docs, scripts, and presets.
- GitHub Releases hold packaged restore zips when needed.
- any local staging folder is only a checkout/staging/recovery convenience.
- Do not manually maintain a local archive as the main collaboration mechanism.

## Current Limitation

Git is installed locally. GitHub Desktop is installed and authenticated.

GitHub CLI (`gh`) is not currently installed. Either publish through GitHub Desktop or install `gh`.

## Publishing With GitHub Desktop

1. Open GitHub Desktop.
2. Choose `File -> Add local repository`.
3. Select:

```text
C:\CodexMonitor
```

4. GitHub Desktop should show the initial commit.
5. Click `Publish repository`.
6. Recommended name:

```text
codex-monitor
```

7. Public is fine when private/local details have been kept out of Git.

## Publishing With GitHub CLI

If you want command-line publishing, install `gh`:

```powershell
winget install --id GitHub.cli -e
gh auth login
```

Then create/push the repo using the command above.

## Suggested Branch Workflow

- `main`: stable, working desktop widget.
- `dev`: integration branch for new changes.
- feature branches:
  - `layout/4k-tuning`
  - `bridge/sensor-mapping`
  - `deploy/reinstall-kit`

## Before Pushing

Run:

```powershell
git status --short
git diff --stat
```

Check that these files are not staged:

- `CodexBridge.error.log`
- `Backups\*`
- `*.zip`
- `CodexBridge\bin\*`
- `CodexBridge\obj\*`
- personal `Rainmeter.ini`

## Release Artifacts

An optional reinstall kit zip can be useful, but it belongs in GitHub Releases rather than normal Git history:

```text
<LocalStagingFolder>\CodexMonitor-Release.zip
```

If using GitHub Releases, tag versions like:

```text
v0.1.0
v0.2.0
```
