$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try {
    dotnet run --project ./tests/PolicyTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Command policy checks failed.' }

    dotnet build ./src/FakeCheats.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

    [xml]$projectDefinition = Get-Content -LiteralPath './src/FakeCheats.csproj' -Raw
    $pluginVersion = $projectDefinition.Project.PropertyGroup.Version
    $packageRoot = Join-Path $PSScriptRoot "dist/package-$pluginVersion"
    $pluginFolder = Join-Path $packageRoot 'addons/counterstrikesharp/plugins/FakeCheats'
    $configFolder = Join-Path $packageRoot 'addons/counterstrikesharp/configs/plugins/FakeCheats'
    New-Item -ItemType Directory -Path $pluginFolder, $configFolder -Force | Out-Null

    foreach ($name in @('FakeCheats.dll', 'FakeCheats.deps.json', 'FakeCheats.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src/bin/Release/net10.0/$name") -Destination $pluginFolder -Force
    }
    # Use CSS's example configuration mechanism so upgrades don't overwrite the
    # server owner's existing JSON configuration.
    Copy-Item -LiteralPath './FakeCheats.example.json' -Destination $configFolder -Force
    Copy-Item -LiteralPath './messages.example.json' -Destination $configFolder -Force
    Copy-Item -LiteralPath './README.md' -Destination $packageRoot -Force
    $archive = Join-Path $PSScriptRoot "dist/FakeCheats-$pluginVersion.zip"
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archive -Force
    Get-Item -LiteralPath $archive | Select-Object FullName, Length
} finally {
    Pop-Location
}
