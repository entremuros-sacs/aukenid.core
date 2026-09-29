#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Interactive chat against a local GGUF using Aukenid.Core. Requires .NET 10 installed and associated to Powershell 7.

.PARAMETER Gguf
    Path to a .gguf weights file.

.PARAMETER Prompt
    Optional first user message. After it is answered, the script keeps the conversation open.

.PARAMETER Template
    Optional chat template name passed to LlamaSharpChatEngine.UseTemplate.

.EXAMPLE
    ./Chat.ps1 -Gguf /path/to/model.gguf

.EXAMPLE
    ./Chat.ps1 -Gguf /path/to/model.gguf -Prompt "Hello"
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Gguf,

    [Parameter(Position = 1)]
    [string] $Prompt,

    [string] $Template
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::Version.Major -lt 10) {
    Write-Error "Aukenid.Core targets net10.0. This pwsh is running on .NET $([Environment]::Version)."
    exit 1
}

function Wait-Task {
    param([Parameter(Mandatory = $true)][object] $Task)
    if ($Task -is [System.Threading.Tasks.ValueTask] -or $Task.GetType().Name -eq 'ValueTask`1') {
        $Task = $Task.AsTask()
    }
    $Task.GetAwaiter().GetResult()
}

function Import-AukenidCore {
    param([Parameter(Mandatory = $true)][string] $BinDirectory)

    # A PowerShell scriptblock on AssemblyResolve re-enters while satellite
    # resources load (LLamaSharp during LoadModelAsync) and overflows the stack.
    if (-not ('Aukenid.Cli.AssemblyResolver' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Aukenid.Cli
{
    public static class AssemblyResolver
    {
        public static string Directory = "";
        static int _busy;

        public static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(name)
                || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                return null;
            }

            try
            {
                var path = Path.Combine(Directory, name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }
    }
}
'@
    }

    [Aukenid.Cli.AssemblyResolver]::Directory = $BinDirectory
    [System.AppDomain]::CurrentDomain.add_AssemblyResolve(
        [System.ResolveEventHandler][Aukenid.Cli.AssemblyResolver]::Resolve)

    foreach ($dll in Get-ChildItem -LiteralPath $BinDirectory -Filter '*.dll') {
        try {
            [void][System.Reflection.Assembly]::LoadFrom($dll.FullName)
        }
        catch {
            # Native or incompatible images stay on disk for the resolver to skip.
        }
    }
}

function Invoke-AukenidReply {
    param(
        [Parameter(Mandatory = $true)] $Engine,
        [Parameter(Mandatory = $true)] $Grounding,
        [Parameter(Mandatory = $true)] $Turns,
        [Parameter(Mandatory = $true)][string] $Text
    )

    Write-Host -NoNewline 'aukenid> '
    $result = Wait-Task ($Grounding.ReplyAsync($Engine, $Turns, $Text, [System.Threading.CancellationToken]::None))
    $plan = $result.Plan
    if ($plan.Any) {
        $tools = @()
        if ($plan.Web) { $tools += 'web' }
        if ($plan.Wiki) { $tools += 'wiki' }
        if ($plan.Scholar) { $tools += 'scholar' }
        Write-Host ($PSStyle.Foreground.FromRgb(140, 140, 140) + "[$($tools -join ', ')]" + $PSStyle.Reset)
    }
    # Mid steel blue (~#5894CC): readable on both light and dark terminals.
    Write-Host ($PSStyle.Foreground.FromRgb(88, 148, 204) + $result.Text + $PSStyle.Reset)
    if ($result.NoticeKey) {
        Write-Host "[$($result.NoticeKey)]"
    }
    Write-Host
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet is required on PATH.'
    exit 1
}

$ggufPath = $Gguf
if (-not [System.IO.Path]::IsPathRooted($ggufPath)) {
    $ggufPath = Join-Path (Get-Location) $Gguf
}
$ggufPath = [System.IO.Path]::GetFullPath($ggufPath)
if (-not (Test-Path -LiteralPath $ggufPath)) {
    Write-Error "GGUF file not found: $ggufPath"
    exit 1
}
if (-not $ggufPath.EndsWith('.gguf', [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "Not a .gguf file: $ggufPath"
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repoRoot 'src' 'Aukenid.Core.csproj'
& dotnet build $csproj --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$bin = Join-Path $repoRoot 'src' 'bin' 'Debug' 'net10.0'
Import-AukenidCore -BinDirectory $bin

[Aukenid.Core.Engine.LlamaSharpChatEngine]::PrepareRuntime()
$engine = [Aukenid.Core.Engine.LlamaSharpChatEngine]::new($ggufPath)
try {
    if ($Template) {
        $engine.UseTemplate($Template)
    }

    Write-Host "Loading $([System.IO.Path]::GetFileName($ggufPath)) ..."
    Wait-Task ($engine.LoadModelAsync($ggufPath, [System.Threading.CancellationToken]::None))
    $name = $engine.ActiveModelName
    if (-not $name) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($ggufPath)
    }
    Write-Host "Ready ($name). Type exit, quit, or end to stop."
    Write-Host

    $grounding = [Aukenid.Core.Services.GroundedChat]::CreateDefault()
    $turns = [System.Collections.Generic.List[Aukenid.Core.Engine.ChatTurn]]::new()
    $exitWords = [string[]]@('exit', 'quit', 'end')

    if ($Prompt) {
        Invoke-AukenidReply -Engine $engine -Grounding $grounding -Turns $turns -Text $Prompt.Trim()
    }

    while ($true) {
        Write-Host -NoNewline 'you> '
        $line = [Console]::ReadLine()
        if ($null -eq $line) {
            Write-Host
            break
        }

        $text = $line.Trim()
        if ($text.Length -eq 0) {
            continue
        }
        if ($exitWords -contains $text.ToLowerInvariant()) {
            break
        }

        Invoke-AukenidReply -Engine $engine -Grounding $grounding -Turns $turns -Text $text
    }
}
finally {
    $engine.Dispose()
}
