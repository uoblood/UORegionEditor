# Builds both release layouts into publish\ and zips them next to it.
#
#   standalone  - one big exe, nothing to install (self-contained, single-file)
#   dotnet10    - small, needs the .NET 10 Desktop Runtime
#
# NEVER set EnableCompressionInSingleFile: it breaks Silk.NET's native library
# loading and the app dies with "GlfwPlatform - not applicable".
param([string]$OutDir = "$PSScriptRoot\publish")

$ErrorActionPreference = 'Stop'
$ver = ([xml](Get-Content "$PSScriptRoot\UORegionEditor.csproj")).Project.PropertyGroup.Version | Where-Object { $_ }
Write-Host "UORegionEditor $ver"

$variants = @(
    @{ Name = 'standalone'; SelfContained = 'true';  Single = 'true'  },
    @{ Name = 'dotnet10';   SelfContained = 'false'; Single = 'false' }
)

foreach ($v in $variants) {
    $dir = Join-Path $OutDir "$($v.Name)\UORegionEditor"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }

    foreach ($proj in @(
        @{ Path = "$PSScriptRoot\UORegionEditor.csproj"; Out = $dir },
        @{ Path = "$PSScriptRoot\Server\UORegionEditor.Server.csproj"; Out = "$dir\server" }
    )) {
        dotnet publish $proj.Path -c Release -r win-x64 -o $proj.Out -v q --nologo `
            --self-contained $v.SelfContained `
            -p:PublishSingleFile=$($v.Single) -p:DebugType=none
        if ($LASTEXITCODE -ne 0) { throw "publish failed: $($proj.Path) [$($v.Name)]" }
    }

    Copy-Item "$PSScriptRoot\README.md", "$PSScriptRoot\LICENSE", "$PSScriptRoot\THIRD-PARTY-NOTICES" $dir
    Get-ChildItem $dir -Recurse -Include *.pdb | Remove-Item -Force

    # nothing user-generated may ever ship
    $junk = Get-ChildItem $dir -Recurse -Include imgui.ini, ui.json, connect.json, server.json, regions.json, *.log, *.mul, *.uop
    if ($junk) { throw "runtime junk in $($v.Name): $($junk.Name -join ', ')" }

    $count = (Get-ChildItem $dir -Recurse -File).Count
    $mb = [math]::Round((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "  $($v.Name): $count files, $mb MB"

    # 7-Zip only: PowerShell 5.1's Compress-Archive writes backslash separators
    # on-access antivirus can still be reading a just-written zip, so retry the delete
    $zip = Join-Path $OutDir "UORegionEditor-$ver-win-x64-$($v.Name).zip"
    for ($i = 0; (Test-Path $zip) -and $i -lt 10; $i++) {
        try { Remove-Item $zip -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 2 }
    }
    if (Test-Path $zip) { throw "could not replace $zip (locked)" }
    & "$env:ProgramFiles\7-Zip\7z.exe" a -tzip -mx=7 $zip (Join-Path $OutDir "$($v.Name)\UORegionEditor") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "zip failed: $($v.Name)" }
    Write-Host "  -> $(Split-Path $zip -Leaf) ($([math]::Round((Get-Item $zip).Length/1MB,1)) MB)"
}
