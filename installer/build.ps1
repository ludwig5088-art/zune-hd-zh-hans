param(
    [Parameter(Mandatory=$true)][string]$BundlePath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path $PSScriptRoot 'source'
$bundleResolved = (Resolve-Path -LiteralPath $BundlePath).Path
$bundleHash = (Get-FileHash -LiteralPath $bundleResolved -Algorithm SHA256).Hash.ToLowerInvariant()
if ($bundleHash -ne 'a1da016c0bd1f31655ae126fe39192028df7d863214bae01939c6c10d2ab9474') {
    throw 'Bundle ZIP differs from the tested installer pin. Do not bypass the guard.'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory.' }
$buildRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $buildRoot | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sourcePaths = @('LyraFiles.cs','BundleCore.cs','BundleConstants.cs','InstallerWindow.cs') | ForEach-Object { Join-Path $sourceRoot $_ }
$options = @('/nologo','/codepage:65001','/optimize+','/platform:anycpu','/target:winexe','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll',('/resource:' + $bundleResolved + ',bundle.zip'),('/resource:' + $sourceRoot + '/bundle-spec.json,bundle.spec'),('/out:' + $buildRoot + '/ZuneChineseHandheld.exe'))
& $compilerPath @options @sourcePaths
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output (Join-Path $buildRoot 'ZuneChineseHandheld.exe')
