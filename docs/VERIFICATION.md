## 2026-11-01 managed player, round 1 (single-agent)

Architecture: [MANAGED_PLAYER.md](MANAGED_PLAYER.md). Source: `5b4ef49`,
`9126b86` and the docs commit that follows them. Windows, .NET 10.0.401 JIT
unless noted; one run each.

| Command | Result |
| --- | --- |
| `python tools/build_managed.py` (patcher, publicize, gen, engine, redirect, jit, census, selftest) | `BUILD_MANAGED OK`; publicize 960 types / 5,986 fields / 7,940 methods; gen 7,946 engine methods (6,113 icall, 1,833 P/Invoke); redirect 7,946 patched, 0 left native, 2 `GetFolderPath` calls redirected |
| `Cuphead360.Player census` | 8,438 types (0 failed), 60,077 methods compiled (0 failed) |
| `Cuphead360.Player selftest` | `SELFTEST PASS: 23 passed, 0 failed` |
| `dotnet publish -r wasi-wasm -p:CupheadWasm=true` (ILCompiler.LLVM 10.0.0-rc.1.26357.1, wasi-sdk 29) | exit 0; `Cuphead360.Player.wasm` 26,400,236 bytes, core module (`0061736d01000000`) |
| w2c2 `-f 2000` + Spire360 `w2c2_fix_tables.py` / `w2c2_imports.py` | 40 C files, 186 MB; 64 imports, 30 implemented by srt, 34 missing (all `wasi:http`/`wasi:io` streams) |
| `python -I tools/convert_objects.py --game game --out work/GameData` | 161 files, 178,326 objects, 0 errors, 38.4 s |
| `Cuphead360.Player run --frames 300` (`work/managed/run3.log`) | scene_start loaded (2 GameObjects, 7 components); original `CupheadStartScene.Awake` -> `Cuphead.Init` instantiates `Core/CupheadCore`; fields decoded 496, mismatched 0; 22 exceptions and 9 missing engine methods, all at unimplemented AssetBundle/audio/font/JsonUtility calls; no scene after scene_start |
| `%APPDATA%\Cuphead` SHA-256 before/after all runs | unchanged (3 files) |

Not covered: rendering, audio, input, later scenes, the XDK compile or any run
of the translated C, Xenia, hardware, performance, memory.

Interpreter-era note: the interrupted D1 r4 source was committed unvalidated
to branch `interpreter-d1-r4-wip`; its fixture build fails the T4 mutation
assertion. The interpreter is retired; the entries below are history.

## 2026-11-01 accepted CP1 coroutine and fault-ledger checkpoint

This checkpoint implements only the coroutine, ownership, join, and
fail-visible accounting prerequisite to the opening-flow work. It does not make
Root Pack playable and does not validate authored attacks, Animator behavior,
scene transitions, UI, audio, saves, Xenia, or hardware execution.

Opus and the independent Sol host accepted this bounded checkpoint on
2026-11-01. Sol rehashed the final source and both binaries, reviewed the
correction, and independently reproduced the distinct-root strict control
(exit 1) and unrelated-prefix control (exit 0). The external review completed
on the exact Opus5.5 subscription route with no API-key source or fallback.
The opening flow remains unaccepted.

Build-time source provenance is CP1 baseline
`694b9f0eac896dcf35a1f4c8843873485327df87` plus the dirty CP1 production
diff, on the host docs-only HEAD `bae43a8`. The final corrected source hashes
were `2A907E37C51B7EF74302E91BA7BF2BC6E3BA110AB5D74BB378AB1E881BE7C19D`
for `interpreter.cpp` and
`A831398705844A327791AD14A1577D22B8BAC015D041125541FAA5373206F897`
for `app.cpp`. The final binaries were:

| Target | Timestamp (UTC) | Size | SHA-256 |
| --- | --- | ---: | --- |
| Windows Release EXE | `2026-11-01T03:42:41.0988596Z` | 419,328 | `F4EC7E5F3C4F65B301F2E0BDD51DB7D8584484E26EC62389BD27C612025B64FD` |
| Xbox 360 Release XEX | `2026-11-01T03:43:08.9490884Z` | 696,320 | `E835924C02C9CAEDFA46815393EDA4E2E54DCDD6EB7DF000BA2105227566C8CB` |

Both final builds exited 0:

```text
scripts\build_win32.bat Release
scripts\build_xbox360.bat Release
```

Windows took 5.361 seconds and Xbox 360 took 13.050 seconds. The final source
files predate both binaries.

The scheduler gives every entry a stable ID and managed
`UnityEngine.Coroutine` handle, records the receiver as owner, links a raw child
before its synchronous first step, and rechecks the entry and all parent entries
after `MoveNext`, `get_Current`, and nested starts. `new WaitForSeconds(...)`
and `new WaitForSecondsRealtime(...)` now allocate a real wait object and store
the argument; the by-reference constructor path remains supported.

The 4096-entry capacity is a pilot limit on cumulative starts per process. It
does not reclaim entries. Exhaustion records a FAULT and rejects only the new
start. Same-pass work is limited to 131072 steps and synchronous recursion to
32 nested steps; exceeding either records a FAULT and rejects or ends only the
affected work. Guard failures and every subsequent refused step in an exhausted
pass are recorded against the offending entry's iterator/owner root. The
refused entries are faulted and ended, rather than deferred; the exhausted
marker also applies to outside-pass first steps until the next pass begins. The
earlier name filter, silent 512-step break, silent 1024-entry rejection,
unbounded synchronous recursion, and summary-order omission are absent.

The ledger stores bounded aggregate groups keyed by category, site, root
iterator, root owner and root callback, with a count and one sampled ancestry
chain. Strict prefixes are evaluated at FAULT time against the full live
managed stack and the root context, before storage or print limits. Prefixes use
`Type::Method`. A dropped or failed stored fault makes strict mode fail closed.
The summary counts every stored group but prints only the requested display
limit and reports `groups`, `observations`, `shown`, `dropped`, `push_fail`,
`alloc_loss`, and `ancestry_truncated` separately.

Chosen, unverified Unity semantics implemented and tested here:

- A yielded `Coroutine` joins to that handle and resumes the waiter after the
  handle completes. Stopping the waiter does not stop an independently started
  child.
- A child stopped before completion leaves its waiter suspended. This records
  an `orphaned-waiter` NOTE rather than cancelling the waiter.
- A raw nested `IEnumerator` is owned by its parent's owner and is recursively
  stopped with the parent.
- A completed child wakes its waiter in the same scheduler pass. Entries
  started during a pass do not execute a second time in that pass.
- An entry first stepped outside `UpdateCoroutines` gets its next step in the
  next frame's pass. The current pass's skip tag remains set even when another
  entry causes the scheduler's inner loop to advance again. This is
  doc-derived from the Unity 2017.4 ExecutionOrder page; no same-frame
  finish-order claim is made.
- Destroy/SetActive stops occur at the API call. Component destroy stops that
  owner; GameObject destroy/deactivation stops matching descendant ownership.
  `Behaviour.enabled=false` does not stop a coroutine.
