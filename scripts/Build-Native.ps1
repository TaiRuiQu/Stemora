[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$CpuOnly,
    [switch]$BuildTensorBackend,
    [string]$CudaArchitectures = '86-virtual;120a-real',
    [int]$Parallel = 4,
    [string]$CudaToolkitRoot,
    [string]$VisualStudioRoot,
    [string]$Proxy = $env:STEMORA_DOWNLOAD_PROXY
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$source = Join-Path $workspace '.local/vendor/ggml-source'
$packages = Join-Path $workspace '.local/tools/cuda132-packages'
$destination = Join-Path $workspace $(if ($CpuOnly) { 'artifacts/native-cpu' } else { 'artifacts/native' })
$ProgressPreference = 'SilentlyContinue'
$requestOptions = @{}
if ($Proxy) { $requestOptions.Proxy = $Proxy }

# Find the installed MSVC C++ workload without assuming a VS version or drive.
if (-not $VisualStudioRoot) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools with the C++ workload is required.' }
    $instances = & $vswhere -products '*' -all -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
    $VisualStudioRoot = $instances | Sort-Object installationVersion -Descending | Select-Object -First 1 -ExpandProperty installationPath
}
if (-not $VisualStudioRoot) { throw 'No installed MSVC x64 build tools were found.' }
$devCommand = Join-Path $VisualStudioRoot 'VC/Auxiliary/Build/vcvars64.bat'
if (-not (Test-Path -LiteralPath $devCommand)) { throw "MSVC environment script not found: $devCommand" }
$environmentScript = Join-Path $workspace '.local/stemora-msvc-environment.cmd'
New-Item -ItemType Directory -Force (Split-Path $environmentScript -Parent) | Out-Null
[IO.File]::WriteAllText($environmentScript, "@echo off`r`ncall `"$devCommand`" >nul`r`nif errorlevel 1 exit /b 1`r`nset`r`n", [Text.Encoding]::ASCII)
$compilerEnvironment = & $env:ComSpec /d /c $environmentScript
if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize the MSVC x64 environment.' }
foreach ($line in $compilerEnvironment) {
    if ($line -match '^(PATH|INCLUDE|LIB|LIBPATH|VCToolsInstallDir|VCToolsRedistDir|VSINSTALLDIR|VCINSTALLDIR|WindowsSdkDir|WindowsSDKVersion|UCRTVersion|UniversalCRTSdkDir)=(.*)$') {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
    }
}
$compiler = (Get-Command cl.exe -ErrorAction Stop).Source
$ninja = (Get-Command ninja.exe -ErrorAction Stop).Source
# CUDA headers contain Unicode comments. Apply this to MSVC child processes,
# including NVCC's host passes, without modifying the installed headers.
$env:CL = '/utf-8'

function Assert-Success([string]$message) { if ($LASTEXITCODE -ne 0) { throw $message } }
function Fetch-Pinned([string]$url, [string]$archive, [string]$checksum) {
    New-Item -ItemType Directory -Force (Split-Path $archive -Parent) | Out-Null
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $url -OutFile $archive @requestOptions }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $checksum) { throw "Download SHA-256 mismatch: $archive" }
}
if (-not (Test-Path -LiteralPath "$source/CMakeLists.txt")) {
    $archive = Join-Path $workspace '.local/vendor/ggml-source-codeload.tar.gz'
    Fetch-Pinned 'https://codeload.github.com/ggml-org/ggml/tar.gz/3af5f5760e19a96427f5f7a93b79cbdf3d4b265b' $archive '8FA6C36ABF1456E412906C72EF82267D749E6741E39554932FFAC5A6F901AD4F'
    New-Item -ItemType Directory -Force $source | Out-Null
    tar -xzf $archive -C $source --strip-components=1
    Assert-Success 'Cannot extract pinned GGML source.'
}

function Configure-Native([string]$build, [string[]]$backendArguments) {
    cmake -S "$workspace/native" -B $build -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" "-DCMAKE_C_COMPILER=$compiler" "-DCMAKE_CXX_COMPILER=$compiler" @backendArguments -DCMAKE_BUILD_TYPE=Release "-DSTEMORA_GGML_SOURCE_DIR=$source"
    Assert-Success 'Native CMake configuration failed.'
}

if ($CpuOnly) {
    $build = Join-Path $workspace '.local/native-cpu-msvc-build'
    Configure-Native $build @('-DSTEMORA_ENABLE_CUDA=OFF', '-DSTEMORA_GGML_BINARY_DIR=')
} else {
    if (-not $CudaToolkitRoot) {
        $CudaToolkitRoot = Join-Path $workspace '.local/tools/cuda-13.2.1'
        $components = @(
            @('cuda_cudart','13.2.75','8c7f187543545cefdbf55f66ecb8c990e159e38a78694fda50e251fcb2f4fe5f'),
            @('libcublas','13.4.0.1','af2de4aab12ce773f934529877ae444452ddf9a5b1156aa2d7549f0ca39693ea')
        )
        if ($BuildTensorBackend) {
            $components += @(
                @('cuda_nvcc','13.2.78','da33f46a1a907a12abd0c192bbe907057b1e2269fdccfc778101fb02161e1c59'),
                @('cuda_crt','13.2.78','0e19f9d23451d77e32794d53bb110a4eecb26d9542391dbb3f997a688c9ddecc'),
                @('cuda_cccl','13.2.75','4c799e2c502ccc9a712d90baa102d9a2f6c50d6303f9e07a227ef4756c4d0a6a'),
                @('libnvvm','13.2.78','2cbd83a3d8bd594cb53cb1b7d0e129b0931f21915aa61c71c7491374b23e9b62')
            )
        }
        New-Item -ItemType Directory -Force $packages,$CudaToolkitRoot | Out-Null
        foreach ($component in $components) {
            $name,$version,$checksum = $component
            $archive = Join-Path $packages "$name.zip"
            $folderName = "$name-windows-x86_64-$version-archive"
            $componentRoot = Join-Path $packages $folderName
            Fetch-Pinned "https://developer.download.nvidia.com/compute/cuda/redist/$name/windows-x86_64/$folderName.zip" $archive $checksum
            if (-not (Test-Path -LiteralPath "$componentRoot/LICENSE")) { Expand-Archive -LiteralPath $archive -DestinationPath $packages -Force }
            $stamp = Join-Path $CudaToolkitRoot ".$name-$version.complete"
            if (-not (Test-Path -LiteralPath $stamp)) {
                foreach ($directory in Get-ChildItem -LiteralPath $componentRoot -Directory) { Copy-Item -LiteralPath $directory.FullName -Destination $CudaToolkitRoot -Recurse -Force }
                New-Item -ItemType File -Path $stamp -Force | Out-Null
            }
            Copy-Item -LiteralPath "$componentRoot/LICENSE" -Destination "$workspace/licenses/NVIDIA-$name.txt" -Force
        }
    }
    $CudaToolkitRoot = [IO.Path]::GetFullPath($CudaToolkitRoot)
    $env:CUDA_PATH = $CudaToolkitRoot
    $env:PATH = "$CudaToolkitRoot/bin;$CudaToolkitRoot/bin/x64;$env:PATH"

    if ($BuildTensorBackend) {
        $build = Join-Path $workspace '.local/native-cuda-build'
        $cudaCompiler = Join-Path $CudaToolkitRoot 'bin/nvcc.exe'
        if (-not (Test-Path -LiteralPath $cudaCompiler)) { throw "CUDA compiler not found: $cudaCompiler" }
        Configure-Native $build @('-DSTEMORA_GGML_BINARY_DIR=', '-DSTEMORA_ENABLE_CUDA=ON', "-DCMAKE_CUDA_COMPILER=$cudaCompiler", "-DCUDAToolkit_ROOT=$CudaToolkitRoot", "-DCMAKE_CUDA_ARCHITECTURES=$CudaArchitectures")
    } else {
        # Default: reuse a fixed precompiled CUDA tensor library. The model adapter,
        # session layer, overlap-add scheduler and process host remain our C++ build.
        $archive = Join-Path $workspace '.local/tools/BSRoformer-windows-cuda-13.1.0.zip'
        $prebuilt = Join-Path $workspace '.local/prebuilt-cuda131'
        Fetch-Pinned 'https://github.com/chenmozhijin/BSRoformer.cpp/releases/download/v0.1.0/BSRoformer-windows-cuda-13.1.0.zip' $archive 'a7c330774c0a40ec4de09ca0613af48fdc23c28bd0d90212425697daf7b1db74'
        Expand-Archive -LiteralPath $archive -DestinationPath $prebuilt -Force
        # Avoid inheriting the upstream CI CPU's instruction set. Build the portable
        # AVX2 CPU DLL from pinned source and use the same verified C ABI.
        $cpuBuild = Join-Path $workspace '.local/native-cpu-msvc-build'
        Configure-Native $cpuBuild @('-DSTEMORA_ENABLE_CUDA=OFF', '-DSTEMORA_GGML_BINARY_DIR=')
        cmake --build $cpuBuild --target ggml-cpu --parallel $Parallel
        Assert-Success 'CPU tensor backend compilation failed.'
        Copy-Item -LiteralPath "$cpuBuild/bin/ggml-cpu.dll" -Destination $prebuilt -Force
        foreach ($component in @('ggml','ggml-base','ggml-cpu','ggml-cuda')) {
            $exportText = & dumpbin.exe /nologo /exports "$prebuilt/$component.dll"
            Assert-Success "Cannot read tensor C API exports: $component"
            $symbols = @($exportText | ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)') { $Matches[1] } })
            if ($symbols.Count -eq 0) { throw "Empty tensor C API export list: $component" }
            [IO.File]::WriteAllText("$prebuilt/$component.def", "LIBRARY $component.dll`nEXPORTS`n" + ($symbols -join "`n") + "`n", [Text.Encoding]::ASCII)
            & lib.exe /nologo /machine:x64 "/def:$prebuilt/$component.def" "/out:$prebuilt/$component.lib"
            Assert-Success "Cannot generate tensor import library: $component"
        }
        $build = Join-Path $workspace '.local/native-prebuilt-cuda-build'
        Configure-Native $build @('-DSTEMORA_ENABLE_CUDA=ON', "-DSTEMORA_GGML_BINARY_DIR=$prebuilt")
    }
}
cmake --build $build --parallel $Parallel
Assert-Success 'Native infrastructure compilation failed.'
$bin = Join-Path $build 'bin'
if (-not $CpuOnly -and -not $BuildTensorBackend) { Copy-Item -Path "$prebuilt/ggml*.dll" -Destination $bin -Force }
if (-not $CpuOnly) {
    foreach ($pattern in @('cudart64_*.dll','cublas64_*.dll','cublasLt64_*.dll')) {
        $files = @(Get-ChildItem -LiteralPath "$CudaToolkitRoot/bin/x64" -Filter $pattern -File)
        if ($files.Count -eq 0) { $files = @(Get-ChildItem -LiteralPath "$CudaToolkitRoot/bin" -Filter $pattern -File) }
        if ($files.Count -eq 0) { throw "Missing CUDA runtime dependency: $pattern" }
        $files | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $bin -Force }
    }
}
if ($env:VCToolsRedistDir) {
    foreach ($directory in Get-ChildItem -LiteralPath "$env:VCToolsRedistDir/x64" -Directory | Where-Object Name -match 'CRT|OpenMP') {
        Get-ChildItem -LiteralPath $directory.FullName -Filter '*.dll' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $bin -Force }
    }
}
if (-not $SkipTests) {
    ctest --test-dir $build --output-on-failure
    Assert-Success 'Native infrastructure checks failed.'
}
# Replace only this task's generated distribution, after successful build/tests.
New-Item -ItemType Directory -Force $destination | Out-Null
$expected = Join-Path $workspace $(if ($CpuOnly) {'artifacts/native-cpu'} else {'artifacts/native'})
if ([IO.Path]::GetFullPath($destination) -ne [IO.Path]::GetFullPath($expected)) { throw 'Unexpected native output directory.' }
Get-ChildItem -LiteralPath $destination -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
Copy-Item -Path "$bin/*.dll","$bin/stemora-infra.exe" -Destination $destination -Force
$manifest = Get-ChildItem -LiteralPath $destination -File | ForEach-Object { [ordered]@{file=$_.Name;size=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} }
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 "$destination/runtime-manifest.json"
if (Get-ChildItem -LiteralPath $destination -File | Where-Object Name -match 'vulkan|ggml-vk|libc\+\+|libunwind') { throw 'An obsolete backend dependency entered the distribution.' }
Write-Host "Native CPU$(if(-not $CpuOnly){'/CUDA'}) infrastructure: $destination/stemora-infra.exe"
