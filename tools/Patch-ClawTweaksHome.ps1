param([Parameter(Mandatory=$true)][string]$InputFile,[Parameter(Mandatory=$true)][string]$OutputFile)
$ErrorActionPreference='Stop'
$taskCecil=Join-Path $PSScriptRoot '..\artifacts\tooling\ilspycmd\tools\net8.0\any\Mono.Cecil.dll'
Add-Type -Path $taskCecil
$taskModule=[Mono.Cecil.ModuleDefinition]::ReadModule($InputFile)
try {
 if ((Get-FileHash -LiteralPath $InputFile).Hash -ne 'DBE62A3EA97A6DC30F069ADCE3D16BB5AE2BBFD0A44CF09AD45D18C7CF141088') { throw 'Unexpected ClawTweaks helper build; patch cancelled.' }
 $taskType=$taskModule.Types | Where-Object FullName -eq 'XboxGamingBarHelper.Settings.GameBarFirstSlot'
 if (-not $taskType) { throw 'GameBarFirstSlot type missing.' }
 $taskFlag='GameBarFirstSlot_PreserveHome'
 if ($taskType.Methods.Body.Instructions.Operand -contains $taskFlag) { throw 'Helper already patched.' }
 $taskRelease=$taskType.Methods | Where-Object Name -eq 'Release'
 $taskGetter=$taskRelease.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.GenericInstanceMethod] -and $_.Operand.Name -eq 'TryGetValue' -and $_.Operand.GenericArguments[0].FullName -eq 'System.Boolean' } | Select-Object -First 1 -ExpandProperty Operand
 if (-not $taskGetter) { throw 'Boolean setting accessor missing.' }
 foreach ($taskName in @('Take','PrimeOurWidget','HandInputToOurWidget')) {
  $taskMethod=$taskType.Methods | Where-Object Name -eq $taskName
  if (@($taskMethod).Count -ne 1) { throw "Unexpected method: $taskName" }
  $taskBody=$taskMethod.Body
  $taskLocal=[Mono.Cecil.Cil.VariableDefinition]::new($taskModule.TypeSystem.Boolean)
  $taskBody.Variables.Add($taskLocal); $taskBody.InitLocals=$true
  $taskIl=$taskBody.GetILProcessor()
  $taskOriginal=$taskBody.Instructions[0]
  if ($taskName -eq 'Take') {
   $taskOriginal=$taskBody.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.Name -eq 'GameBarHomeSetting' -and $_.Operand.Name -eq 'TryRead' } | Select-Object -First 1
   $taskBranch=$taskOriginal.Next
   while ($taskBranch -and $taskBranch.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Brfalse_S -and $taskBranch.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Brfalse) { $taskBranch=$taskBranch.Next }
   if (-not $taskBranch) { throw 'Home setting guard missing.' }
   $taskTarget=$taskBranch.Operand
  } else { $taskTarget=$taskOriginal }
  $taskGuard=[Collections.Generic.List[Mono.Cecil.Cil.Instruction]]::new()
  $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Ldstr,$taskFlag))
  $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Ldloca,$taskLocal))
  $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Call,$taskGetter))
  if ($taskGetter.ReturnType.FullName -ne 'System.Void') { $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Pop)) }
  $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Ldloc,$taskLocal))
  if ($taskName -eq 'Take') { $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Brtrue,$taskTarget)) }
  else {
   $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Brfalse,$taskOriginal))
   if ($taskName -eq 'PrimeOurWidget') { $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)) }
   $taskGuard.Add($taskIl.Create([Mono.Cecil.Cil.OpCodes]::Ret))
  }
  if ($taskName -eq 'Take') {
   foreach ($taskInst in $taskBody.Instructions) { if ($taskInst.Operand -eq $taskOriginal) { $taskInst.Operand=$taskGuard[0] } }
  }
  foreach ($taskInst in $taskGuard) { $taskIl.InsertBefore($taskOriginal,$taskInst) }
  # Added instructions can exceed short branch ranges; widen every affected branch.
  foreach ($taskInst in $taskBody.Instructions) {
   if ($taskInst.OpCode.OperandType -eq [Mono.Cecil.Cil.OperandType]::ShortInlineBrTarget) {
    $taskLong=$taskInst.OpCode.Code.ToString() -replace '_S$',''
    $taskField=[Mono.Cecil.Cil.OpCodes].GetField($taskLong)
    if (-not $taskField) { throw "No long branch opcode: $taskLong" }
    $taskInst.OpCode=$taskField.GetValue($null)
   }
  }
 }
 $taskModule.Write($OutputFile)
 Write-Output "Patched opt-in Home preference: $OutputFile"
} finally { $taskModule.Dispose() }
