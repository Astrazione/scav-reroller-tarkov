param([string]$GameRoot = (Join-Path $PSScriptRoot '../..'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $GameRoot 'BepInEx/core/Mono.Cecil.dll')
$dll = Join-Path $PSScriptRoot '../src/ScavReroller.Client/bin/Release/net472/ScavReroller.Client.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $dll).Path)

function Get-AllTypes($types) {
    foreach ($type in $types) {
        $type
        Get-AllTypes $type.NestedTypes
    }
}

try {
    $types = @(Get-AllTypes $assembly.MainModule.Types)
    $calls = @($types | ForEach-Object {
        $_.Methods | Where-Object HasBody | ForEach-Object {
            $_.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
                ForEach-Object { $_.Operand }
        }
    })
    # Reject the actual cause: retrying an expensive scene search when an
    # absent/destroyed menu cannot be cached. Also reject periodic scheduling.
    $forbidden = @($calls | Where-Object {
        ($_.DeclaringType.FullName -eq 'UnityEngine.Object' -and $_.Name -like 'FindObject*') -or
        ($_.DeclaringType.FullName -eq 'UnityEngine.Resources' -and $_.Name -eq 'FindObjectsOfTypeAll') -or
        $_.Name -eq 'InvokeRepeating'
    })
    if ($forbidden.Count -ne 0) { throw ('Polling/scene search found: ' + ($forbidden.FullName -join ', ')) }

    $plugin = $types | Where-Object FullName -eq 'ScavReroller.Client.Plugin'
    if (@($plugin.Methods | Where-Object Name -in @('Update','LateUpdate','FixedUpdate','OnGUI')).Count -ne 0) {
        throw 'Persistent plugin must not have frame/GUI callbacks.'
    }
    foreach ($methodName in @('UpdateMenu','DrawMenu','IsSideSelectionScreenOpen')) {
        $method = $plugin.Methods | Where-Object Name -eq $methodName
        $methodCalls = @($method.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
            ForEach-Object { $_.Operand.FullName })
        if ($methodCalls -match '::GetAssemblies\(|::GetFikaProfile\(|::GameWorldPresent\(|::FindType\(') {
            throw "Unexpected background reflection/world lookup in $methodName."
        }
    }

    foreach ($entry in @(@{Name='SelectionScreenShown';Value='ldc.i4.1'}, @{Name='SelectionScreenClosed';Value='ldc.i4.0'})) {
        $method = $plugin.Methods | Where-Object Name -eq $entry.Name
        $instructions = $method.Body.Instructions
        $setter = @($instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -like '*Behaviour::set_enabled*'
        })
        if ($setter.Count -ne 1 -or $setter[0].Previous.OpCode.Name -ne $entry.Value) {
            throw "Overlay enable/disable contract failed in $($entry.Name)."
        }
    }

    $overlay = $types | Where-Object FullName -eq 'ScavReroller.Client.MenuOverlay'
    if (@($overlay.Methods | Where-Object Name -in @('Update','OnGUI')).Count -ne 2) {
        throw 'Menu callbacks must belong to the separately enabled overlay.'
    }
    'Passed: no scene searches or timed polling; persistent plugin has no frame/GUI callbacks; menu lifecycle enables/disables the overlay.'
    'This is an offline IL regression check, not an in-game frame-time measurement.'
}
finally {
    $assembly.Dispose()
}