- Inactive-owner `StartCoroutine` records a FAULT containing the owner type,
  GameObject and first inactive ancestor, then returns success with a null
  `Coroutine`, allowing the managed caller to continue.
- An explicit API `StartCoroutine` made after the current managed entry has
  stopped itself still proceeds under normal receiver, inactive-owner and
  capacity rules. It records an aggregated `start-after-self-stop` NOTE naming
  the cancelled iterator and receiver. Only a scheduler-originated raw child of
  a cancelled parent is suppressed; that suppression records a
  `raw-child-suppressed` NOTE.

The execution-order reference used only to choose the scheduler phase is the
Unity 2017.4 page
<https://docs.unity3d.com/2017.4/Documentation/Manual/ExecutionOrder.html>.
The host still needs to confirm the semantics above against Unity 2017.4.
`WaitForFixedUpdate` and `WaitForEndOfFrame` deliberately approximate the next
scheduler pass and produce a NOTE.

### T1-A: original wait path

```text
build\win32\Cuphead360.exe --command coroutinetest ^
  --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --headless ^
  --log work\restart-20261031\cp1-final-narrow-builder\final-original.log
```

Exit 0. Key output:

```text
[coroutinetest] original owner=UnityEngine.UI.Image ci=0
[coroutinetest] original child iterator=CupheadTime/<waitForSeconds_cr>c__Iterator0 owner=UnityEngine.UI.Image
[coroutinetest] PASS inactive owner records FAULT and returns null Coroutine
[coroutinetest] PASS original 0.5s wait incomplete on frame 30
[coroutinetest] PASS original 0.5s wait complete on frame 31 with state=-1
[coroutinetest] PASS Behaviour.enabled=false does not stop coroutine
[coroutinetest] PASS StopAllCoroutines(B) stops B-owned handle
[coroutinetest] PASS GameObject.SetActive(false) stops B-owned handle
[coroutinetest] original result=PASS
```

The stop operations use original MemberRef signatures through
`InvokeMemberRef`; the test records the active `UnityEngine.UI.Image` receiver,
not merely a C++ helper result. The original `new WaitForSeconds` path now
returns its allocated managed object and stores the float, rather than pushing
the constructor's void result.

### T1-B: join fixture

No suitable original parent iterator was found whose pre-yield path was free
of the required UI or Animator dependencies, so the clearly labelled synthetic
fallback is used. Build and run:

```text
tests\fixtures\build_cp1_fixture.bat
build\win32\Cuphead360.exe --command coroutinetest ^
  --metadata work\restart-20261031\cp1-final-narrow-builder\fixture\CP1CoroutineFixture.chm ^
  --headless ^
  --log work\restart-20261031\cp1-final-narrow-builder\final-core.log
```

Both commands exited 0. The test observes the fixture's `State` field and
managed scheduler entries:

```text
[coroutinetest] PASS fixture parent state=1 after synchronous first step
[coroutinetest] PASS fixture 0.5s wait incomplete on frame 30
[coroutinetest] PASS fixture handle completed 0.5s wait and parent resumed on frame 31
[coroutinetest] PASS stopping a finished handle is a NOTE no-op
[coroutinetest] PASS StopCoroutine(parent) leaves independent child running
[coroutinetest] PASS stopped child leaves parent suspended with orphaned-waiter NOTE
[coroutinetest] PASS stopping raw parent recursively stops its raw child
```

The extended fixture command was:

```text
build\win32\Cuphead360.exe --command coroutinetest-extended ^
  --metadata work\restart-20261031\cp1-final-narrow-builder\fixture\CP1CoroutineFixture.chm ^
  --headless ^
  --log work\restart-20261031\cp1-final-narrow-builder\final-extended.log
```

It exited 0 and verified outside-pass and mixed-entry timing, observed raw-child
ordering, faulted and stopped waiters, parent cancellation during a raw child's
first step, and a safe 33-entry recursive chain. Self-stop on the first and later
steps, and parent stop during the raw child's first step, each allowed the
explicit nested start exactly once; the nested coroutine completed while the
self-stopped entries remained frozen. The recursion control recorded
`coroutine scheduler::step-depth` against the distinct
`CP1GuardVictimIterator/<Run>` root. Its intentionally failing `MoveNext` was
never entered. The run then invoked a probe successfully. This is engine
semantics evidence only, not original-game correctness.

### T1-C: negative and strict controls

Seven temporary local reversions were rebuilt and run after the final ledger
structure was in place. Each was restored before the final binaries. Evidence
uses the `negative-*.log` names under `cp1-final-narrow-builder/`:

- Returning a null handle from the StartCoroutine shim made both original and
  fixture commands exit 1.
- Storing zero for the `WaitForSeconds` constructor made the frame-30 assertion
  fail and exit 1.
- Restoring the original summary index progression omitted one reversed-order
  group and exited 1.
- Restricting strict ancestry to the last 32 stored methods missed the
  outer-only match (`strict_matched=0`, exit 0 instead of the expected 1).
- Removing the post-`MoveNext` cancellation check marked a self-stopped iterator
  completed and exited 1.
- Reinstating suppression when `current_entry_` was stopped made the explicit
  nested starts disappear and exited 1 (`negative-f1-suppression-reinstated.log`).
- Linking a raw child only after its first synchronous step left it running
  after a parent-only stop and exited 1.
- Raising the depth cap above the finite 40-level fixture removed the scheduler
  FAULT and exited 1 with 41 starts.

Fault-time strict matching uses the full live stack. The distinguishing
outer-only fixture has more than 32 ancestors, reports `ancestors=32 truncated`,
but still matches `CP1LedgerSites::DeepOuterOnly` and exits 1. The reversed-order
fixture stores two groups and three observations; `FailZ` is sorted after
`FailA` and still matches:

The F2 guard control runs `coroutinetest-extended --strict-faults
CP1GuardVictimIterator`; it exits 1. The unrelated prefix exits 0. The depth
FAULT has `root_iterator=CP1GuardVictimIterator/<Run>`, where that type appears
at neither the FAULT site nor any live managed ancestor. The victim's
intentionally failing first `MoveNext` does not run. The per-pass budget path
uses the same root-first wrapper and aggregates each later refusal by the
offending root; reproducing 131072 finite steps is impractical in this fixture,
so that branch is verified by source review rather than a scene run.
Evidence: `final-f2-root-match.log` and `final-f2-root-nonmatch.log`.

```text
--strict-faults CP1LedgerSites::FailZ
[ledger] FAULT count=1 site=CP1LedgerSites::FailA
[ledger] FAULT count=2 site=CP1LedgerSites::FailZ
[ledger] summary groups=2 observations=3 shown=2 strict_matched=1
[shutdown] result=1

--strict-faults Unrelated::Prefix
[ledger] summary strict_matched=0
[shutdown] result=0
```

Distinct-group exhaustion stored exactly 8192 groups, observed one more dropped
fault, printed only one group, and failed closed:

```text
[ledger] summary groups=8192 observations=8193 shown=1 dropped=1
  push_fail=0 alloc_loss=0 strict_prefixes=1 strict_matched=0 strict_loss=1
[shutdown] result=1
```

