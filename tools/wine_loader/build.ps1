param(
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $work, $output | Out-Null
$loader = Join-Path $work 'Vulkan-Loader'
$headers = Join-Path $work 'Vulkan-Headers'

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

function Get-PinnedSource {
    param([string]$Path, [string]$Repository, [string]$Commit)
    if (-not (Test-Path -LiteralPath $Path)) {
        Invoke-Checked 'git' @('clone', '--quiet', '--branch', 'v1.4.357', '--depth', '1', $Repository, $Path)
    }
    $actual = (& git -C $Path rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne $Commit) { throw "Unexpected source revision in $Path" }
}

Get-PinnedSource $loader 'https://github.com/KhronosGroup/Vulkan-Loader.git' '5f157b62e333c63260d05d81bf66faa216ab0fb8'
Get-PinnedSource $headers 'https://github.com/KhronosGroup/Vulkan-Headers.git' 'e3b1eec08173d6b825cd3ac88c885a63b621504a'
$patch = Join-Path $PSScriptRoot 'khronos-wine.patch'
& git -C $loader apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    Invoke-Checked 'git' @('-C', $loader, 'apply', '--check', $patch)
    Invoke-Checked 'git' @('-C', $loader, 'apply', $patch)
}
$headersBuild = Join-Path $work 'headers-build'
$headersInstall = Join-Path $work 'headers-install'
$loaderBuild = Join-Path $work 'loader-build'
Invoke-Checked 'cmake' @('-S', $headers, '-B', $headersBuild, '-A', 'x64', "-DCMAKE_INSTALL_PREFIX=$headersInstall")
Invoke-Checked 'cmake' @('--install', $headersBuild, '--config', 'Release')
Invoke-Checked 'cmake' @('-S', $loader, '-B', $loaderBuild, '-A', 'x64', '-DBUILD_TESTS=OFF', "-DCMAKE_PREFIX_PATH=$headersInstall")
Invoke-Checked 'cmake' @('--build', $loaderBuild, '--config', 'Release', '--parallel')
Copy-Item -LiteralPath (Join-Path $loaderBuild 'loader/Release/vulkan-1.dll') -Destination $output
Copy-Item -LiteralPath (Join-Path $loader 'LICENSE.txt') -Destination (Join-Path $output 'Vulkan-Loader-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UPSTREAM-LICENSE.txt') -Destination (Join-Path $output 'Wine-patch-LICENSE.txt')
Copy-Item -LiteralPath $patch -Destination (Join-Path $output 'khronos-wine.patch')
