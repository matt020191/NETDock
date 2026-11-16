<#
.SYNOPSIS
    Build, sign and publish a NETDock release to GitHub.

.DESCRIPTION
    1. Checks the tree is clean and pushed, reads VERSION (major.minor[.patch]).
    2. Runs the tests and publishes the Native AOT exe.
    3. Signs NETDock.exe with M-TEC's code-signing cert in Azure Key Vault (AzureSignTool, cached 'az login').
    4. Builds the portable zip (exe + languages + docs + 'portable' marker) and SHA-256 files.
    5. Tags vX.Y.Z on the private Gitea (origin), waits for its push mirror to carry the commit to
       GitHub (matt020191/NETDock), then creates the GitHub release and uploads:
         NETDock.exe, NETDock.exe.sha256           <- what the in-app updater downloads and verifies
         NETDock-X.Y.Z-portable.zip(.sha256)
    Run with -NoPublish to stop after step 4 (output in packaging\out).

    Keep this file ASCII-only: Windows PowerShell 5.1 reads BOM-less scripts as ANSI.

.PARAMETER Notes        Release notes (markdown). Default: commit subjects since the previous tag.
.PARAMETER NoPublish    Build and sign only; don't tag or touch GitHub.
.PARAMETER GitHubToken  Token with contents:write on the repo; default $env:GITHUB_TOKEN, else the git credential for github.com.
#>
param(
    [string] $Notes,
    [switch] $NoPublish,
    [string] $GitHubToken = $env:GITHUB_TOKEN,
    [string] $VaultUrl = 'https://mtec-premium-kv.vault.azure.net/',
    [string] $CertificateName = 'mtsccert',
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $GitHubRepo = 'matt020191/NETDock'
)
$ErrorActionPreference = 'Stop'
function Write-Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok($m) { Write-Host "    $m" -ForegroundColor Green }

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$out = Join-Path $PSScriptRoot 'out'

# ---------------------------------------------------------------------
# 1. Version + git state
# ---------------------------------------------------------------------
$raw = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
$v = [Version]$raw
$version = '{0}.{1}.{2}' -f $v.Major, $v.Minor, [Math]::Max(0, $v.Build)
$tag = "v$version"
Write-Step "NETDock $version ($tag)"

if (-not $NoPublish) {
    if (git status --porcelain) { throw 'Working tree is not clean: commit first.' }
    git fetch -q origin
    if ((git rev-parse HEAD) -ne (git rev-parse '@{u}')) { throw 'HEAD is not pushed to origin.' }
    if (git tag --list $tag) { throw "Tag $tag already exists: bump VERSION." }
}

# ---------------------------------------------------------------------
# 2. Test + publish
# ---------------------------------------------------------------------
Write-Step 'Running tests'
dotnet test NETDock.Tests -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

Write-Step 'Publishing Native AOT build'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$pub = Join-Path $out 'publish'
dotnet publish NETDock.App -c Release -r win-x64 -o $pub | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$exe = Join-Path $pub 'NETDock.exe'
$fileVersion = (Get-Item $exe).VersionInfo.ProductVersion
Write-Ok "Built $exe ($fileVersion)"

