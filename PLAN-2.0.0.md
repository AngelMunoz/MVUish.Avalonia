# MVUish.Avalonia 2.0.0 — Implementation Plan

Status: ready for review
Scope: close the gap to modern Elmish (v4/v5 subscription design), add Avalonia-native
lifecycle features, fix issue 6, update docs and templates, cut 2.0.0.
Source of truth for the Elmish side: `E:\elmish` (branch `v5.x`, HEAD `3280e26`, release notes 5.0.2).
Source of truth for the Avalonia side: `E:\Avalonia` (12.0.5 — same version the library targets).

## 1. Background

Issue 6 reports that `Program.runWith` discards the subscription disposable and the update
mailbox has no stop path. Research showed this is one symptom of a larger fact: MVUish 1.0
implements Elmish v3 subscription semantics (subscribe once with the initial model, never
re-evaluate, no teardown). Elmish replaced that model in v4.0.0 and has kept it through v5:

- `Sub<'msg> = (SubId * Subscribe<'msg>) list`, `SubId = string list`.
- `subscribe` is a pure function of the model, re-evaluated after **every** update.
- A diff engine reconciles the active set against the requested set by ID: stops removed,
  starts added, warns on duplicates.
- `withTermination` gives the loop an exit message; termination stops all subs and calls
  a final-model handler.
- `runWithDispatch` lets callers serialize dispatch across threads.

The v5.x clone contains no further subscription changes after v4 (5.0.0 was Task/ValueTask),
so "the most modern sub design" and "the v4 design" are the same design. We port it as-is.

## 2. Locked decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Subscription model | Full Elmish v4/v5 keyed subs. `Sub<'Msg>` becomes `(SubId * Subscribe<'Msg>) list`. Breaking; covered by migration guide. |
| 2 | Subscribe input | Snapshot only: `Subscribe: 'Model -> Sub<'Msg>`, re-evaluated per update. New `Sub.fromObservable` bridges any `IObservable<'Msg>` as one keyed sub. No stream parameter on `Subscribe`. |
| 3 | Update loop | Keep the `MailboxProcessor` + `Dispatcher.UIThread.InvokeAsync` design. Add a `Stop` control message, a terminated flag, and teardown. No synchronous ring-buffer rewrite. |
| 4 | Avalonia extras | All three: routed-event subs, visual-tree lifetimes, stream-completion teardown. |

## 3. Target public API

### 3.1 Types (breaking)

```fsharp
type Dispatch<'Msg>  = 'Msg -> unit                                  // unchanged
type Cmd<'Msg>       = Dispatch<'Msg> -> (string * exn -> unit) -> unit  // unchanged

type SubId           = string list                                   // new
type Subscribe<'Msg> = Dispatch<'Msg> -> IDisposable                  // renamed shape of old Sub
type Sub<'Msg>       = (SubId * Subscribe<'Msg>) list                // breaking

type Program<'Arg, 'Model, 'Msg, 'View> = {
  Init: 'Arg -> 'Model * Cmd<'Msg>
  Update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>
  View: IObservable<'Model> -> Dispatch<'Msg> -> 'View
  Subscribe: 'Model -> Sub<'Msg>
  OnError: (string * exn) -> unit
  Termination: ('Msg -> bool) * ('Model -> unit)                     // new field, safe default
}
```

The Program record stays public (as today) to limit blast radius; docs recommend the
`withX` functions.

### 3.2 Sub module (breaking changes marked)

