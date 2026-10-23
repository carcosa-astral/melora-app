# Publishes a Melora release on GitHub, from the version in Melora.csproj and its CHANGELOG.md entry:
# checks everything is committed and pushed, runs the tests, builds the portable folder, zips it
# (without any data folder), tags the commit and creates the release with the zip attached.
#   pwsh tools/release.ps1            do it
#   pwsh tools/release.ps1 -DryRun    everything except tagging and publishing; shows the notes
# Needs the GitHub CLI (gh), signed in.
param([switch]$DryRun)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

function Fail($message) { Write-Host "release: $message" -ForegroundColor Red; exit 1 }
# A dry run reports what would stop a real release but carries on, so it can be tried any time.
function Check($ok, $message) { if ($ok) { return }; if ($DryRun) { Write-Host "release: (would stop) $message" -ForegroundColor Yellow } else { Fail $message } }
function Run { & $args[0] $args[1..($args.Count - 1)]; if ($LASTEXITCODE -ne 0) { Fail "$($args -join ' ') failed" } }

# ---- What's being released ----
$version = ([xml](Get-Content src/Melora/Melora.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { Fail "couldn't read a version like 1.2.3 from Melora.csproj (got '$version')" }
$tag = "v$version"
$zip = "dist/Melora-$version-win-x64.zip"

# The changelog section for this version, minus its heading, becomes the release notes.
$changelog = Get-Content CHANGELOG.md -Raw
$section = [regex]::Match($changelog, "(?ms)^## \[$([regex]::Escape($version))\][^\n]*\n(.*?)(?=^## \[|\z)")
if (-not $section.Success -or -not $section.Groups[1].Value.Trim()) {
    Fail "CHANGELOG.md has no entry for $version; add '## [$version] - $(Get-Date -Format yyyy-MM-dd)' with the changes"
}

# ---- Ready to release? ----
# A terminal opened before gh was installed doesn't have it on PATH yet; look where it installs.
if (-not (Get-Command gh -ErrorAction SilentlyContinue) -and (Test-Path "$env:ProgramFiles\GitHub CLI\gh.exe")) {
    $env:PATH += ";$env:ProgramFiles\GitHub CLI"
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "the GitHub CLI isn't installed (winget install GitHub.cli)" }
Check (-not (git status --porcelain)) "there are uncommitted changes"
Check ((git rev-parse --abbrev-ref HEAD) -eq "main") "not on main"
Run git fetch --quiet --tags origin
Check ((git rev-parse HEAD) -eq (git rev-parse origin/main)) "main isn't the same as GitHub's; push or pull first"
Check (-not (git tag --list $tag)) "$tag already exists; bump the version in Melora.csproj"

# ---- Build ----
Run dotnet test
Run dotnet publish src/Melora -p:PublishProfile=Portable

$stage = "dist/stage"
Remove-Item $stage, $zip -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$stage/Melora" | Out-Null
# Files only: dist/Melora may hold a data folder from trying the build, which must never ship.
Get-ChildItem dist/Melora -File | Copy-Item -Destination "$stage/Melora"
Compress-Archive -Path "$stage/Melora" -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

# The changelog wraps bullets onto indented lines; GitHub shows every line break in release notes,
# so join those back onto their bullet.
$changes = $section.Groups[1].Value.Trim() -replace '\r?\n {2,}(?=\S)', ' '

$notes = @"
$changes

**Install**: download ``Melora-$version-win-x64.zip``, unzip it anywhere and run ``Melora.exe``. To update, replace ``Melora.exe`` (and the DLLs next to it) and keep your ``data`` folder. Windows 10 (1809) or later, 64-bit.

Full history: [CHANGELOG.md](https://github.com/carcosa-astral/melora-app/blob/main/CHANGELOG.md)
"@
$notesFile = New-TemporaryFile
Set-Content $notesFile $notes -Encoding utf8

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
if ($DryRun) {
    Write-Host "Dry run: would tag $tag and publish 'Melora $tag' with $zip ($size MB). Notes:`n" -ForegroundColor Yellow
    Write-Host $notes
    Remove-Item $notesFile
    exit 0
}

# ---- Publish ----
Run git tag $tag
Run git push --quiet origin $tag
Run gh release create $tag $zip --title "Melora $tag" --notes-file $notesFile --verify-tag
Remove-Item $notesFile
Write-Host "Published Melora $tag ($size MB)." -ForegroundColor Green
