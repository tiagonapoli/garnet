# herd7 checks of the emitted machine code

The TLA+ specs in `../tla/` check the *algorithm*, and `../Litmus/` checks
whether the bug shows up on the x86 machine you happen to be sitting at.
Neither says anything about what the JIT actually emits, and neither can say
anything at all about AArch64 unless you own an AArch64 machine.

This folder closes that gap. It takes the real RyuJIT output for `LightEpoch`
on both architectures, reduces it to the handful of shared-memory instructions
that carry the ordering, and checks those against the vendors' own memory
models with [herd7](https://github.com/herd/herdtools7).

```
docker build -t garnet-lightepoch-herd libs/storage/Tsavorite/cs/test/epoch/formal-verification/herd7
docker run --rm garnet-lightepoch-herd
```

The container exits non-zero if any result differs from its expectation. As
with the TLA+ runner, an optional substring selects which rows to run:

```
docker run --rm garnet-lightepoch-herd arm64-refresh
```

To run outside Docker you need `herd7` on `PATH` (`opam install herdtools7`):

```
bash libs/storage/Tsavorite/cs/test/epoch/formal-verification/herd7/run.sh
```

The tests are control/fix pairs, and the pairing is the argument — a lone
`Never` is also what a mis-encoded test returns. To see a pair as a diff and run
both halves together:

```
./run.sh --pairs              # every pair and the one instruction between them
./run.sh --pair arm64-announce-sb
```

`MODEL.md` has the same table with the reasoning.

## Results

| Test | Expected | Meaning |
| --- | --- | --- |
| `x86-announce-sb-main` | Sometimes | the shipped bug, under x86-TSO |
| `x86-announce-sb-fixed` | Never | closed by the CAS-carried announce |
| `arm64-announce-sb-main` | Sometimes | same bug, under `aarch64.cat` |
| `arm64-announce-sb-fixed` | Never | closed by the CAS-carried announce |
| `x86-refresh-mp-main` | Never | load-load, so x86 was never exposed |
| `x86-refresh-mp-fixed` | Never | the acquire load is free on x86 |
| `arm64-refresh-mp-main` | Sometimes | **ARM-only bug**: `CurrentEpoch` is read by a plain `LDP` |
| `arm64-refresh-mp-fixed` | Never | closed by `LDAPR` |
| `arm64-release-plainstore` | Sometimes | counterfactual: `STR` in place of the `STLR` |
| `arm64-release-fixed` | Never | `STLR` orders the handover |
| `arm64-release-loadstore-main` | Sometimes | **ARM-only bug**: the slot clear can precede the dereference |
| `arm64-release-loadstore-fixed` | Never | `STLR` is ordered after the dereference |
| `x86-release-loadstore-main` | Never | TSO preserves Load→Store |
| `x86-composed-main` | Sometimes | the whole sequence, unfixed |
| `x86-composed-fixed` | Never | the whole sequence, fixed |
| `arm64-composed-main` | Sometimes | the whole sequence, unfixed |
| `arm64-composed-fixed` | Never | the whole sequence, fixed |

Each `Never` row is paired with a row that must be violated, for the same
reason the TLA+ suite pairs its rows: a suite that cannot detect the bug it
reports absent has established nothing.

The last four rows are the ones that matter most. Everything above them is a
single hazard shape studied in isolation, which is how the shapes are
*understood* but not on its own an argument that the program is correct. The
composed rows run `Acquire` → `ProtectAndDrain` → critical section → `Release`
against a full reclaimer, with every memory access of the reduced listing
present, and say that no execution of the fixed sequence frees an object under
a live reader.

`memory-ordering-bugs-found.md` explains each finding, including the two that
only exist on AArch64, the earlier claim this pass retracts, and a false
positive the first composed encoding produced.

## Layout

| Path | What it is |
| --- | --- |
| `jit/x86.asm`, `jit/arm64.asm` | verbatim RyuJIT FullOpts dumps of the epoch operations as they stand in this repository |
| `jit/reduced/*.reduced.asm` | the same code cut down to what herd7 can parse |
| `REDUCTION.md` | every removal and substitution, and why none of them can change the result |
| `MODEL.md` | the protocol the tests encode and what each `exists` clause means operationally |
| `memory-ordering-bugs-found.md` | the findings, split by architecture |
| `litmus/` | the herd7 tests |
| `capture/` | the harness that produced the dumps |
| `run.sh`, `Dockerfile` | the matrix runner |

The raw dumps are committed deliberately. herd7 is only evidence about the code
that ships today — a future JIT may emit something else — so the reduced
listings have to be auditable against the thing they were reduced from.

## Regenerating the dumps

The dumps record the code as it stands in this repository. The `*-main` litmus
tests, which are the controls, are that same instruction stream with the claim
CAS moved back onto `threadId` and the announce left as a plain store — the
one-instruction difference is spelled out in each of those tests rather than
carried as a second dump.

### Without an ARM machine (Docker + NativeAOT)

`LightEpoch` is its own project (`src/epoch/Garnet.LightEpoch.csproj`), so the
harness can lift the whole thing out of any git ref and compile it standalone.
NativeAOT's ILC hosts the same RyuJIT the runtime does and cross-targets, which
is what lets one x64 host emit the AArch64 listings:

```powershell
docker build -t garnet-lightepoch-disasm capture
docker run --rm -v "${PWD}\..\..\..\..\..\..\..\..:/repo:ro" -v "${PWD}\jit:/out" `
    garnet-lightepoch-disasm
```

The single optional argument is the git ref to capture, defaulting to `HEAD`.

Two things worth knowing about that image:

- ILC compiles methods **in parallel onto one stdout**, which interleaves lines
  from different listings. `Disasm.csproj` passes `--parallelism 1`; without it
  the output looks plausible and is silently garbage.
- The disassembly is written during ILC codegen, *before* the native link, so a
  cross-link failure still leaves a complete listing. `capture.sh` therefore
  keys success off the presence of `; Assembly listing for method`, not the
  publish exit code.

**Fidelity caveat.** NativeAOT is the same JIT but not the same surrounding
codegen: no tiering, different statics and helper access, and `[ThreadStatic]`
`Metadata.threadId` in particular is reached differently than under the runtime
JIT. The memory-ordering-relevant instructions — the CAS, `ldar`/`ldapr`,
`stlr`, `dmb`, and the plain loads and stores on the epoch table — should be
identical, which is all the litmus tests reduce from. The committed dumps were
taken with the **runtime JIT** (below); do not silently mix the two in one file.

A higher-fidelity alternative, at the cost of emulation speed, is to run the
ordinary apphost under `docker --platform linux/arm64` via QEMU, which gives
actual runtime-JIT AArch64 output.

### With the runtime JIT (how the committed dumps were taken)

`capture/` is a standalone console app that news up a `LightEpoch` and drives
it. The epoch operations are wrapped in `[MethodImpl(NoInlining)]` methods
(`OpResume`, `OpSuspend`, `OpProtectAndDrain`, `OpBumpCurrentEpoch`) so each one
gets its own listing while everything inside it still inlines as it would at a
real call site — without those wrappers the whole thing collapses into `Main`.

Build Release and run the **apphost** (not `dotnet X.dll` — the environment
variables below leak into the CLI muxer otherwise):

```powershell
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_TieredPGO         = '0'
$env:DOTNET_ReadyToRun        = '0'
$env:DOTNET_JitDisasmDiffable = '1'
$env:DOTNET_JitDisasm         = 'OpResume OpProtectAndDrain OpSuspend BumpCurrentEpoch ComputeNewSafeToReclaimEpoch'
.\bin\Release\net10.0\Disasm.exe 2>&1 | Out-String | Set-Content variant.asm
```

Then replace `jit/<arch>.asm` with the result, keeping the header banner.

Two things that cost time the first go round:

- `DOTNET_JitDisasm` matches **bare method names, space-separated**. The
  `Class:Method` form silently matches nothing.
- `DOTNET_JitDisasmSummary=1` lists what was actually compiled and under what
  name. Reach for it the moment a filter produces no output.

`DOTNET_JitDisasmDiffable=1` replaces addresses and large immediates with
`0xD1FFAB1E`, which is what makes the committed dumps stable enough to diff
across runs.

The AArch64 sections in `jit/arm64.asm` were taken on an Azure
`Standard_D8ps_v5` (Ampere Altra) running Ubuntu 24.04 arm64 with the .NET 10
SDK — any AArch64 machine with the same SDK will do. Same environment
variables, and set `DOTNET_ROOT` if the SDK is not in the default location.

## Scope, honestly

herd7 checks emitted instructions against the *architecture's* memory model.
That is a different and weaker contract than the .NET memory model, and it says
nothing about codegen the JIT might produce tomorrow. What it does give you is
the one thing the other two layers cannot: a check of real AArch64 instructions
against Arm's own model, which is how A2 in `memory-ordering-bugs-found.md` —
a hole no x86 stress test could ever expose — was found.