```fsharp
Sub.none                         : Sub<'Msg>                          // [] now
Sub.batch : seq<Sub<'Msg>>       -> Sub<'Msg>                        // List.concat
Sub.map   : idPrefix:string      -> ('A -> 'Msg) -> Sub<'A> -> Sub<'Msg>   // BREAKING: gains prefix
Sub.ofId  : id:string            -> Subscribe<'Msg> -> Sub<'Msg>     // new sugar: [[id], start]

Sub.interval      : id:string -> TimeSpan -> (unit -> 'Msg) -> Sub<'Msg>        // BREAKING: gains id
Sub.fromEvent     : id:string -> add:(EventHandler<'E> -> unit)
                  -> remove:(EventHandler<'E> -> unit) -> map:('E -> 'Msg) -> Sub<'Msg>  // BREAKING: gains id
Sub.fromObservable: id:string -> IObservable<'Msg> -> Sub<'Msg>                 // new (issue 6)
Sub.fromRoutedEvent: id:string -> target:#Interactive
                  -> routedEvent:RoutedEvent<'E> -> map:('E -> 'Msg) -> Sub<'Msg>       // new (Avalonia)
Sub.fromRoutedEventWith: id:string -> target:#Interactive -> routedEvent:RoutedEvent<'E>
                  -> routes:RoutingStrategies -> handledEventsToo:bool
                  -> map:('E -> 'Msg) -> Sub<'Msg>                                     // full control
Sub.untilDetached : Visual -> Sub<'Msg> -> Sub<'Msg>                             // new (Avalonia)
```

Internal (ported from Elmish, adapted): `SubId.toString`, `Fx.warnDupe/tryStop/tryStart/stop/change`,
`NewSubs.calculate`, `diff`. Names and behavior follow `E:\elmish\src\sub.fs`.

### 3.3 Program module

```fsharp
Program.mkProgram / mkSimple                                          // unchanged signatures
Program.withSubscription : ('Model -> Sub<'Msg>) -> Program -> Program    // replaces (unchanged)
Program.mapSubscription : (('Model -> Sub<'Msg>) -> 'Model -> Sub<'Msg>) -> Program -> Program  // new
Program.withTermination  : ('Msg -> bool) -> ('Model -> unit) -> Program -> Program             // new
Program.mapTermination   : ...                                          // new
Program.withTrace        : ('Msg -> 'Model -> SubId list -> unit) -> Program -> Program         // new
Program.withConsoleTrace                                        // extended: also logs active sub IDs
Program.withErrorHandler / mapErrorHandler / onError                    // mapErrorHandler, onError new

Program.run             : Program<unit,...> -> 'View                    // unchanged signature
Program.runWith         : 'Arg -> Program -> 'View                       // unchanged signature (issue 6 compat)
Program.runWithLifecycle: 'Arg -> Program -> 'View * IDisposable         // new (issue 6 proposal)
Program.runWithLifetime : Visual -> 'Arg -> Program -> 'View * IDisposable  // new (Avalonia)
```

`runWith` is `runWithLifecycle |> fst`. An unheld program runs until its termination
predicate fires — the same contract as Elmish `Program.run`.

### 3.4 Cmd module (additive only)

```fsharp
Cmd.ofEffect : (Dispatch<'Msg> -> unit) -> Cmd<'Msg>                     // new convenience

Cmd.OfFunc.either   / perform / attempt                                 // either new
Cmd.OfAsync.either  / perform / attempt                                 // either new (StartImmediate, as today)
Cmd.OfAsyncWith.either / perform / attempt (start: Async<unit> -> unit) // new module
Cmd.OfTask.either   / perform / attempt                                 // either new
Cmd.OfValueTask.either / perform / attempt                              // new module (net10 target)
```

Semantics stay MVUish: `perform` routes exceptions to the error handler (Elmish swallows
them — documented difference); `either` maps the exception to a message; `attempt`
dispatches only the error. `OfValueTask` ports the v5 completed-synchronously fast path.
`OfAsyncWith` + `Async.Start` is the migration target for Elmish's thread-pool `OfAsync`.

### 3.5 C# surface (parity)

```csharp
Subscription.None / Batch / Map(idPrefix, ...) / OfId / Interval(id, ...) / FromEvent(id, ...)
  / FromRoutedEvent(id, target, routedEvent, map) / FromObservable(id, source) / UntilDetached(control, sub)

Command.None / OfMsg / Batch / Map / Perform / PerformTask                            // existing
Command.Attempt / AttemptTask / Either / EitherTask                                   // new
Command.PerformValueTask / AttemptValueTask / EitherValueTask                         // new

ProgramBuilder.WithSubscription / WithTermination / WithTrace / WithConsoleTrace / WithErrorHandler
  .RunWith(arg)                        -> View                                        // unchanged
  .RunWithLifecycle(arg)               -> (View, IDisposable)
  .RunWithLifetime(control, arg)       -> (View, IDisposable)
```

