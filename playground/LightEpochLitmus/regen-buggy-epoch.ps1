# Copyright (c) Microsoft Corporation.
# Licensed under the MIT license.

# Regenerates BuggyLightEpoch.cs from LightEpoch.cs as it stands on the given ref (default
# origin/main), so the control the litmus runs against does not silently drift from upstream.
#
#   pwsh playground/LightEpochLitmus/regen-buggy-epoch.ps1 [-Ref origin/main]

[CmdletBinding()]
param(
    [string] $Ref = 'origin/main',
    [string] $Path = 'libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.cs'
)

$ErrorActionPreference = 'Stop'

$repo = git rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0) { throw 'not inside a git repository' }

$src = (git show "${Ref}:${Path}") -join "`r`n"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($src)) { throw "could not read ${Ref}:${Path}" }

$src = $src -replace 'namespace Tsavorite\.core', 'namespace Tsavorite.epoch.litmus'
$src = $src -replace 'using System\.Threading;', "using System.Threading;`r`nusing Tsavorite.core;"
$src = $src -replace 'public sealed unsafe class LightEpoch : IEpochAccessor', 'internal sealed unsafe class BuggyLightEpoch : IEpochAccessor, IDisposable'
$src = $src -replace 'public LightEpoch\(\)', 'public BuggyLightEpoch()'
$src = $src -replace 'nameof\(LightEpoch\)', 'nameof(BuggyLightEpoch)'
$src = $src -replace 'Utility\.Murmur3\(', 'Murmur3.Hash('
$src = $src -replace 'LightEpoch instances', 'BuggyLightEpoch instances'
$src = $src -replace '<see cref="LightEpoch', '<see cref="BuggyLightEpoch'

$header = @"
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

// ---------------------------------------------------------------------------------------------
// GENERATED - do not edit. Produced by regen-buggy-epoch.ps1 from $Path on $Ref.
//
// A copy of LightEpoch as it stands on main, kept here so the litmus harness can be pointed at
// the unfixed algorithm and the violation counts compared side by side. It is a control, not a
// second implementation: nothing outside this playground references it, and it is expected to
// FAIL the soak on x86-64.
//
// The two differences that matter, both in the slot-claim/announce path:
//   * the slot is claimed by CAS-ing threadId, and the epoch is then announced with a plain
//     store, so the announce sits in the store buffer with no StoreLoad fence behind it;
//   * Release() clears localCurrentEpoch before threadId.
// ---------------------------------------------------------------------------------------------

"@

$hooks = @'


        /// <summary>
        /// The epoch announced in epoch table slot <paramref name="entry"/>, or 0 if the slot is free.
        /// Mirrors the test hook of the same name on the fixed epoch so the harness can drive both.
        /// </summary>
        internal long AnnouncedEpochAt(int entry) => (*(tableAligned + entry)).localCurrentEpoch;
'@

$body = $src.Substring($src.IndexOf('using System;')).TrimEnd()
$split = $body.LastIndexOf("`n    }")
if ($split -lt 0) { throw 'could not find the class closing brace' }

$out = $header + $body.Substring(0, $split) + $hooks + $body.Substring($split) + "`r`n"
$dest = Join-Path $repo 'playground/LightEpochLitmus/BuggyLightEpoch.cs'
Set-Content -Path $dest -Value $out -Encoding utf8

Write-Host "wrote $dest"
