# Reproducible, signed PawnIO ACPI EC module; no driver installation or hardware access.
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOutput=Join-Path $taskRoot 'plugin\bin\pawnio'
$taskModule=Join-Path $taskOutput 'LpcACPIEC.bin'
$taskModuleHash='C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906'
if(Test-Path -LiteralPath $taskModule) {
 if((Get-FileHash -LiteralPath $taskModule).Hash -ne $taskModuleHash){throw 'Unexpected existing ACPI EC module'}
} else {
 $taskCache=Join-Path $taskRoot 'artifacts\tooling\pawnio-modules-0.2.11'
 [IO.Directory]::CreateDirectory($taskCache)|Out-Null
 $taskArchive=Join-Path $taskCache 'release.zip'
 if(-not(Test-Path -LiteralPath $taskArchive)){Invoke-WebRequest -Uri 'https://github.com/namazso/PawnIO.Modules/releases/download/0.2.11/release_0_2_11.zip' -OutFile $taskArchive}
 if((Get-FileHash -LiteralPath $taskArchive).Hash -ne '43608CB89BC84247FEF1368A139013F7D043E17DB6D6C8DFC9B46BF0905A81F4'){throw 'PawnIO module release hash mismatch'}
 $taskExpanded=Join-Path $taskCache 'release'
 if(-not(Test-Path -LiteralPath $taskExpanded)){Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskExpanded}
 $taskSource=Join-Path $taskExpanded 'LpcACPIEC.bin'
 if((Get-FileHash -LiteralPath $taskSource).Hash -ne $taskModuleHash){throw 'PawnIO ACPI EC module hash mismatch'}
 [IO.Directory]::CreateDirectory($taskOutput)|Out-Null
 Copy-Item -LiteralPath $taskSource -Destination $taskModule
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'native\upstream\PawnIO-ACPIEC\COPYING') -Destination (Join-Path $taskOutput 'LpcACPIEC-COPYING.txt') -Force
Write-Output 'Verified PawnIO ACPI EC module 0.2.11.'
