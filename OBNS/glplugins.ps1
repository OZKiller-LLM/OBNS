# OBNS: makes the input device plugins' variants for the OpenGL and native menus (OBNS/GLFORMS.md).
#
# Upstream's input device plugins show their settings in Windows Forms windows, which can't open on 64-bit macOS, and most
# build those forms when they load. So each plugin is also compiled, from its sources in the build's mirror, with
# System.Windows.Forms replaced by OBNS's controls (OpenBve.GLForms, in OpenBve.exe), into Data\InputDevicePlugins.GL under
# the same file name. Only the plugin's window changes. The game takes these copies where it uses those menus.
param(
    [Parameter(Mandatory = $true)][string]$Mirror,
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][string]$Program,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$plugins = 'DefaultDisplayPlugin', 'DenshaDeGoInput', 'KatoInput', 'SanYingInput', 'ZuikiInput'
$target = Join-Path $Program 'Data\InputDevicePlugins.GL'
New-Item -ItemType Directory -Force $target | Out-Null

foreach ($plugin in $plugins) {
    $source = Join-Path $Mirror "source\InputDevicePlugins\$plugin"
    $work = Join-Path $Output $plugin
    New-Item -ItemType Directory -Force $work | Out-Null
    [xml]$project = Get-Content -Raw (Join-Path $source "$plugin.csproj")
    $rootNamespace = @($project.Project.PropertyGroup.RootNamespace | Where-Object { $_ })[0]

    $wanted = @{}
    foreach ($file in Get-ChildItem $source -Recurse -File | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $copy = Join-Path $work $relative
        $wanted[$copy] = $true
        New-Item -ItemType Directory -Force (Split-Path $copy) | Out-Null
        if ($file.Extension -eq '.cs') {
            $text = [System.IO.File]::ReadAllText($file.FullName)
            $text = $text -replace 'System\.Windows\.Forms', 'OpenBve.GLForms'
            $text = $text -replace 'System\.ComponentModel\.ComponentResourceManager', 'OpenBve.GLForms.ComponentResourceManager'
            # The interface in OpenBveApi still names Windows Forms' type; no owner is needed in the menu's own loop.
            $text = $text -replace '\bvoid Config\(IWin32Window owner\)', 'void Config(System.Windows.Forms.IWin32Window owner)'
            $text = $text -replace '\.ShowDialog\(owner\)', '.ShowDialog()'
            if (-not (Test-Path $copy) -or [System.IO.File]::ReadAllText($copy) -ne $text) {
                [System.IO.File]::WriteAllText($copy, $text, (New-Object System.Text.UTF8Encoding($true)))
            }
        }
        elseif ($file.Extension -in '.resx', '.cfg', '.rules') {
            Copy-Item $file.FullName $copy -Force
        }
    }

    # Everything the plugin was built against is beside the program, already built.
    $references = New-Object System.Collections.ArrayList
    foreach ($name in 'OpenBve.exe', 'OpenBveApi.dll', 'OpenTK.dll', 'LibUsbDotNet.LibUsbDotNet.dll', 'HidSharp.dll', 'Newtonsoft.Json.dll') {
        foreach ($candidate in @((Join-Path $Program $name), (Join-Path $Program "Data\InputDevicePlugins\$name"))) {
            if (Test-Path $candidate) {
                [void]$references.Add("    <Reference Include=`"$([System.IO.Path]::GetFileNameWithoutExtension($name))`"><HintPath>$candidate</HintPath><Private>false</Private></Reference>")
                break
            }
        }
    }

    $embedded = (Get-ChildItem $source -Recurse -File -Include *.cfg, *.rules | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object {
            "    <EmbeddedResource Include=`"$($_.FullName.Substring($source.Length + 1))`" />"
        }) -join "`r`n"

    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <AssemblyName>$plugin</AssemblyName>
    <RootNamespace>$rootNamespace</RootNamespace>
    <LangVersion>12</LangVersion>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <WarningLevel>0</WarningLevel>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <NoWarn>`$(NoWarn);CS0067;CS0108;CS0162;CS0168;CS0169;CS0219;CS0414;CS0618;CS0649;CS1591;MSB3277;NU1701;CA1416</NoWarn>
    <OutputPath>$target</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
    <GenerateDependencyFile>false</GenerateDependencyFile>
    <AutoGenerateBindingRedirects>false</AutoGenerateBindingRedirects>
    <DebugType>none</DebugType>
    <EnableDefaultEmbeddedResourceItems>true</EnableDefaultEmbeddedResourceItems>
    <GenerateResourceUsePreserializedResources>false</GenerateResourceUsePreserializedResources>
    <Optimize>true</Optimize>
  </PropertyGroup>
  <ItemGroup>
$($references -join "`r`n")
    <Reference Include="System.Windows.Forms" />
    <Reference Include="System.Drawing" />
    <Reference Include="System.Xml" />
    <Reference Include="System.Xml.Linq" />
    <Reference Include="System.Core" />
    <Reference Include="Microsoft.CSharp" />
$embedded
  </ItemGroup>
</Project>
"@
    $csprojPath = Join-Path $work "$plugin.csproj"
    if (-not (Test-Path $csprojPath) -or [System.IO.File]::ReadAllText($csprojPath) -ne $csproj) {
        [System.IO.File]::WriteAllText($csprojPath, $csproj, (New-Object System.Text.UTF8Encoding($false)))
    }

    & dotnet build $csprojPath -c $Configuration -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "The menu's variant of $plugin failed to build" }
}

Get-ChildItem $target -File | Where-Object { $_.Extension -ne '.dll' -or [System.IO.Path]::GetFileNameWithoutExtension($_.Name) -notin $plugins } | Remove-Item
Write-Host "Input device plugins for the OpenGL and native menus: $(@(Get-ChildItem $target -Filter *.dll).Count) in $target"