## 4. Core implementation details

### 4.1 Loop rewrite (`Program.runWithLifecycle`)

- Internal `type LoopMsg<'Msg> = UserMsg of 'Msg | Stop`.
- `stopped` flag; the public `dispatch` becomes
  `fun msg -> if not stopped then mailbox.Post(UserMsg msg)`.
- Per `UserMsg`, on the UI thread, in this order (Elmish order):
  1. termination predicate check — on match: teardown(terminate = true); exit loop.
  2. `Update msg state.Value` (try/catch → OnError; program survives).
  3. `state.OnNext(newModel)` when the reference changed (render).
  4. re-evaluate `program.Subscribe newModel` (try/catch → OnError; on throw, keep the
     previous active set), then `activeSubs <- Subs.diff activeSubs sub' |> Fx.change OnError dispatch`.
  5. execute `cmd'` with dispatch and a contextual OnError.
- Init order becomes: create subject → create view (try/catch → OnError) → start initial
  subs from `initModel` via the same diff path → run init cmd. (1.0 ran init cmd first;
  the new order matches Elmish setState → subs → cmds.)
- `teardown(terminate)` (guarded, idempotent — double dispose is a no-op per issue 6):
  1. set `stopped`.
  2. `Fx.stop` all active subs (each stop individually try/catch → OnError).
  3. `state.OnCompleted()` — every subscription derived from the model stream ends.
  4. `state.Dispose()`.
  5. if `terminate`: call the termination handler with the final model.
- The returned `IDisposable` posts `Stop` and runs the same teardown.

### 4.2 `Sub.untilDetached`

Wraps every `Subscribe` in the list so its disposable is also disposed when the visual
detaches. The wrapper is itself idempotent because the diff engine may dispose the same
disposable later. Documented caveat: a detached sub stays in the active set until the next
diff removes it; its start function simply stops producing.

### 4.3 Visual-tree lifetimes and load/unload

`AttachedToVisualTree` fires when a control enters the visual tree (loaded);
`DetachedFromVisualTree` fires when it leaves (unloaded). `runWithLifetime control`
disposes the program on the **first detach**. Re-attaching does not restart it — MVU
programs are single-run (`init` happens once); reload means running a new program.
Guidance written into the docs:

- Page navigation (page leaves and is discarded): `runWithLifetime`.
- Cached tabs / kept-alive views that detach and reattach: use `runWithLifecycle` and let
  the navigation host own the disposable.

### 4.4 Attribution

The sub engine and `OfValueTask` fast path are ported from Elmish (Apache License 2.0,
Copyright elmish contributors). Add a source header comment on the ported sections and a
third-party notice line in the README. Our overall MIT license is unaffected.

## 5. Test plan

New files: `tests/MVUish.Avalonia.Tests/SubDiffTests.fs`, `ProgramTests.fs`.
Existing `DiffTests.fs` / `AdapterTests.fs` stay green.

Ported from `E:\elmish\tests\SubTests.fs` (6 cases): no-change, stop-removed,
start-added, mixed, duplicate detection with no changes, last-dupe-wins.

New program tests (headless session, as in AdapterTests). Harness pattern, validated by
the dry run: every test that depends on the mailbox loop awaits inside
`session.Dispatch<int>(Func<Task<'T>>, ct)` using `Task.WhenAny(work, Task.Delay(timeout))`
— never blocking sleeps on the dispatcher thread. Constructing routed-event args requires
initializer syntax because `KeyEventArgs.Key` is init-only in Avalonia 12:
`KeyEventArgs(Key = Key.A)` then `args.RoutedEvent <- InputElement.KeyDownEvent`.
- Batch folding: dispatching N batched messages folds to the expected model (property-style, ported).
- `Update` throws → OnError called, program processes the next message.
- View factory throws → OnError called, no crash.
- Termination message → handler sees the final model, further dispatch is ignored, loop exits.
- `runWithLifecycle` dispose → `Sub.interval` counter stops ticking; double dispose no-op;
  dispatch after stop no-op.