Evidence: `final-ledger-positive.log`, `final-ledger-match.log`,
`final-ledger-nonmatch.log`, `final-ledger-deep.log`, and
`final-ledger-exhaust.log`.

### T2: Root Pack progress

One final narrow-correction run postdates both final Release builds. It uses 150
frames, enough to cover the accepted frame-24 wait release and frame-118 fault:

```text
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_veggies\scene.chs --levelplay --frames 150 ^
  --headless ^
  --log work\restart-20261031\cp1-final-narrow-builder\rootpack-150-final.log
```

Exit 0; duration 77.840 seconds; no process crash. The run emitted bounded
frame-0 through frame-149 progress rows for the `Level` and `VeggiesLevel`
coroutine entries, including iterator, owner, `$PC`, last yield, activity and
fault state. Key rows:

```text
[rootpack-wait] initial_state=1 initial_yield=UnityEngine.WaitForSeconds transition_delay=0.000000 computed_seconds=0.400000 computed_frames=24
[rootpack-progress] frame=0 iterator=Level/<startBattle_cr>c__Iterator2 owner=VeggiesLevel state=1 last_yield=UnityEngine.WaitForSeconds running=1 faulted=0
[rootpack-wait] state_changed initial=1 current=2 frame=24 last_yield=UnityEngine.WaitForSeconds running=1 faulted=0
[rootpack-progress] frame=120 iterator=Level/<startBattle_cr>c__Iterator2 owner=VeggiesLevel state=-1 last_yield=UnityEngine.WaitForSeconds running=0 faulted=1 fault_site=Level/<startBattle_cr>c__Iterator2::MoveNext
[levelplay] result=PASS
[ledger] summary groups=148 observations=9839 shown=32 dropped=0 push_fail=0 alloc_loss=0 ancestry_truncated=0 strict_loss=0
[rootpack-wait] state_hold=PASS initial_state=1 first_change_frame=24 computed_frames=24 startBattle_starts=1
[rootpack-chain] starts=104 faulted=1 parked_running=0 start_after_self_stop_notes=0 ledger_groups=148 cumulative_limit=4096
[rootpack] status=NOT_PLAYABLE blocked_on=Level/<startBattle_cr>c__Iterator2::MoveNext
```

The blocker is now derived from the earliest faulted coroutine in the
Level/VeggiesLevel chain. The old first-`Animator`-record-anywhere heuristic is
gone. The fault is `ldfld on null` at frame 118 after the real 0.4-second wait.
If no chain entry faulted, Root Pack reports the earliest stalled running entry's
wait kind and first relevant dependency. That stalled-poller fallback is specific
to this status report and is not generic deadlock detection.
The frame-24 and frame-118 rows match the accepted 600-frame run. This run
contains no `start-after-self-stop` NOTE, so its reported count is 0. No
didShoot/Shoot call, animation event, health write, teleport, or authored
attack injection was performed. The 4096 value is the cumulative-start pilot
limit; this run used 104 entries.

### T3: regressions

All cases below were run once from the final Windows binary:

| Case | Exit | Ledger/main result |
| --- | ---: | --- |
| Tutorial `--level` | 0 | PASS; 25 groups, 51 observations |
| Tutorial `--levelplay --frames 120` | 1 | 112 groups, 6808 observations; `OnlineInterfaceGog::UnlockAchievement` mismatch at frame 101 |
| Flower `--level` | 0 | PASS; 26 groups, 52 observations |
| Synthetic `--combat-test --frames 272` | 0 | PASS; 114 groups, 14576 observations; full damage sequence and final health 0 |

The frame-101 failures are an inherited interface-slot misdispatch. At IL PC 375
in `LevelPlayerMotor.OnGrounded` (method 15783), `callvirt` token `0x06003a9c`
declares method 15004, `OnlineInterface.SetStat`. Signature 2320 has three
explicit parameters, `PlayerId`, `string`, and `float`, plus HASTHIS bit `0x20`:
the authored call supplies 4 stack values including the receiver. That value is
derived from the package and call code; runtime does not log the count. The
interface method's numeric vtable slot is 23.

The tools assign interface and class virtual methods in separate slot spaces.
`InvokeVirtual` preserves the interface slot number but indexes the receiver
class's vtable. Class slot 23 selects method 15040,
`OnlineInterfaceGog.UnlockAchievement`, whose signature 2303 has two explicit
parameters, `PlayerId` and `string`, and therefore requires 3 values including
the receiver. The fault is emitted because the derived 4-value call reaches a
3-value callee. If arity happened to match, the same mechanism could silently
call the wrong method.

This is independent of CP1 scheduling and ledger code and is the subject of the
separate next D1 interface-dispatch checkpoint. The original operation requests
`SetStat`, not an achievement. No dispatch change, special native success,
argument padding, or `Achievement`-parameter claim is made here.

Flower `--level` was CP1-caused and is resolved: inactive-owner StartCoroutine
no longer aborts `FlowerLevel.Start`; it records the owner/GameObject/ancestor
FAULT and lets the caller continue. Flower levelplay then reaches the shared
inherited mismatch at frame 101 and also exposes the real-wait
`Level/<startBattle_cr>` fault at frame 118.

The combat predicate now passes. The former synchronous TweenValue/Color.get_gray
failure no longer aborts the test after the inactive StartCoroutine fix. A
nonterminal `UnityEngine.Color::Lerp` FAULT remains in `HitFlash::SetColor` and
is a next-checkpoint visual-shim gap, not combat evidence.

Logs are `t3-*.log` under `work/restart-20261031/cp1-final-narrow-builder/`.

## 2026-11-01 D1 r2 interface-dispatch checkpoint

Status: implementation and builder validation are complete; D1 acceptance
remains with the host/lead. This checkpoint replaces interface-owned numeric
slot indexing for in-assembly IL receivers with metadata-derived interface
mapping. It does not claim MethodImpl, variance, default-interface methods,
generic virtual methods, opening-flow completion, or gameplay acceptance.

Source hashes:

- `src/runtime/interpreter.cpp`:
  `47FEDA840C9AF841A9253C8353341EFC555B508EC41F0111607F283230A2A9C2`
- `src/runtime/interpreter.h`:
  `89607A2454C3FA6280F13778ED5BFF1083722DC4EE896BA27E2CDCF42F564DE9`
- `src/runtime/metadata.h`:
  `B10FDE171A837E870EFD907316ADF495401FFD9F96AF23C2762CB381F0F98930`
- `src/app/app.cpp`:
  `3F894907595E9EBCB455D81F29EBD693FE42CBE6CC811498EA9B1908EA5C7393`
- `tests/fixtures/D1DispatchFixture.cs`:
  `1F2BB6314243928262F9742F69CC930B5FFE2F4BE476C3702E40741DFEBB4BF0`
- `tests/fixtures/build_d1_fixture.bat`:
  `4FC1A5F1C3FA3B9900943B95C9725924A58E4C35554644EDB398881103A819D5`

Both Release artifacts postdate the final source edit:

| Artifact | Bytes | UTC timestamp | SHA-256 |
| --- | ---: | --- | --- |
| `build/win32/Cuphead360.exe` | 431616 | `2026-11-01T12:10:55.8389015Z` | `91DBF58DFAB67AD02FAC6C9629FA3B53909B0F6184A85798444AD1C7B63273DE` |
| `build/xbox360/default.xex` | 696320 | `2026-11-01T12:11:03.7185484Z` | `D400745846C2D0EBF28283B39BED2AB67B109677159DE316190FAB5BD98B4B6E` |

Builds:

```text
scripts\build_win32.bat Release
scripts\build_xbox360.bat Release
```

Both exited 0. Observed wall times were 6.123 s for Windows and 14.055 s for
Xbox 360. Xbox validation is build-only; no Xenia or hardware replay is
claimed.

### Mapping and dispatch rule

The runtime uses these bounds:

- A call is an interface call for an in-assembly MethodDef whose declaring type
  is an interface, or for a callvirt MemberRef whose declared TypeRef/TypeSpec
  appears in the receiver class chain's direct InterfaceImpl list after
  interface-base expansion only.
- Mapping origin is the most-derived class that directly declares the
  interface. A plain inherited implementation does not move the origin;
  re-declared implementation does.
- At the origin and then its IL bases, a matching explicit implementation wins
  over a public implicit implementation. Explicit and implicit matching both
  require the same exact, structurally compared signature.
- A direct or final implementation is invoked directly. An overridable virtual
  implementation is indexed through the receiver class's vtable using the
  matched method's own class slot. Interface slots are never used.
- MemberRef interface membership now uses the same class-chain origin search as
  MethodDef calls. This reaches an inherited IL `IEnumerator<int>` without
  treating ordinary external class MemberRefs as interface calls.
- Class-level `VAR` substitution is supported for closed generic interfaces.
  Multidimensional arrays, pointers, function pointers, custom modifiers,
  unresolved nested parameters, `MVAR`, and generic interface methods produce
  `interface dispatch::unsupported-signature` before any callee runs.
- Supported compound types and nested generic arguments are fully validated
  before equal-type-ID or substituted-VAR shortcuts. Array rank is part of
  closed-type identity.
- Unresolved, ambiguous, unsupported, and no-member cases fail closed. There is
  no raw-slot fallback, padding, IL rewrite, or native success shim.
- Resolution and failures are cached by receiver type, call kind, call id, and
  interface identity. Cache hits re-emit a failure under the current ledger
  root. No-origin failures now pass through the same bounded insertion path.
  Cache state and counters reset on Interpreter initialization and shutdown.

### Synthetic fixture

`tests\fixtures\build_d1_fixture.bat` exited 0 and produced a 25312-byte
`D1DispatchFixture.chm` with SHA-256
`541D2D6F459BBD6865472263CB3EAF731058DD1F4B289DAE7AFCDBDB0D89BBB4`.
The command below exited 0:

```text
build\win32\Cuphead360.exe --command dispatchtest --metadata work\restart-20261031\d1-builder\fixture\D1DispatchFixture.chm --headless --log work\restart-20261031\d1-builder\final-dispatchtest.log
```

The fixture asserts the method that actually ran using distinct markers:

- same-arity interface-slot collisions: 110 and 120;
- `M(int)`, `M(float)`, and `M(string)` overloads: 230, 231, and 232;
- hiding without re-declaration: 240;
- override below the mapping origin: 251;
- re-declaration: 261;
- interface call before a new virtual slot: 270, with ordinary class call
  272 on the new chain;
- inherited public implementation: 280 and a separately forced non-virtual
  metadata control at 990;
- inherited explicit implementation versus a derived public hider: 290;
- same-class explicit-plus-implicit and explicit-only cases: 300, 301, and
  310;
- interface inheritance: 320;
- closed game interface via `VAR`: 337;
- explicit closed-generic name normalization: 347;
- two receivers sharing one interface identity without cache aliasing: 401
  and 402;
- inherited external `IEnumerator<int>` consumed by `foreach`: 410, with two
  IL `MoveNext` calls and one IL `get_Current` call;
- native `List<int>` direct and foreach controls: 17 and 7;
- ordinary non-interface class override: 431;
- generic `MVAR` interface method: visible unsupported FAULT on both calls and
  zero callee executions.

Two nested controls also fail closed with visible unsupported-signature FAULTs
and zero side effects: a re-declared `IBox<int[,]>` implementation that
otherwise would select a rank-three overload, and an `IBox<List<int[,]>>`
implementation whose nested generic graph exceeds the supported bounds. The
fixture build clears csc's virtual-final promotion only for the synthetic
`D1ExplicitBase.M` metadata row so the public non-virtual rule is exercised
directly.

Observed fixture explicit names include `D1IExplicit.M`,
`D1IExplicitBox<System.Int32>.Unwrap`, and
`System.Collections.IEnumerator.get_Current`. Their normalized forms are
`D1IExplicit.M`, `D1IExplicitBox.Unwrap`, and
`System.Collections.IEnumerator.get_Current`. Original mcs metadata contains
both `System.Collections.Generic.IEnumerator<object>.get_Current` and
`System.Collections.IEnumerator.get_Current`; the first normalizes to
`System.Collections.Generic.IEnumerator.get_Current`, which remains distinct
from the non-generic interface. Exact signature checks still decide the match.

Final counters:

```text
resolved=22 cache_entries=24 cache_full=0 unresolved=0 ambiguous=0
unsupported_signature=4 external_il_redirections=5 external_base_native=0
```

The ledger contained three groups and four observations: both nested signature
faults plus two cached emissions for the generic method fault.

### Negative controls

Every temporary source reversion rebuilt successfully, made `dispatchtest`
exit 1, and restored `interpreter.cpp` to
`47FEDA840C9AF841A9253C8353341EFC555B508EC41F0111607F283230A2A9C2`.
Evidence is under `work/restart-20261031/d1-builder/negative-controls/`.

| Reversion | Exit | Distinguishing failure |
| --- | ---: | --- |
| raw interface slots | 1 | fixture markers select wrong/colliding methods |
| name-only matching | 1 | overloads become ambiguous/wrong |
| explicit lookup removed | 1 | implicit hiders replace explicit implementations |
| receiver removed from cache key | 1 | second alias receiver reuses first receiver's method |
| most-derived receiver search | 1 | `new` hider steals inherited interface mapping |
| virtual-only implicit candidates | 1 | public non-virtual 990 control is rejected |
| external detection via `is_interface` | 1 | inherited external iterator misses IL interception |
| `VAR` substitution ignored | 1 | closed generic 337/347 controls fail |

### CP1 preservation and original route

The compact CP1 set used the accepted `CP1CoroutineFixture.chm` unchanged:

```text
build\win32\Cuphead360.exe --command coroutinetest ... --log work\restart-20261031\d1-builder\cp1-core.log
build\win32\Cuphead360.exe --command coroutinetest-extended ... --log work\restart-20261031\d1-builder\cp1-extended.log
build\win32\Cuphead360.exe --command coroutinetest --metadata work\metadata\metadata.chm --scene work\extract_level\scene.chs ... --log work\restart-20261031\d1-builder\cp1-original.log
build\win32\Cuphead360.exe --command ledger-sites ... --log work\restart-20261031\d1-builder\cp1-ledger-positive.log
build\win32\Cuphead360.exe --command ledger-sites ... --strict-faults CP1LedgerSites::FailZ ...
build\win32\Cuphead360.exe --command ledger-sites ... --strict-faults Unrelated::Prefix ...
build\win32\Cuphead360.exe --command ledger-deep ... --strict-faults CP1LedgerSites::DeepOuterOnly ...
build\win32\Cuphead360.exe --command ledger-exhaust ... --strict-faults Never::Match ...
build\win32\Cuphead360.exe --command coroutinetest-extended ... --strict-faults CP1GuardVictimIterator ...
```

The normal runs exited 0. The matching strict and exhaust controls exited 1,
while the unrelated-prefix controls exited 0. The recursive guard stayed at
the finite limit 32 with `CP1GuardVictimIterator/<Run>` retained as the
offending root. The F2 victim body remained unrun; the 131072-step sibling path
is source-reviewed because a finite reproduction is impractical.

The same Release binary completed 120 frames with exit 0 on tutorial,
platforming 1-1F, and Flower. Each log resolves the OnGrounded call as:

```text
receiver=OnlineInterfaceGog interface=OnlineInterface method=SetStat
method_id=15043 mode=direct origin=OnlineInterfaceGog
```

This is package method 15043, with the four-value
`PlayerId,string,float` call shape including the receiver. It is not
`UnlockAchievement`; interface slot 23 and class slot 23 are no longer
interchanged.

| Log | Exit | Result |
| --- | ---: | --- |
| `t3-tutorial-levelplay-120.log` | 0 | PASS; max_x=406.7, min_y=-274.5, collisions=186 |
| `t3-platform1-levelplay-120.log` | 0 | PASS; max_x=296.7, collisions=153 |
| `t3-flower-levelplay-120.log` | 0 | PASS; max_x=375.0, collisions=194 |

### Final regressions

All five `--level` boot commands (tutorial, platforming 1-1F, Train, Frogs,
and Flower) exited 0 with `result=PASS`. Train and Frogs `--levelplay
--frames 120` also exited 0. Existing non-D1 Unity/native fault rows remain
visible and are not treated as success.

The 272-frame synthetic combat run exited 0. It retained the full damage
sequence (`firstHit=1`, `sameDealerBlock=1`,
`freshDealerIFrameBlock=1`, `postIFrameHit=1`, `lethal=1`,
`finalHealth=0`), the tutorial target transition from health 20 to -4, and the
unchanged `retry=UNPROVEN` result.

The single final Root Pack 150-frame run exited 0 with the same accepted CP1
chain behavior:

- initial wait state 1, `UnityEngine.WaitForSeconds`, first change at frame
  24, exactly 24 computed frames;
- the owned fault remains frame 118 at
  `Level/<startBattle_cr>c__Iterator2::MoveNext` with detail `ldfld on null`;
- 104 starts, 148 ledger groups, 9839 observations, one faulted entry, zero
  parked running entries, and zero start-after-self-stop NOTEs;
- status remains `NOT_PLAYABLE`; this is outside D1;
- dispatch counters were `resolved=15 cache_entries=10 cache_full=0
  unresolved=0 ambiguous=0 unsupported_signature=0
  external_il_redirections=0 external_base_native=1563`.

The no-origin case cannot be authored as typed C# because the compiler rejects
an interface call on a receiver that does not implement it. Its cached,
current-root failure path is therefore a source-review boundary. External-base
native classification is also source-reviewed and represented by nonzero
`external_base_native` counters in the original runs. No metadata format or
tool change was made.

Detailed commands, timings, probe logs, source/binary hashes, and the compact
evidence index are in `work/restart-20261031/d1-builder/`.

### Frame argument order control

The Windows parser now preserves an explicit `--frames N` regardless of its
position relative to `--motor`, `--motorplay`, or `--levelplay`. Both commands
below exited 1 because two frames cannot satisfy the existing tutorial movement
predicate, but both emitted only `frame=0` before the result:

```text
... --levelplay --frames 2 ...
... --frames 2 --levelplay ...
```

Evidence: `frame-order-mode-first.log` and `frame-order-frames-first.log`.

Known ledger omissions remain UI, layers, cameras, materials, and other shims
outside the explicit CP1 registry. The harness currently invokes `Awake` and
`Start` on inactive template objects; lifecycle/activation parity is a
next-checkpoint gap. A Windows PASS or Xbox compile is not Xbox gameplay,
Xenia, or hardware evidence.

## 2026-10-31 player-damage engine checkpoint (scope corrected)

This is a checkpoint for the port runtime, not normal tutorial gameplay and
not M1 completion. `TutorialLevel` has no authored attack which damages the
player; `--combat-test` intentionally calls the original
`DamageDealer.NewEnemy()` and `DamageDealer.DealDamage(player)` methods to
exercise the game's damage, receiver, stats, hit, and death code.

Windows build and run:

```text
scripts\build_win32.bat Release
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --combat-test ^
  --frames 272 --headless --screenshot work\m1_combat_checkpoint ^
  --probe-fields --log work\m1_checkpoint_combat.log
```

Observed process exit code:

```text
EXIT=0
```

Key output:

```text
[combat] probe begin playerHealth=3 targetHealth=20 targetCi=182
[combat] hit1-first invoke=1 returned=1.00 health=2
[combat] hit1-repeat-same-dealer invoke=1 returned=0.00 health=2
[combat] hit1-iframes-fresh-dealer invoke=1 returned=1.00 health=2
[combat] hit2-after-iframes invoke=1 returned=1.00 health=1
[combat] hit3-lethal invoke=1 returned=1.00 health=0
[combat] target health initial=20 final=-4 trackedInactive=1
[combat] sequence firstHit=1 sameDealerBlock=1 freshDealerIFrameBlock=1 postIFrameHit=1 lethal=1 finalHealth=0
PASS
[combat] single-player retry=UNPROVEN requestPropagated=0
[probe] health PlayerStatsManager.<Health>k__BackingField ci=918 value=0 active=0
[levelplay] ... result=PASS
```

Historical receiver diagnosis excerpts are preserved in
[`evidence/m1-player-damage-diagnosis.txt`](evidence/m1-player-damage-diagnosis.txt).
They show a non-null `PlayerDamageReceiver`, `CanTakeDamage=1`,
`DamageTypes.Player=true`, and initialized dealer timers. The excerpt's
`enabled=0 flags=1` fields are inconsistent with the current helper's enabled-bit
calculation; do not use that historical line as a current enabled-state oracle.
The investigation changed activity/enabled handling and float comparisons.
The lead's unresolved review findings are in
[`HANDOFF.md`](HANDOFF.md).

HUD evidence was captured from the same run:

- [`health3`](evidence/m1-hud.health3.png)
- [`health2`](evidence/m1-hud.health2.png)
- [`health1`](evidence/m1-hud.health1.png)
- [`health0`](evidence/m1-hud.health0.png)

