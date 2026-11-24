<#
.SYNOPSIS
  Builds the game and publishes it as a GitHub release, in one go.

.EXAMPLE
  .\release.ps1 1.2 "Bigger maps and a new species"

  1. Sets the game's version (Project Settings > Player > Version) to 1.2
  2. Builds the Windows .exe with Unity (the project must not be open in the Unity editor)
  3. Zips it as Builds\DolphinAntSimulator-v1.2-Windows.zip (without the Burst debug folder)
  4. Commits the version change, tags it v1.2 and pushes to GitHub
  5. Creates the GitHub release v1.2 with the zip attached

  Needs GitHub CLI signed in once:  gh auth login
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$Version = $Version.TrimStart("v", "V")
$tag = "v$Version"

function Fail($msg) { Write-Host "Release stopped: $msg" -ForegroundColor Red; exit 1 }
function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }

# ---- checks
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { Fail "GitHub CLI not found. Install it: winget install GitHub.cli" }
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { Fail "GitHub CLI is not signed in. Run: gh auth login" }
if (Test-Path "Temp\UnityLockfile") {
    try { [IO.File]::Open("Temp\UnityLockfile", 'Open', 'ReadWrite', 'None').Close() }
    catch { Fail "this project is open in the Unity editor. Close it first (the build needs the project)." }
}
if (git tag --list $tag) { Fail "tag $tag already exists. Pick a higher version." }
$dirty = git status --porcelain | Where-Object { $_ -notmatch "ProjectSettings/ProjectSettings.asset" }
if ($dirty) { Fail "there are uncommitted changes. Commit them first:`n$($dirty -join "`n")" }

$unityVersion = (Select-String -Path "ProjectSettings\ProjectVersion.txt" -Pattern "m_EditorVersion: (.+)").Matches[0].Groups[1].Value.Trim()
$unity = "C:\Program Files\Unity\Hub\Editor\$unityVersion\Editor\Unity.exe"
if (-not (Test-Path $unity)) { Fail "Unity $unityVersion not found at $unity" }

# ---- version
Step "Setting version to $Version"
$ps = "ProjectSettings\ProjectSettings.asset"
$text = [IO.File]::ReadAllText($ps)
$text = [regex]::Replace($text, "(?m)^  bundleVersion: .*$", "  bundleVersion: $Version")
[IO.File]::WriteAllText($ps, $text)

# ---- build
Step "Building with Unity $unityVersion (a few minutes)"
$log = Join-Path $PSScriptRoot "Builds\build.log"
New-Item -ItemType Directory -Force "Builds" | Out-Null
$p = Start-Process -FilePath $unity -Wait -PassThru -ArgumentList @(
    "-batchmode", "-quit", "-projectPath", "`"$PSScriptRoot`"",
    "-executeMethod", "AntSim.EditorTools.AntSimBuild.BuildWindows", "-logFile", "`"$log`"")
$result = Select-String -Path $log -Pattern "AntSimBuild: (\w+)" | Select-Object -Last 1
if ($p.ExitCode -ne 0 -or -not $result -or $result.Matches[0].Groups[1].Value -ne "Succeeded") {
    Fail "the Unity build failed. See $log"
}
Write-Host $result.Line

# ---- zip
Step "Zipping"
$zip = "Builds\DolphinAntSimulator-$tag-Windows.zip"
$stage = "Builds\DolphinAntSimulator"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
Copy-Item -Recurse "Builds\Windows" $stage
Get-ChildItem $stage -Directory -Filter "*DoNotShip*" | Remove-Item -Recurse -Force
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item -Recurse -Force $stage
Write-Host ("{0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

# ---- git
Step "Committing, tagging $tag and pushing"
git add $ps
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { git commit -q -m "Version $Version" }
git tag -a $tag -m "Version $Version"
git push origin HEAD
git push origin $tag

# ---- release
Step "Publishing GitHub release $tag"
$ghArgs = @("release", "create", $tag, $zip, "--title", "Dolphin Ant Simulator $Version")
if ($Notes) { $ghArgs += @("--notes", $Notes) } else { $ghArgs += "--generate-notes" }
& $gh @ghArgs
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (the tag is pushed; you can retry: gh release create $tag $zip)" }
Write-Host "`nDone: https://github.com/EthanDolphinP/DolphinAntSim/releases/tag/$tag" -ForegroundColor Green
