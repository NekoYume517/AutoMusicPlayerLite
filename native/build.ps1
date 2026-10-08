param(
    [string]$Python = 'python',
    [Parameter(Mandatory = $true)][string]$VcRuntime,
    [Parameter(Mandatory = $true)][string]$InnoCompiler,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$buildWork = Join-Path $repo 'work'
$release = Join-Path $buildWork 'release-win64'
$envPython = Join-Path $buildWork 'build-env\Scripts\python.exe'
New-Item -ItemType Directory -Path $buildWork, $OutputDirectory -Force | Out-Null
if (!(Test-Path -LiteralPath $envPython)) {
    & $Python -m venv (Join-Path $buildWork 'build-env')
    if ($LASTEXITCODE -ne 0) { throw 'Python venv creation failed' }
}
Push-Location $repo
try {
    & $envPython -m pip install -r native\requirements-build.txt
    if ($LASTEXITCODE -ne 0) { throw 'Python dependency install failed' }
    & $envPython -m unittest discover -s native\tests -v
    if ($LASTEXITCODE -ne 0) { throw 'Python tests failed' }
    & dotnet run --project native/tests/UpdateTests --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Update tests failed" }
    & $envPython -m PyInstaller native\AmpEngine.spec --noconfirm --distpath work\engine-dist --workpath work\engine-build --log-level WARN
    if ($LASTEXITCODE -ne 0) { throw 'Engine freeze failed' }
    Push-Location $PSScriptRoot
    try {
        & dotnet publish .\WinUI\AutoMusicPlayer.csproj -c Release -p:Platform=x64 -o $release --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed' }
    } finally { Pop-Location }
    New-Item -ItemType Directory -Path (Join-Path $release 'backend') -Force | Out-Null
    Copy-Item -Path .\work\engine-dist\AmpEngine\* -Destination (Join-Path $release 'backend') -Recurse -Force
    Copy-Item -Path (Join-Path $VcRuntime '*.dll') -Destination $release -Force
    & $envPython native\prepare_release.py --publish $release --nuget (Join-Path $env:USERPROFILE '.nuget\packages')
    if ($LASTEXITCODE -ne 0) { throw 'Release notice assembly failed' }
    $report = Join-Path $buildWork ('build-self-test-' + [guid]::NewGuid().ToString('N') + '.json')
    $data = Join-Path $buildWork ('build-test-data-' + [guid]::NewGuid().ToString('N'))
    $app = Start-Process -FilePath (Join-Path $release 'AutoMusicPlayerLite.exe') -ArgumentList @('--self-test', ('"' + $report + '"'), '--data-dir', ('"' + $data + '"'), '--picker-self-test') -WindowStyle Hidden -PassThru
    if (!$app.WaitForExit(60000)) { throw 'Native self-test timed out' }
    if (!(Test-Path -LiteralPath $report) -or !(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).passed) { throw "Native self-test failed: $report" }
    & $InnoCompiler ('/DPublishDir=' + $release) ('/DOutputDir=' + [IO.Path]::GetFullPath($OutputDirectory)) native\installer.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compile failed' }
    Write-Output "Installer exported to $OutputDirectory"
} finally { Pop-Location }