The compact combat transcript is
[`evidence/m1-checkpoint-combat.txt`](evidence/m1-checkpoint-combat.txt).
The captures validate changing HUD rectangles, but also show major scene
rendering artifacts, including atlas-like character art in the death capture.
They do not validate visual gameplay parity.

Normal tutorial regression after the final stabilization:

```text
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --frames 60 ^
  --headless --log work\checkpoint_tutorial_play_fixed2.log

[levelplay] start=-410.0,-270.0 max_x=80.0 ... result=PASS
```

Tutorial `--level` passed before the final offline-achievement no-op. The Xbox
360 Release build also passes:

```text
scripts\build_xbox360.bat Release
Xbox 360 build OK: build\xbox360\default.xex
```

Original single-player retry is not verified. The probe invokes the original
`LevelGameOverGUI.Retry()` and reaches `SceneLoader.ReloadLevel()`, but the
current incomplete transition stack leaves `SceneLoader.SceneName` unset. The
checkpoint deliberately reports `UNPROVEN`; it does not reset health or use
player revive as a substitute. This must be completed through the normal
opening-flow scene loader and boss accept/reject work.

Audio remains a muted headless-stub path. Manual shoot input is now wired to
the existing Rewired action through keyboard `J` and Xbox right trigger, but
has not been interactively play-tested.

Not run before the requested checkpoint pause:

- full platforming 1-1F `--level` / `--levelplay` regression;
- Train, Frogs, Flower and Veggies boot/play regressions;
- real-hardware Xbox execution;
- normal new-game, house, map, dialogue, Root Pack, retry, victory, and map
  return flows.

## Historical enemy-only combat probe (superseded)

Implemented and tested the enemy-side combat loop on the Windows build:

- virtual-delegate dispatch for weapon overrides (`WeaponPeashot.fireBasic`),
- world-space transform position and persistent euler rotation,
- cloned prefab root parenting,
- effective `DamageReceiver.type` tags and `GetInstanceID`,
- real `Object.Destroy` deactivation,
- target-first collision resolution,
- `--combat-test` and `--probe-fields` development probes.

Build and run:

```
scripts\build_win32.bat Release
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --combat-test ^
  --frames 80 --headless --probe-fields ^
  --log work\m1_final_combat2.log
```

Observed (exit code 0):

```
[combat] enemy damage frame=12 result=0.00
[combat] enemy damage frame=40 result=0.00
[combat] enemy damage frame=68 result=0.00
[combat] tutorial target seen=1 active=0
[probe] health TutorialLevelDamagableBox.boxHealth ci=182 value=-4.000 active=0
[probe] health TutorialLevelDamagableBox.boxHealth ci=1009 value=0.000 active=0
[levelplay] start=3000.0,81.0 max_x=3008.2 ... collisions=169 result=PASS
```

Tutorial regression:

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --frames 120 --headless ^
  --log work\m1_final_reg.log
```

Observed:

```
[levelplay] start=-410.0,-270.0 max_x=938.7 ... result=PASS
```

Caveat at the time: the synthetic enemy-damage attempt returned `0.0` and
player health remained `3`. This historical result was superseded by the
player-damage engine checkpoint above and did not demonstrate retry.

## Train boss scene boot (2026-10-31)


Implemented the `System.Array.Copy` overloads used by boss timelines and
validated `scene_level_train`:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_train --out work\extract_train --add-level-resources

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_train\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_train\scene.chs --levelplay --frames 30 --headless
```

Observed:

```
[INFO] [level] result=PASS
[INFO] [levelplay] start=-330.0,-250.0 max_x=-85.0 max_y=-171.0 min_y=-283.3 collisions=74 result=PASS
```

Tutorial regression remains PASS. Boss attack/timeline behavior is still not
validated.

## Veggies boss scene boot (2026-10-31)

Added coroutine liveness validation and a temporary safety filter for known
unstable coroutine state machines. Extracted `scene_level_veggies` and ran:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_veggies --out work\extract_veggies --add-level-resources

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_veggies\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_veggies\scene.chs --levelplay --frames 30 --headless
```

Observed:

```
[INFO] [level] result=PASS
[INFO] [levelplay] start=-370.0,-240.0 max_x=655.0 max_y=-137.2 min_y=-249.5 collisions=483 result=PASS
```

The safety filter currently skips Veggies boss, homing-projectile, UI and
death-effect coroutines; the level boots and the player moves, but those
skipped behaviors remain a known parity gap.

## Flower boss scene boot (2026-10-31)

Added the generic `AbstractLevelProperties<TState,...>.CurrentState` getter
(array or list backed) and explicit most-derived `PartialInit` dispatch after
`Level.Awake`. This lets levels whose properties are created by `PartialInit`
(Flower) initialize their state machine.

Extracted and ran `scene_level_flower`:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_flower --out work\extract_flower --add-level-resources

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_flower\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_flower\scene.chs --levelplay --frames 30 --headless
```

Observed:

```
[INFO] [level] result=PASS
[INFO] [levelplay] start=-410.0,-195.0 max_x=375.0 max_y=-87.2 min_y=-199.5 collisions=693 result=PASS
```

Tutorial and Frogs regressions remain PASS after the change. Boss attacks and
phase logic are still not validated.

## Frogs boss scene boot (2026-10-31)

Fixed the content pipeline to skip Unity `Dictionary`/`HashSet` fields that
occupy no bytes in serialized MonoBehaviour payloads. This unblocked
`Level/Bounds.colliders` and, in turn, the derived `FrogsLevel` fields
(`tall`, `small`, `morphed`, `demonTrigger`).

Extracted and ran `scene_level_frogs`:

```
python tools\extract_scene.py --game game\Cuphead_Data ^
  --scene scene_level_frogs --out work\extract_frogs --add-level-resources

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_frogs\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_frogs\scene.chs --levelplay --frames 30 --headless
```

Observed:

```
[INFO] [level] result=PASS
[INFO] [levelplay] start=-410.0,-240.0 max_x=225.9 max_y=-132.2 min_y=-244.5 collisions=653 result=PASS
```

This validates boss-scene extraction, level startup and the movement/play
harness on a second boss scene. Boss attacks, phase logic, damage, UI/audio and
full-scene rendering are still not validated.

## Synthetic weapon firing and collision milestone

The level-play harness now invokes the original
`LevelPlayerWeaponManager.StartBasic` once when synthesized shooting starts,
and the runtime coroutine scheduler runs the original
`AbstractLevelWeapon.<fireWeapon_cr>` state machines. Observed on the tutorial
scene:

```
[INFO] [levelplay] instances objects=2403 projectiles=114
[INFO] [levelplay] start=-410.0,-270.0 max_x=1327.6 ... collisions=16224 result=PASS
```

Projectile clones are created and trigger collisions are dispatched. This is
not yet a validated shooting slice: the spawned projectiles sit at the weapon
prefab position, do not reach the target, and the tutorial target's
`boxHealth` remains `20.0`. The real Rewired shoot-button path, projectile
movement/despawn, damage delivery and enemy reaction are still open. The
harness trigger is a temporary adapter, not parity with the Windows game.


