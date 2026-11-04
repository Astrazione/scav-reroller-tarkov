param([string]$GameRoot = (Join-Path $PSScriptRoot '../..'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::Version.Major -lt 10) { throw 'Run this server contract test in PowerShell on .NET 10 or later.' }

function Test-NativeSuccess($response) {
    # EFT HTTPTransportManager.ExtractResponseResult rejects any non-null errmsg,
    # including "". String.IsNullOrEmpty would miss the regression from 0.1.4.
    return $response.err -eq 0 -and $null -eq $response.errmsg
}

$previousBug = '{"err":0,"errmsg":"","data":{"ok":true}}' | ConvertFrom-Json
if (Test-NativeSuccess $previousBug) { throw 'The previous invalid envelope was accepted by the regression check.' }

Add-Type -Path (Join-Path $GameRoot 'BepInEx/core/Mono.Cecil.dll')
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameRoot 'EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll'))
try {
    $http = $game.MainModule.Types | Where-Object FullName -eq 'HTTPTransportManager'
    $extract = $http.Methods | Where-Object Name -eq 'ExtractResponseResult'
    $checks = @($extract.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'errmsg' -and
        $_.Next.OpCode.Name -like 'brtrue*'
    })
    if ($checks.Count -eq 0) { throw 'Installed game no longer has the expected strict errmsg check.' }
}
finally { $game.Dispose() }

$runtimePath = (Resolve-Path -LiteralPath (Join-Path $GameRoot 'SPT_Runtime')).Path
$resolver = [System.ResolveEventHandler] { param($sender, $eventArgs)
    $path = Join-Path $runtimePath ($eventArgs.Name.Split(',')[0] + '.dll')
    if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::LoadFrom($path) }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    # Invoke only the compiled pure response formatter, not a server, generator,
    # profile store or endpoint. No player data is loaded or changed.
    $dll = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../src/ScavReroller.Server/bin/Release/net10.0/ScavReroller.Server.dll')).Path
    $assembly = [Reflection.Assembly]::LoadFrom($dll)
    $handler = $assembly.GetType('ScavReroller.Server.ScavRerollerHandler', $true)
    $reply = $handler.GetMethod('Reply', [Reflection.BindingFlags]'Static,NonPublic')
    $count = 0
    foreach ($accepted in @($true, $false)) {
        foreach ($message in @('Generated', 'Generation rejected', '', "Quotes: `" and newline`ntext")) {
            $text = [string]$reply.Invoke($null, [object[]]@($accepted, $message))
            $parsed = $text | ConvertFrom-Json
            if (-not (Test-NativeSuccess $parsed)) { throw 'Compiled server response triggers a native backend error.' }
            if ($parsed.ok -ne $accepted -or $parsed.data.ok -ne $accepted -or
                $parsed.message -ne $message -or $parsed.data.message -ne $message) {
                throw 'Legacy and native clients receive different logical results.'
            }
            $count++
        }
    }
    "Passed $count compiled response cases for native and legacy clients; previous empty-errmsg regression rejected."
}
finally { [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver) }
