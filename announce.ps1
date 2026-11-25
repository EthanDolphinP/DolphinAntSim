<#
.SYNOPSIS
  Posts a GitHub release to the Discord channel (e.g. #ant-updates) and pings the Antvester role.

.EXAMPLE
  .\announce.ps1 1.2            # announce release v1.2 with a role ping
  .\announce.ps1 1.2 -NoPing    # same, without pinging anyone
  .\announce.ps1 -Test          # check the setup: posts a short test line, no ping

  release.ps1 runs this by itself after publishing. The settings live in announce.local.json next to this
  script (not committed, because the webhook link lets anyone post to the channel):

    { "webhook": "https://discord.com/api/webhooks/...", "roleId": "123456789012345678" }
#>
param(
    [string]$Version = "",
    [switch]$NoPing,
    [switch]$Test
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$repo = "EthanDolphinP/DolphinAntSim"

function Fail($msg) { Write-Host "Announcement not sent: $msg" -ForegroundColor Yellow; exit 1 }

$cfgPath = Join-Path $PSScriptRoot "announce.local.json"
if (-not (Test-Path $cfgPath)) { Fail "no announce.local.json (see the top of announce.ps1 or the README)." }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
if (-not $cfg.webhook -or $cfg.webhook -notmatch '^https://(discord|discordapp)\.com/api/webhooks/') { Fail "announce.local.json has no valid Discord webhook link." }
$roleId = "$($cfg.roleId)".Trim()
if ($roleId -and $roleId -notmatch '^\d{15,22}$') { Fail "roleId should be the role's number (Server Settings > Roles > right-click Antvester > Copy Role ID)." }

function Send($payload) {
    $json = $payload | ConvertTo-Json -Depth 6 -Compress
    Invoke-RestMethod -Uri $cfg.webhook -Method Post -ContentType "application/json; charset=utf-8" `
        -Body ([Text.Encoding]::UTF8.GetBytes($json)) | Out-Null
}

if ($Test) {
    Send @{ content = "Test from announce.ps1: release announcements will appear here."; allowed_mentions = @{ parse = @() } }
    Write-Host "Test message posted." -ForegroundColor Green
    exit 0
}

if (-not $Version) { Fail "say which version, e.g. .\announce.ps1 1.2" }
$tag = "v" + $Version.TrimStart("v", "V")

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
$rel = & $gh release view $tag --repo $repo --json name,body,url,assets 2>$null | ConvertFrom-Json
if (-not $rel) { Fail "release $tag not found on GitHub." }

$notes = "$($rel.body)".Trim()
if ($notes.Length -gt 3500) { $notes = $notes.Substring(0, 3500) + "`n…" }
if (-not $notes) { $notes = "See the release page for what's new." }
$zip = $rel.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
$links = "[Release page]($($rel.url))"
if ($zip) { $links = "[Download for Windows]($($zip.url)) · " + $links }

$ping = if ($roleId -and -not $NoPing) { "<@&$roleId> " } else { "" }
Send @{
    content          = "$ping**Dolphin Ant Simulator $($tag.TrimStart('v')) is out!**"
    allowed_mentions = @{ roles = @(if ($ping) { $roleId }) }
    embeds           = @(@{
        title       = $rel.name
        url         = $rel.url
        description = "$notes`n`n$links"
        color       = 0xC8743A
        footer      = @{ text = "Already playing? The game's start screen offers the update too." }
    })
}
Write-Host "Announced $tag on Discord$(if ($ping) { ' (pinged the role)' })." -ForegroundColor Green