The extractor now reads Unity `m_TagString` for scene and prefab objects and
writes a parallel tag-offset table into `CHSC` (header word 24). The runtime
binds those tags to `GameObject.tag` / `Component.CompareTag`, so the original
`AbstractCollidableObject.checkCollision` routes walls, ground, enemies, player
and projectile hits by their real Unity tags instead of name heuristics.

Re-extracted tutorial and platforming 1-1F scenes after this change. Windows
results:

```
tutorial --level PASS
tutorial --levelplay 120 frames: start=-410,-270 max_x=1327.6 collisions=204 PASS
tutorial instances: objects=1844 projectiles=24
platforming --level PASS
platforming --levelplay 100 frames: start=-520,-93 max_x=-209.7 collisions=104 PASS
```

The projectile count includes the 24 projectile templates emitted with the
player prefab graph; end-to-end projectile damage/despawn and enemy behavior
are still not validated.

## Animator default-state playback

`--levelplay` now loads the extracted `anim.chs` alongside the scene and
advances the default Animator state of every extracted controller before the
native player sprite bridge runs. On the tutorial scene this loads 84
controllers / 304 clips / 6687 frames and drives 91 animator instances over 216
sprite targets. Animator script parameters/transitions and animation events are
still not implemented; only default states play.

## Xbox 360 / Xenia revalidation (2026-10-31)

Rebuilt with `scripts\build_xbox360.bat Release`, copied the fresh
`build\xbox360\default.xex` into `work\xenia_test`, and ran the XEX under
Xenia Canary. Observed:

```
[INFO] [boot] event=command name=play
[INFO] [metadata] loaded game:\GameData\metadata.chm types=7417 ...
[INFO] [scene] loaded game:\GameData\scene.chs objects=186 components=277 ...
[INFO] [unity] bound 186 gameobjects, 277 components
[INFO] [play] player object=185 component=276 idle=5 run=54 jump=22 dash=16
[INFO] [display] D3D9 1280x720 initialized
[INFO] [play] scene lifecycle LevelPlatform.Awake=1 PlayerDeathEffect.Start=1
[INFO] [play] frame=30 state=Jump pos=220.5,-165.2 vel=489.5,1190.0 grounded=0
[INFO] [play] screenshot=game:\play.45.bmp
[INFO] [play] frame=90 state=Run pos=753.7,-185.0 vel=359.5,0.0 grounded=1
[INFO] [play] screenshot=game:\play.100.bmp
```

Fresh `work\xenia_test\play.45.bmp` and `play.100.bmp` were written. This
validates the newest runtime on the Xbox 360 target for the native tutorial
play slice. It is not a full-level or boss validation, and the XEX does not
yet run the complete original scene graph on Xbox.

`UnityEngine.Object.Instantiate` now clones the template hierarchy and its
managed components, remaps internal GameObject/Transform/component references,
repairs cloned `LevelPlayerController` and `_basePlayer` wiring, and runs
`Awake`. Instance overloads apply position/parent arguments. Player-prefab
creation still reuses the extracted template until player cloning is validated
end-to-end; projectile/enemy prefabs use the clone path.

The level-play harness now discovers active runtime-created components and
starts/ticks their original `Start`/`Update` methods. Inactive extracted
prefab templates are no longer ticked as gameplay.

Dictionary `KeyCollection`/`ValueCollection` enumerators are implemented, so
`PlayerManager.get_Count()` and projectile damage-multiplier lookups complete.

Validation after this increment (Windows):
- tutorial `--level` PASS
- tutorial `--levelplay` PASS (`start=-410,-270 max_x=1327.6`)
- tutorial `--stack` PASS
- platforming `--level` PASS
- platforming `--levelplay` PASS (`start=-520,-93 max_x=174.2 min_y=-95.5`)

Cloned projectile components now reach `Awake`; their `BasicProjectile.Awake`
still fails on collision/tag setup, so projectile damage/despawn is the next
gameplay blocker.


All results below were produced locally against the user's GOG installation.
They are development-verification results, not a claim that the full game is
playable.

## Latest Windows runtime pass (2026-10-31)

```
scripts\build_win32.bat Release

build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --level --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --levelplay --frames 120 --headless
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_level\scene.chs --stack --headless
```

Observed:
```
[INFO] [level] TutorialLevel.Awake PASS
[INFO] [level] TutorialLevel.Start PASS
[INFO] [level] result=PASS
[INFO] [levelplay] player LevelInit invoked
[INFO] [levelplay] player LevelStart invoked
[INFO] [levelplay] start=-410.0,-270.0 max_x=1327.6 max_y=-159.7 min_y=-274.5 result=PASS
[INFO] [stack] player LevelInit PASS
[INFO] [stack] player LevelStart PASS
[INFO] [stack] result=PASS
```

The latest runtime fixes in this pass:

- `Enum.GetValues`/`EnumUtils.GetValues<T>()` + `constrained.` enum
  `ToString` now agree, so weapon-prefab dictionaries populate instead of
  silently remaining empty.
- Dictionary keys compare boxed enum values numerically.
- `GameObject.activeInHierarchy` walks the parent chain instead of failing.
- The `--levelplay` harness invokes the original player `LevelInit` before
  `LevelStart`, which creates weapon prefabs/loadouts.
- The `--stack` harness repairs serialized `_basePlayer` references after
  construction, so original child-component `LevelInit` paths run.

The synthetic `--motor` harness still reports a failure in the motor's
serialized component wiring; the real `--levelplay` path passes. `--play`
requires a scene extracted with `--add-player`.

## Build

```
scripts\build_win32.bat Release
scripts\build_xbox360.bat Release
```

Both complete successfully:
- `build\win32\Cuphead360.exe`
- `build\xbox360\default.xex`

## Windows original-IL logic

```
build\win32\Cuphead360.exe --logic --metadata work\metadata\metadata.chm --log build\logs\final_logic.log
```

Observed:
```
[INFO] [logic] PlayerInventory.ctor weapons=2 expect=2 PASS
[INFO] [logic] VelocityManager.Total=(100.000,-202.500) expect=(100.000,-202.500) PASS
[INFO] [logic] result=PASS
```

