# Run with PowerShell 7 on Windows. Only explicit files are packaged.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$files = @('.gitignore', '.gitattributes', '.github/workflows/ci.yml',
    'Program.cs', 'NativeFiles.cs', 'Core.cs', 'MainForm.cs', 'SelfTests.cs',
    'app.config', 'app.manifest', 'build.ps1', 'package.ps1',
    'README.md', 'CONTRIBUTING.md', 'SECURITY.md', 'RELEASE-CHECK.md')
foreach ($name in $files) {
    $item = Get-Item -LiteralPath (Join-Path $root $name)
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Not a regular release source file: $name"
    }
}
& (Join-Path $root 'build.ps1')
$base = Join-Path $root ('release/public/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $base | Out-Null
$testDir = Join-Path $base 'verification'
$exe = Join-Path $root '目录搬家助手.exe'
$process = Start-Process -FilePath $exe -ArgumentList @('--self-test', ('"' + $testDir + '"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Integration tests failed; see $testDir/test-results.txt" }
$report = Get-Content -LiteralPath (Join-Path $testDir 'test-results.txt') -Encoding utf8
$report | Where-Object { $_ -match '^(PASS|SKIP):' } | Write-Output
$source = Join-Path $base 'source/FolderBridge'
$binary = Join-Path $base 'windows/FolderBridge'
foreach ($name in $files) {
    $dest = Join-Path $source $name
    New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $dest
}
New-Item -ItemType Directory -Path $binary -Force | Out-Null
foreach ($name in @('目录搬家助手.exe', '目录搬家助手.exe.config', 'README.md', 'SECURITY.md', 'RELEASE-CHECK.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $binary $name)
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archives = @('FolderBridge-1.0.0-source.zip', 'FolderBridge-1.0.0-windows.zip')
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $source -Parent), (Join-Path $base $archives[0]))
[IO.Compression.ZipFile]::CreateFromDirectory((Split-Path $binary -Parent), (Join-Path $base $archives[1]))
$hashes = foreach ($name in $archives) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $base $name)).Hash.ToLowerInvariant()
    "$hash  $name"
}
$hashes | Set-Content -LiteralPath (Join-Path $base 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Packages: $base"
