#Requires -Version 7.0
param(
    [string]$TlaHelpers,
    [string]$LeanBin,
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) "tsavorite-formal-$([guid]::NewGuid())"),
    [switch]$IncludeDeep
)

$ErrorActionPreference = 'Stop'
$specDir = Join-Path $PSScriptRoot 'tla'
$leanDir = Join-Path $PSScriptRoot 'lean'
$helpers = $null
$image = 'tsavorite-formal-tla:1.7.4'
if ($TlaHelpers) {
    $image = 'tla-tools:latest'
    $helpers = (Resolve-Path -LiteralPath $TlaHelpers).ProviderPath
    foreach ($script in @('Pcal.ps1', 'Sany.ps1', 'Run-TLC.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $helpers $script))) {
            throw "Missing TLA helper: $script"
        }
    }
}

function Invoke-Tla([string]$Tool, [string[]]$ToolArguments) {
    $output = & docker run --rm --mount "type=bind,source=$specDir,target=/spec" --workdir /spec $image java -cp /opt/tla2tools.jar $Tool @ToolArguments 2>&1
    $exitCode = $LASTEXITCODE
    $output
    if ($exitCode -ne 0 -or ($output -match 'Semantic errors:|Fatal errors:|Unrecoverable error:')) {
        throw "$Tool failed (exit code $exitCode)."
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if ($LeanBin) {
    $env:PATH = "$((Resolve-Path -LiteralPath $LeanBin).ProviderPath)$([IO.Path]::PathSeparator)$env:PATH"
}

Push-Location $leanDir
try {
    $version = & lake env lean --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch 'version 4\.19\.0,') {
        throw "Expected Lean 4.19.0; received: $version"
    }

    & lake build --wfail *> (Join-Path $OutputDirectory 'Lean.out')
    if ($LASTEXITCODE -ne 0) {
        throw "Lean build failed; inspect $OutputDirectory\Lean.out"
    }

    & lake env lean Tsavorite.lean *> (Join-Path $OutputDirectory 'Axioms.out')
    if ($LASTEXITCODE -ne 0) {
        throw "Lean axiom audit failed; inspect $OutputDirectory\Axioms.out"
    }
}
finally {
    Pop-Location
}

if (-not $helpers) {
    & docker build --tag $image --file (Join-Path $PSScriptRoot 'Dockerfile.tla') $PSScriptRoot *> (Join-Path $OutputDirectory 'DockerBuild.out')
    if ($LASTEXITCODE -ne 0) {
        throw "TLA Docker build failed; inspect $OutputDirectory\DockerBuild.out"
    }
}

$hash = & docker run --rm $image sha256sum /opt/tla2tools.jar
if ($LASTEXITCODE -ne 0 -or $hash -notmatch '^936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88\s') {
    throw 'Expected the pinned TLA+ 1.7.4 jar used by the Docker helpers.'
}

foreach ($module in @('Core', 'Epoch', 'Checkpoint')) {
    if ($helpers) {
        & (Join-Path $helpers 'Pcal.ps1') -SpecDir $specDir -Module $module *> (Join-Path $OutputDirectory "$module-Translate.out")
        & (Join-Path $helpers 'Sany.ps1') -SpecDir $specDir -Module $module *> (Join-Path $OutputDirectory "$module-Parse.out")
    }
    else {
        Invoke-Tla 'pcal.trans' @("$module.tla") *> (Join-Path $OutputDirectory "$module-Translate.out")
        Invoke-Tla 'tla2sany.SANY' @("$module.tla") *> (Join-Path $OutputDirectory "$module-Parse.out")
    }
}

$checks = @(
    @{ Module = 'Core'; Config = 'CoreSmall'; Violation = $null },
    @{ Module = 'Core'; Config = 'CoreSingle'; Violation = $null },
    @{ Module = 'Epoch'; Config = 'EpochSmall'; Violation = $null },
    @{ Module = 'Checkpoint'; Config = 'CheckpointSmall'; Violation = $null },
    @{ Module = 'Core'; Config = 'CoreBroken'; Violation = 'DataRefinesMap' },
    @{ Module = 'Epoch'; Config = 'EpochBroken'; Violation = 'NoProtectedReclamation' },
    @{ Module = 'Checkpoint'; Config = 'CheckpointBroken'; Violation = 'CommittedIsDurable' }
)
if ($IncludeDeep) {
    $checks += @{ Module = 'Core'; Config = 'CoreTwoOps'; Violation = $null }
}

foreach ($check in $checks) {
    $log = Join-Path $OutputDirectory "$($check.Config).out"
    $failure = $null
    try {
        if ($helpers) {
            & (Join-Path $helpers 'Run-TLC.ps1') -SpecDir $specDir -Module $check.Module -Config $check.Config -Workers 2 *> $log
        }
        else {
            Invoke-Tla 'tlc2.TLC' @('-config', "$($check.Config).cfg", '-workers', '2', "$($check.Module).tla") *> $log
        }
    }
    catch {
        $failure = $_
    }

    $text = Get-Content -LiteralPath $log -Raw
    if ($check.Violation) {
        $expected = "Error: Invariant $($check.Violation) is violated."
        if (-not $failure -or -not $text.Contains($expected)) {
            throw "Negative control $($check.Config) did not fail with '$expected'; inspect $log"
        }

        Write-Output "$($check.Config): detected $($check.Violation) as expected."
    }
    else {
        if ($failure) {
            throw $failure
        }

        if (-not $text.Contains('No error has been found.') -or -not $text.Contains('Finished checking temporal properties')) {
            throw "Incomplete model check for $($check.Config); inspect $log"
        }

        Write-Output "$($check.Config): exhaustive safety and termination checks passed."
    }
}

Write-Output "Formal checks passed. Logs: $OutputDirectory"
