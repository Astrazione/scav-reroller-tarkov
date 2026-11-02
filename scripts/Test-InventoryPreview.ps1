param([string]$GameRoot = (Join-Path $PSScriptRoot '../..'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $GameRoot 'BepInEx/core/Mono.Cecil.dll')
$dll = Join-Path $PSScriptRoot '../src/ScavReroller.Client/bin/Release/net472/ScavReroller.Client.dll'
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Resolve-Path -LiteralPath $dll).Path)
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameRoot 'EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll'))

function Get-LocalIndex($instruction) {
    if ($instruction.OpCode.Name -match '^(?:st|ld)loc\.(\d)$') { return [int]$Matches[1] }
    if ($instruction.Operand -is [Mono.Cecil.Cil.VariableDefinition]) { return $instruction.Operand.Index }
    return -1
}

try {
    $viewer = $assembly.MainModule.Types | Where-Object Name -eq 'ScavInventoryPreview'
    $open = $viewer.Methods | Where-Object Name -eq 'Open'
    $calls = @($open.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
    $clone = @($calls | Where-Object { $_.Operand.FullName -eq 'EFT.Profile EFT.Profile::Clone()' })
    $show = @($calls | Where-Object { $_.Operand.FullName -like '*ItemUiContext::ShowPlayerEquipmentWindow*' })
    $visual = @($calls | Where-Object { $_.Operand.FullName -like '*Profile::GetVisualEquipmentState*' })
    if ($clone.Count -ne 1 -or $show.Count -ne 1 -or $visual.Count -ne 1) {
        throw 'Expected a single native clone, complete-equipment projection and window opening.'
    }
    $snapshotSlot = Get-LocalIndex $clone[0].Next
    if ($snapshotSlot -lt 0 -or (Get-LocalIndex $show[0].Previous) -ne $snapshotSlot) {
        throw 'Native equipment window must receive the detached snapshot, not the live profile.'
    }
    if ($visual[0].Previous.OpCode.Name -ne 'ldc.i4.0' -or
        (Get-LocalIndex $visual[0].Previous.Previous) -ne $snapshotSlot) {
        throw 'Inventory projection must retain the snapshot container contents.'
    }
    $viewerCalls = @($viewer.Methods | Where-Object HasBody | ForEach-Object {
        $_.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
            ForEach-Object { $_.Operand.FullName }
    })
    if ($viewerCalls -match '::set_AllProfiles\(|::set_Profile\(|RequestHandler::|Backend::Send|OfflineInventoryController::') {
        throw 'Preview must not replace session profiles or send inventory/server operations.'
    }

    $profile = $game.MainModule.Types | Where-Object FullName -eq 'EFT.Profile'
    $nativeClone = $profile.Methods | Where-Object Name -eq 'Clone'
    $nativeOperations = $nativeClone.Body.Instructions | ForEach-Object { $_.Operand }
    if (-not ($nativeOperations -match 'FullySearchedSearchController::Instance') -or
        -not ($nativeOperations -match 'ProfileDescriptor::.ctor') -or
        -not ($nativeOperations -match 'Profile::.ctor')) {
        throw 'Installed game profile clone no longer has the expected descriptor round trip.'
    }

    $plugin = $assembly.MainModule.Types | Where-Object Name -eq 'Plugin'
    $openButton = $plugin.Methods | Where-Object Name -eq 'OpenScavInventory'
    $buttonCalls = @($openButton.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
        ForEach-Object { $_.Operand.FullName })
    if (-not ($buttonCalls -match 'ClientBackendSession::get_ProfileOfPet')) {
        throw 'Preview button must obtain the current session scav on every click.'
    }
    $closeScreen = $plugin.Methods | Where-Object Name -eq 'SelectionScreenClosed'
    $closeCalls = @($closeScreen.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
        ForEach-Object { $_.Operand.FullName })
    if (-not ($closeCalls -match 'ScavInventoryPreview::Close')) {
        throw 'Leaving selection must close the inventory preview.'
    }
    'Passed: native window receives a detached, fully searched current scav; containers retained; no server/session writes; preview closes with selection.'
    'Offline IL/API checks only. Window appearance, item inspection and Escape/X handling require an in-game check.'
}
finally {
    $assembly.Dispose()
    $game.Dispose()
}