- Stream completion: a derived `Observable.subscribe` receives OnCompleted on dispose and
  on termination; an `AvaloniaObject.Bind` against a completed subject releases the
  subscription and keeps the last value.
- Dynamic subs: conditional sub starts/stops when the model toggles it; an ID that embeds
  an interval (`["timer"; string ms]`) restarts the timer when the interval changes.
- `Sub.fromRoutedEvent`: headless control, `RaiseEvent(KeyDownEvent, …)`, message dispatched,
  handler removed after diff/stop.
- `runWithLifetime`: attach to a headless window, remove → program disposed.

## 6. Docs plan (fsdocs — everything lives in docs/)

| File | Action |
|------|--------|
| `docs/subscriptions.md` | New page: model-driven subs, IDs and dependencies, conditional subs, `Sub.map`/`batch` composition, `fromObservable`, routed events, lifetimes, load/unload guidance. Modeled on Elmish's subscription guide. |
| `docs/migration-1-to-2.md` | New page (fsdocs front matter, category Documentation): the 1.0 → 2.0 migration guide. Breaking-change table plus before/after F# and C# snippets: Sub type, `Sub.map` prefix, keyed `Sub.interval`/`fromEvent`, subscribe functions return keyed subs, collection bindings returning the control (from the unreleased 1.x fix), new `Termination` field, Elmish `OfAsync` → `OfAsyncWith` mapping. |
| `docs/reactive-mvu.md` | Rewrite Subscriptions section; update the Program builder table (`withTermination`, `withTrace`, `mapSubscription`, lifecycle run functions); extend the Commands table (`either`, `OfValueTask`, `OfAsyncWith`); replace the false "disposes when you say so" sentence; new Lifecycle section. |
| `docs/getting-started.md` | Update any `Sub`/`withSubscription` snippets to the keyed API. |
| `README.md` | Feature bullets, sample links, third-party notice for Elmish. |
| `CHANGELOG.md` | `[2.0.0]` section in KeepAChangelog format (Added / Changed / Fixed). Drives the package version via Ionide.KeepAChangelog.Tasks. Absorbs the unreleased collection-binding entry. |
| XML docs | All new public members documented; the fsdocs reference site rebuilds from them. |

## 7. Samples, templates, release

- `src/samples/FSharp` + `src/samples/CSharp`: `Clock.subscribe` moves to keyed subs;
  Metrics gains a conditional sub demo (run the poll only while a page/mode is active).
- `templates/mvu-fsharp` + `templates/mvu-csharp`: both use `Sub.interval` +
  `withSubscription` today; update to the 2.0 API so `dotnet new` emits compiling code.
- Release sequence: merge `fix/collection-binding-lifetime` into `main` → implement on a
  `release/2.0.0` branch → full build + tests → `dotnet pack` smoke (artifacts/packtest
  pattern) → CI green → update CHANGELOG date → tag `2.0.0` → GitHub Release → close
  issue 6 referencing the release.

## 8. Phase order and size

| Phase | Content | Size |
|-------|---------|------|
| 0 | Merge current fix branch to main | 0.25 d |
| 1 | Types + Sub engine port + SubDiffTests | 0.5–1 d |
| 2 | Loop rewrite, lifecycle, termination + ProgramTests | 1–1.5 d |
| 3 | Sub helpers (interval/fromEvent/fromObservable/fromRoutedEvent/untilDetached) + tests | 0.5–1 d |
| 4 | Cmd additions (`either`, `OfAsyncWith`, `OfValueTask`, `ofEffect`) + tests | 0.5 d |
| 5 | Program tooling (`withTrace`, `mapSubscription`, `mapTermination`, console trace subs) | 0.5 d |
| 6 | C# interop parity + builder + tests | 1 d |
| 7 | Samples + templates | 0.5–1 d |
| 8 | Docs (subscriptions, migration, reactive-mvu, getting-started, README, CHANGELOG) | 1–1.5 d |
| 9 | Release 2.0.0 | 0.5 d |

Total: roughly 6–9 dev-days.

## 10. Dry run results (2026-10-20)

Verification executed without touching product code. Baseline: full build + test suite of
the current branch — 33/33 tests pass in 342 ms.

