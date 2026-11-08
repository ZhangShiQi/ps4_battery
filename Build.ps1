$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish PS4Battery.csproj -c Release -o app
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $test = Start-Process -FilePath (Join-Path $PSScriptRoot 'app\PS4Battery.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw "Self-test failed: $($test.ExitCode)" }
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'app\test-result.txt')
    Write-Host 'Build complete. Run Start.cmd to start the tray application.'
}
finally { Pop-Location }
