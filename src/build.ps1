# build.ps1 - 编译 HotspotGuard.exe (.NET Framework + WinRT, 无需 .NET SDK)
$ErrorActionPreference = 'Stop'
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path (Split-Path -Parent $src) 'HotspotGuard.exe'
$gac = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Get-Gac([string]$name) {
    $dll = Get-ChildItem (Join-Path $gac $name) -Recurse -Filter '*.dll' -ErrorAction Stop | Select-Object -First 1
    if (-not $dll) { throw "GAC assembly not found: $name" }
    return $dll.FullName
}

# 只需显式引用不在 csc 默认路径中的程序集(facade + WinRT 桥), 其余由 csc 自带
$refs = @('System.Runtime', 'System.Runtime.WindowsRuntime') | ForEach-Object { Get-Gac $_ }
$winmds = @('C:\Windows\System32\WinMetadata\Windows.Foundation.winmd', 'C:\Windows\System32\WinMetadata\Windows.Networking.winmd')

$args = @('/nologo', '/target:winexe', '/optimize', '/codepage:65001', "/out:$out", "/win32manifest:$(Join-Path $src 'app.manifest')")
foreach ($r in $refs) { $args += "/r:$r" }
foreach ($w in $winmds) { $args += "/r:$w" }
$args += (Join-Path $src 'HotspotGuard.cs')

& $csc @args
if ($LASTEXITCODE -ne 0) { throw "编译失败, exit=$LASTEXITCODE" }
"编译成功: $out ($([math]::Round((Get-Item $out).Length/1KB,1)) KB)"