Static checks against `E:\Avalonia` (12.0.5, identical to the library's target):

| Assumption | Result |
|---|---|
| `Interactive.AddHandler<TEventArgs>(RoutedEvent, EventHandler, RoutingStrategies, bool)` | Confirmed — [Interactive.cs:52](E:/Avalonia/src/Avalonia.Base/Interactivity/Interactive.cs#L52) |
| `Interactive.RemoveHandler<TEventArgs>(RoutedEvent<T>, EventHandler<T>)` | Confirmed — [Interactive.cs:106](E:/Avalonia/src/Avalonia.Base/Interactivity/Interactive.cs#L106) |
| `Visual.DetachedFromVisualTree` / `AttachedToVisualTree` events | Confirmed — [Visual.cs:177](E:/Avalonia/src/Avalonia.Base/Visual.cs#L177) |
| `AvaloniaObject.Bind<T>(StyledProperty<T>, IObservable<T>)` returns `IDisposable` | Confirmed — [AvaloniaObject.cs:511](E:/Avalonia/src/Avalonia.Base/AvaloniaObject.cs#L511) |
| Routed events raisable in tests (`RoutedEventArgs(RoutedEvent)` ctor, settable `RoutedEvent`) | Confirmed — [RoutedEventArgs.cs:21](E:/Avalonia/src/Avalonia.Base/Interactivity/RoutedEventArgs.cs#L21) |
| Version derives from CHANGELOG.md (`ChangelogFile` + Ionide.KeepAChangelog.Tasks) | Confirmed — Directory.Build.props; artifacts/packtest shows the 1.0.0 nupkg it produced |

Behavioral spike (throwaway console app, Avalonia.Headless 12.0.5 + System.Reactive 7.0,
mirroring the test project's stack):

| Check | Result |
|---|---|
| Bind receives live emissions from `BehaviorSubject` | PASS |
| Derived `Observable.subscribe` receives `OnCompleted`; post-completion emissions ignored; subject dispose after completion safe | PASS |
| Bound property after `OnCompleted` | **Resets to default** (`Width` 50 → NaN, `Text` "world" → null, `IsSet` false) — documented behavior, decision unchanged |
| Mailbox loop + `Dispatcher.UIThread.InvokeAsync` awaited inside `Dispatch(Func<Task>)` | PASS (mandatory test pattern) |
| Routed event add → `RaiseEvent` → remove | PASS |
| `AttachedToVisualTree` on window show; `DetachedFromVisualTree` on content removal | PASS |
| Background UI work without an in-flight awaited `Dispatch` | Never executes — tests must await inside `Dispatch(Func<Task>)` |
| `KeyEventArgs.Key` | Init-only — construct with `KeyEventArgs(Key = Key.A)` |

Spike caveats handled in code: `Dispatch` overload ambiguity requires an explicit type
argument (`session.Dispatch<int>(work, ct)`).

Verdict: the plan is viable. One planned behavior was corrected from assumption to fact
(property reset on stream completion) and the test harness pattern is now fixed. No design
decision changed.

## 9. Risks

- ~~`BehaviorSubject.OnCompleted` while Avalonia bindings are attached~~ — resolved by the
  dry run: bindings revert the property to its default (`Width` → NaN, `Text` → null,
  `IsSet` → false) and release the subscription; derived `Observable.subscribe`
  subscriptions receive `OnCompleted`; emissions after completion are ignored; disposing
  the subject after completing is safe. Decision unchanged (complete the stream on
  teardown), but this becomes documented behavior: terminating a program resets controls
  bound to its model stream. The subscriptions doc and migration guide state it.
- Headless tests around mailbox timing — resolved by the dry run: work posted to the UI
  dispatcher from the mailbox does **not** run spontaneously in a headless session (not
  even after an unrelated `Dispatch(Action)`). The working pattern is to await from
  inside `session.Dispatch<int>(Func<Task<'T>>, ct)` with `Task.WhenAny` timeouts; this
  pattern is mandatory for all Program loop tests.
- `Sub.map` gaining the ID prefix is the widest consumer touch; the migration page leads
  with it.
- C# tuple returns for the lifecycle functions: acceptable (`ValueTuple` already used in
  the builder signatures).
