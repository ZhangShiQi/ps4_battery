$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    [xml]$project = Get-Content -LiteralPath 'PS4Battery.csproj'
    $version = [string]$project.Project.PropertyGroup.Version
    $packageName = "PS4Battery-$version-win-x64"
    $artifacts = Join-Path $PSScriptRoot 'artifacts'
    $stage = Join-Path $artifacts ([guid]::NewGuid().ToString('N'))
    $package = Join-Path $stage $packageName
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    dotnet publish PS4Battery.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $package
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    $test = Start-Process -FilePath (Join-Path $package 'PS4Battery.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw "Release self-test failed: $($test.ExitCode)" }
    Get-Content -LiteralPath (Join-Path $package 'test-result.txt')
    Remove-Item -LiteralPath (Join-Path $package 'test-result.txt')
    Copy-Item -LiteralPath 'README.md','RELEASE-NOTES.md' -Destination $package
    if (Test-Path -LiteralPath (Join-Path $package 'settings.json')) { throw 'Personal settings must not be packaged.' }
    $archive = Join-Path $artifacts "$packageName.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Host "Package: $archive"
    Write-Host "SHA256: $hash"
}
finally { Pop-Location }