## Windows serialized fields + lifecycle

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_tutorial_full4\scene.chs --unity --log build\logs\unity_lifecycle.log
```

Observed:
```
[INFO] [unity] bound 185 gameobjects, 276 components
[INFO] [unity] ResetLocalTransforms after pos=0.00,0.00 scale=1.00,1.00 PASS
[INFO] [unity] AnimationHelper.Update PASS
[INFO] [unity] AnimationHelper serialized speed=1.00 layer=1 PASS
[INFO] [unity] DeferredAudioSource string 'MUS_Tutorial' PASS
[INFO] [unity] CupheadGlyph array length=6 expect=6 PASS
[INFO] [unity] PlayerDeathEffect PPtr PASS
[INFO] [unity] lifecycle LevelPlatform.Awake PASS
[INFO] [unity] lifecycle PlayerDeathEffect.Start PASS
[INFO] [unity] lifecycle AnimationHelper.Update PASS
[INFO] [unity] TutorialLevel nested intro=object child=1 PASS
[INFO] [unity] AbstractMonoBehaviour.Awake PASS
```

## Xbox 360 in Xenia

The same logic and Unity-bridge modes were run from the XEX in Xenia Canary
(big-endian PowerPC). The captured `game:\cuphead360.log` showed the same
PASS lines, including:

```
[INFO] [unity] bound 185 gameobjects, 276 components
[INFO] [unity] lifecycle LevelPlatform.Awake PASS
[INFO] [unity] lifecycle PlayerDeathEffect.Start PASS
[INFO] [unity] lifecycle AnimationHelper.Update PASS
[INFO] [unity] TutorialLevel nested intro=object child=1 PASS
```

The scene-viewer mode also loaded `scene.chs`/`anim.chs`, initialized D3D9,
rendered the real tutorial scene, and wrote `game:\frame.bmp` under Xenia.
The D3D9 host-window contents could not be captured through PrintWindow
(black capture); the framebuffer screenshot is the validated render output.

Rebuilt XEX with the expanded metadata format (MethodSpec MemberRefs, FieldRVA
static-array data and enum constants) and the playable tutorial slice:

```
[INFO] [boot] platform=xbox360 _MSC_VER=1600
[INFO] [boot] big_endian=1
[INFO] [metadata] loaded game:\GameData\metadata.chm types=7417 ...
[INFO] [unity] bound 186 gameobjects, 277 components
[INFO] [play] result=PASS start=0.0,-180.0 max_x=900.0 max_y=21.7 min_y=-185.0 jumped=1 landed=1 dashes=2
```

The XEX wrote `game:\play.45.bmp`, `play.100.bmp`, `play.200.bmp`, and
`play.final.bmp`. This verifies the expanded runtime on big-endian PowerPC.

## Playable tutorial slice

Windows:

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_play\scene.chs --play --frames 240 --headless ^
  --screenshot work\extract_play\play --log build\logs\play8.log
```

Observed:

```
[INFO] [play] player object=185 component=276 idle=5 run=54 jump=22 dash=16
[INFO] [play] scene lifecycle LevelPlatform.Awake=1 PlayerDeathEffect.Start=1
[INFO] [play] frame=30 state=Jump ...
[INFO] [play] frame=90 state=Run ...
[INFO] [play] frame=180 state=Idle ...
[INFO] [play] result=PASS ... jumped=1 landed=1 dashes=2
```

Xbox 360 XEX in Xenia produced the same state trace and a PASS result, and
wrote `game:\play.45.bmp`, `play.100.bmp`, `play.200.bmp`, and
`play.final.bmp` (real framebuffer renders of the tutorial scene with
Cuphead).

## Original player stack harness

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_prefab\scene.chs --stack --headless ^
  --log build\logs\stack.log
```

Observed:

```
[INFO] [stack] Level.Current set, PlayersCreated=true
[INFO] [stack] get_motor ok=1 type=LevelPlayerMotor PASS
[INFO] [stack] player Awake PASS
[INFO] [stack] prefab component Awakes 11/11
[INFO] [stack] player LevelInit PASS (last_error=0)
[INFO] [stack] player LevelStart PASS
[INFO] [stack] result=PASS
```

This verifies generic `GetComponent<T>`, the entire player-prefab `Awake`
chain (motor, animation, collider, weapon, damage, stats, camera), and the
original `LevelPlayerController.LevelInit`/`LevelStart`. The dialogue database
is an explicit empty scaffold for now; the real asset still needs extraction.
The next gameplay step is running the original motor/input loop.

## Original player motor

```
build\win32\Cuphead360.exe --metadata work\metadata\metadata.chm ^
  --scene work\extract_prefab\scene.chs --motor --headless ^
  --log build\logs\motor.log
```

Observed:

```
[INFO] [stack] motor moved start=0.00,-180.00 max_x=816.67 end=816.67,-211.26 min_y=-213.29 PASS
[INFO] [stack] result=PASS
```

The motor moves and stays on the tutorial level with synthesized input.
`--motorplay` adds rendering/camera follow; its logic passes, but the
captured camera-follow frames were black because the prefab scene's drawable
content is the oversized static player art rather than the tutorial level.
This is not a validated visual slice.

## Latest status (current runtime)

- Windows `--logic`, `--stack`, `--motor` and the native `--play` tutorial
  slice all PASS with the current runtime; `--play` reports
  `result=PASS ... jumped=1 landed=1 dashes=2`.
- `tools\extract_scene.py --add-level-resources` now resolves cross-file
  PPtrs and includes `LevelResources`, `CupheadCore`, `LevelGUI`, `LevelHUD`,
  `LevelAudio`, `PlayerJoinEffect`, the dialogue UI and the real player
  prefab. The generated tutorial scene binds 1515 GameObjects and 1993
  components.
- The original `--level` flow now PASSes on Windows:
  `TutorialLevel.Awake PASS`, `TutorialLevel.Start PASS`, `result=PASS`.
- `--levelplay` drives the real player/motor created by `Level.Start` with
  synthesized input, camera follow and software rendering:
  `start=-410.0,-270.0 max_x=804.9 max_y=-270.0 min_y=-274.5 result=PASS`.
  Screenshots: `work\extract_level\levelplay.45.bmp`,
  `levelplay.100.bmp`, `levelplay.200.bmp`.
- The level-play harness now also runs a bounded scene-wide `Awake`/`Start`
  lifecycle and ticks original non-UI gameplay scripts. Shooting spawns
  projectiles whose original `Update` methods execute. `Object.Instantiate`
  is still template reuse rather than true cloning, and damage/target
  destruction are not yet validated.
- The content pipeline now loads all shared assets, follows scene-authored
  external PPtrs and maps `RectTransform` references. The first normal
  platforming level (`scene_level_platforming_1_1F`) was converted with 2346
  GameObjects / 4619 sprites; `PlatformingLevel.Awake` and
  `PlatformingLevel.Start` both PASS on Windows.
- Generic runtime support now includes nested method-generic substitution,
  `Comparison<T>` delegates, `List<T>.Sort` and a GOG achievement-sync no-op.
- `--levelplay` on the platforming level still fails: the player falls
  through the start area. Platform/ground collider runtime support and the
  full UI/audio/Animator stack remain missing, so no normal level is playable
  yet.
- Sprite world size now uses the cropped sprite rect instead of the atlas
  rect, fixing the oversized character art in both the native play slice and
  the original-level play harness.
- The Xbox 360 XEX is rebuilt from the current runtime. Full 240-frame play
  revalidation under Xenia is recorded below once complete.
- This milestone validates original level startup and a movement/rendering
  slice. It does **not** validate complete gameplay: weapons, enemies,
  bosses, damage, parry/super, hazards, real Animator state, audio playback,
  UI rendering, saves, dialogue, menus, cutscenes, transitions and DLC remain
  absent. See `docs/STATUS.md` for the honest breakdown.

This verifies the runtime/metadata/scene/script foundation, original
`TutorialLevel.Awake`/`Start`, and the beginning of the original player
lifecycle. It does **not** verify any complete level, boss, menu, save,
audio, or DLC gameplay.
