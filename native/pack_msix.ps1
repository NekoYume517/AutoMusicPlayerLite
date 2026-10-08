param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [Parameter(Mandatory=$true)][string]$StageDir,
    [Parameter(Mandatory=$true)][string]$OutputFile,
    [Parameter(Mandatory=$true)][string]$Identity,
    [Parameter(Mandatory=$true)][string]$DisplayName,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Python,
    [Parameter(Mandatory=$true)][string]$SigningThumbprint,
    [string]$SdkBin='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
)
$ErrorActionPreference='Stop'
& $Python (Join-Path $PSScriptRoot 'stage_msix.py') --publish $PublishDir --stage $StageDir --nuget (Join-Path $env:USERPROFILE '.nuget\packages') --identity $Identity --display-name $DisplayName --version $Version
if($LASTEXITCODE -ne 0){throw 'MSIX staging failed'}
Add-Type -AssemblyName System.Drawing
$assets=Join-Path $StageDir 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$icon=[System.Drawing.Icon]::new((Join-Path $PublishDir 'app.ico'),[System.Drawing.Size]::new(256,256))
$bitmap=$icon.ToBitmap()
try {
    foreach($entry in @{StoreLogo=50;Square44x44Logo=44;Square150x150Logo=150}.GetEnumerator()){
        $resized=[System.Drawing.Bitmap]::new($entry.Value,$entry.Value)
        $graphics=[System.Drawing.Graphics]::FromImage($resized)
        try {
            $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($bitmap,0,0,$entry.Value,$entry.Value)
            $resized.Save((Join-Path $assets ($entry.Key+'.png')),[System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $resized.Dispose() }
    }
} finally { $bitmap.Dispose(); $icon.Dispose() }
# Packaged WinUI reads resources.pri, indexed by the package identity.
$priList=Join-Path $StageDir 'msix.pri.resfiles'
$priConfig=Join-Path $StageDir 'msix.priconfig.xml'
Set-Content -LiteralPath $priList -Value 'AutoMusicPlayerLite.pri' -Encoding utf8
Set-Content -LiteralPath $priConfig -Encoding utf8 -Value '<?xml version="1.0" encoding="utf-8"?><resources targetOsVersion="10.0.0" majorVersion="1"><index root="\" startIndexAt="msix.pri.resfiles"><default><qualifier name="Language" value="en-US"/></default><indexer-config type="PRI" /><indexer-config type="RESFILES" qualifierDelimiter="." /></index></resources>'
& (Join-Path $SdkBin 'makepri.exe') new /pr $StageDir /cf $priConfig /in $Identity /of (Join-Path $StageDir 'resources.pri') /o *> ($StageDir+'.makepri.log')
if($LASTEXITCODE -ne 0){throw 'MSIX resource indexing failed'}
Remove-Item -LiteralPath $priList,$priConfig
& (Join-Path $SdkBin 'makeappx.exe') pack /d $StageDir /p $OutputFile /o *> ($StageDir+'.makeappx.log')
if($LASTEXITCODE -ne 0){Get-Content -LiteralPath ($StageDir+'.makeappx.log') -Tail 25; throw 'MSIX manifest or package validation failed'}
& (Join-Path $SdkBin 'signtool.exe') sign /fd SHA256 /s My /sha1 $SigningThumbprint $OutputFile
if($LASTEXITCODE -ne 0){throw 'MSIX signing failed'}
Write-Output ('Signed MSIX: '+$OutputFile)