# ---------------------------------------------------------------------
# 3. Sign
# ---------------------------------------------------------------------
Write-Step 'Restoring AzureSignTool'
dotnet tool restore | Out-Host
Write-Step 'Fetching AKV access token'
$token = az account get-access-token --resource 'https://vault.azure.net' --query accessToken -o tsv
if ($LASTEXITCODE -ne 0 -or -not $token) { throw "Could not fetch AKV access token. Run 'az login'." }
Write-Step "Signing NETDock.exe with '$CertificateName'"
try {
    & dotnet tool run azuresigntool sign -kvu $VaultUrl -kvc $CertificateName -kva $token `
        -tr $TimestampUrl -td sha256 -fd sha256 -d 'NETDock' -du 'https://github.com/matt020191/NETDock' $exe | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
} finally { $token = $null }
$sig = Get-AuthenticodeSignature $exe
if ($sig.Status -ne 'Valid') { throw "Signature is not Valid: $($sig.Status)." }
Write-Ok "Authenticode: Valid ($($sig.SignerCertificate.Subject))"

# ---------------------------------------------------------------------
# 4. Assets
# ---------------------------------------------------------------------
Write-Step 'Packaging'
$assets = Join-Path $out 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
Copy-Item $exe (Join-Path $assets 'NETDock.exe')

$stage = Join-Path $out 'portable\NETDock'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item $exe $stage
Copy-Item (Join-Path $pub 'languages') $stage -Recurse
Copy-Item README.md, THIRD-PARTY-NOTICES.md $stage
if (Test-Path LICENSE) { Copy-Item LICENSE $stage }
Set-Content (Join-Path $stage 'portable') 'Settings are kept in the Data folder next to NETDock.exe.' -Encoding ascii
$zip = Join-Path $assets "NETDock-$version-portable.zip"
Compress-Archive -Path $stage -DestinationPath $zip -Force

foreach ($f in Get-ChildItem $assets -File) {
    $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$($f.FullName).sha256", "$hash  $($f.Name)`n", (New-Object Text.UTF8Encoding $false))
}
Get-ChildItem $assets | ForEach-Object { Write-Ok ("{0,-34} {1,10:N0} bytes" -f $_.Name, $_.Length) }

if ($NoPublish) { Write-Step "Done (not published). Assets: $assets"; return }

# ---------------------------------------------------------------------
# 5. Tag (Gitea -> mirror) and GitHub release
# ---------------------------------------------------------------------
if (-not $GitHubToken) {
    # Windows PowerShell mangles text piped into native commands ("missing protocol field"): feed git from a file.
    $req = Join-Path $out 'credential-request.txt'
    [IO.File]::WriteAllText($req, "protocol=https`nhost=github.com`n`n")
    try { $cred = cmd /c "git credential fill < `"$req`"" } finally { Remove-Item $req -ErrorAction SilentlyContinue }
    $GitHubToken = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
}
if (-not $GitHubToken) { throw 'No GitHub credentials: set GITHUB_TOKEN.' }
$headers = @{ Authorization = "Bearer $GitHubToken"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'NETDock-release' }
$api = "https://api.github.com/repos/$GitHubRepo"
$sha = git rev-parse HEAD

if (-not $Notes) {
    $prev = git tag --list 'v*' --sort=-v:refname | Select-Object -First 1  # (git describe errors when there's no tag yet)
    $range = if ($prev) { "$prev..HEAD" } else { 'HEAD' }
    $Notes = (git log $range --no-merges --format='- %s') -join "`n"
}

Write-Step "Tagging $tag on origin"
git tag -a $tag -m "NETDock $version"
git push -q origin $tag
if ($LASTEXITCODE -ne 0) { throw 'Pushing the tag failed.' }

# Gitea's push mirror syncs on push (or on its interval); the release needs the commit on GitHub.
Write-Step 'Waiting for the mirror to reach GitHub'
$deadline = (Get-Date).AddMinutes(10)
while ($true) {
    try { Invoke-RestMethod -Uri "$api/commits/$sha" -Headers $headers | Out-Null; break }
    catch {
        if ((Get-Date) -gt $deadline) { throw "Commit $sha is not on GitHub after 10 minutes: check the Gitea push mirror (Settings > Mirror settings > Synchronize now), then re-run the release step." }
        Start-Sleep 15
    }
}
Write-Ok 'Commit is on GitHub'

Write-Step "Creating GitHub release $tag"
$body = @{ tag_name = $tag; target_commitish = $sha; name = "NETDock $version"; body = $Notes; draft = $false; prerelease = $false } | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
Write-Ok "Release id $($release.id)"

foreach ($f in Get-ChildItem $assets -File) {
    Write-Step "Uploading $($f.Name)"
    $type = if ($f.Extension -eq '.zip') { 'application/zip' } elseif ($f.Extension -eq '.sha256') { 'text/plain' } else { 'application/octet-stream' }
    Invoke-RestMethod -Method Post -Uri "https://uploads.github.com/repos/$GitHubRepo/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($f.Name))" `
        -Headers $headers -ContentType $type -InFile $f.FullName | Out-Null
}
Write-Step "Published: $($release.html_url)"
