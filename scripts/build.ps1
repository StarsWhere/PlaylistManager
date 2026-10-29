#requires -Version 7.0
[CmdletBinding()]
param([string]$GameRoot=$env:BEAT_SABER_DIR)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(!$GameRoot){throw 'Specify -GameRoot or BEAT_SABER_DIR.'}
$GameRoot=(Resolve-Path -LiteralPath $GameRoot).Path
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848'){throw 'This branch targets Beat Saber 1.45.2_28848.'}
$dependency=Join-Path $repo 'vendor/BeatSage'
$sharedProject=Join-Path $dependency 'scripts/shared_song_import/SongPackageIO.csproj'
if(!(Test-Path -LiteralPath $sharedProject)){throw 'Run git submodule update --init --recursive first.'}
$sharedOutput=Join-Path $repo '.tmp/shared/bin'
$sharedObj=Join-Path $repo '.tmp/shared/obj'
& dotnet build $sharedProject -c Release --nologo -v minimal ('-p:GameRoot='+$GameRoot) ('-p:RepoRoot='+$dependency) ('-p:OutputPath='+$sharedOutput+'/') ('-p:BaseIntermediateOutputPath='+$sharedObj+'/') '-p:DisableCopyToGame=true' '-p:DisableCopyToPlugins=true' '-p:DisableZipRelease=true'
if($LASTEXITCODE -ne 0){throw 'SongPackageIO source build failed.'}
$output=Join-Path $repo '.tmp/build/bin'
& dotnet build (Join-Path $repo 'PlaylistManager/PlaylistManager.csproj') -c Release --nologo -v minimal ('-p:GameRoot='+$GameRoot) ('-p:BeatSaberDir='+$GameRoot) ('-p:PlaylistDependenciesDir='+$GameRoot) ('-p:SongPackageIOPath='+(Join-Path $sharedOutput 'SongPackageIO.dll')) ('-p:OutputPath='+$output+'/') ('-p:BaseIntermediateOutputPath='+(Join-Path $repo '.tmp/build/obj')+'/') '-p:AppendTargetFrameworkToOutputPath=false' '-p:DisableCopyToGame=true' '-p:DisableCopyToPlugins=true' '-p:DisableZipRelease=true' '-p:SkipGitInfo=true' '-p:ImportDirectoryBuildTargets=false'
if($LASTEXITCODE -ne 0){throw 'PlaylistManager source build failed.'}
$dll=Join-Path $output 'PlaylistManager.dll'
$identity=[Reflection.AssemblyName]::GetAssemblyName($dll)
if($identity.Name -ne 'PlaylistManager'){throw 'Unexpected output assembly.'}
Write-Output $dll
