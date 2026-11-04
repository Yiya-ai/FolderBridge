$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework compiler not found.' }
$output = Join-Path $PSScriptRoot '目录搬家助手.exe'
$sources = @('Program.cs','NativeFiles.cs','Core.cs','MainForm.cs','SelfTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$output" "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination ($output + '.config') -Force
Get-Item -LiteralPath $output | Select-Object FullName,Length
