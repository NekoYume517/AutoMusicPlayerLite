param(
    [string]$Python='python',
    [Parameter(Mandatory=$true)][string]$VcRuntime,
    [Parameter(Mandatory=$true)][string]$InnoCompiler,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$SigningThumbprint,
    [string]$SdkBin='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$out=[IO.Path]::GetFullPath($OutputDirectory)
$project=[xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'WinUI\AutoMusicPlayer.csproj') -Raw)
$version=[string]$project.Project.PropertyGroup[0].Version
$private=([string]$project.Project.PropertyGroup[0].Product).EndsWith(' Private')
$stem='AutoMusicPlayerLite-'+$version+$(if($private){'-Private'}else{''})
$identity='NekoYume517.AutoMusicPlayerLite'+$(if($private){'Private'}else{''})
$label='Auto Music Player Lite'+$(if($private){' Private'}else{''})
$stamp=[guid]::NewGuid().ToString('N')
$payload=Join-Path $repo ('work\portable-payload-'+$stamp)
$portable=Join-Path $repo ('work\portable-publish-'+$stamp)
$stage=Join-Path $repo ('work\msix-stage-'+$stamp)
$cert=Get-Item -LiteralPath ('Cert:\CurrentUser\My\'+$SigningThumbprint)
if(!$cert.HasPrivateKey -or $cert.Subject -ne 'CN=NekoYume517'){throw 'A code-signing certificate for CN=NekoYume517 with its private key in CurrentUser\My is required'}
& (Join-Path $PSScriptRoot 'build.ps1') -Python $Python -VcRuntime $VcRuntime -InnoCompiler $InnoCompiler -OutputDirectory $out
$release=Join-Path $repo 'work\release-win64'
Copy-Item -LiteralPath $release -Destination $payload -Recurse
Set-Content -LiteralPath (Join-Path $payload 'portable.mode') -Value 'portable' -Encoding utf8
Copy-Item -LiteralPath (Join-Path $payload 'AutoMusicPlayerLite.pri') -Destination (Join-Path $payload 'resources.pri')
Push-Location $PSScriptRoot
try {
    & dotnet publish .\WinUI\AutoMusicPlayer.csproj -c Release -p:Platform=x64 -p:IncludeSourceRevisionInInformationalVersion=false ('-p:PortablePayloadDir='+$payload+'\') -o $portable --verbosity quiet
    if($LASTEXITCODE -ne 0){throw 'Single-file publish failed'}
} finally { Pop-Location }
$green=Join-Path $out ($stem+'-Portable-x64.exe')
Copy-Item -LiteralPath (Join-Path $portable 'AutoMusicPlayerLite.exe') -Destination $green -Force
$report=Join-Path $repo ('work\portable-check-'+$stamp+'.json')
$data=Join-Path $repo ('work\portable-check-data-'+$stamp)
$app=Start-Process -FilePath $green -ArgumentList @('--self-test',('"'+$report+'"'),'--data-dir',('"'+$data+'"'),'--picker-self-test') -WindowStyle Hidden -PassThru
if(!$app.WaitForExit(60000) -or !(Test-Path -LiteralPath $report) -or !(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).passed){throw 'Portable WinUI verification failed'}
& (Join-Path $PSScriptRoot 'pack_msix.ps1') -PublishDir $release -StageDir $stage -OutputFile (Join-Path $out ($stem+'-x64.msix')) -Identity $identity -DisplayName $label -Version ($version+'.0') -Python (Join-Path $repo 'work\build-env\Scripts\python.exe') -SigningThumbprint $SigningThumbprint -SdkBin $SdkBin
Export-Certificate -Cert $cert -FilePath (Join-Path $out 'AutoMusicPlayerLite-MSIX.cer') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MSIX安装说明.md') -Destination (Join-Path $out 'MSIX安装说明.md') -Force
Write-Output ('Built installer EXE, single-file portable EXE and signed MSIX: '+$out)
