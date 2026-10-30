---
name: mvuish-testing
description: Test MVUish programs and modules with the headless-session harness — Task-returning facts, onUi UI-thread bodies that await and never block, waitFor polling, recording Harness for model/dispatch/completion, teardown and subscription assertions. Use whenever writing, changing, reviewing, or debugging tests for MVUish.Avalonia code in this library or consumer apps, when adding tests for programs, commands, subscriptions, bindings, or lifetimes, or when a MVUish test hangs, times out, or flakes.
---

# MVUish testing

MVUish programs are async by construction: `update` runs on a UI-thread loop,
dispatch is posted with `InvokeAsync`, subscriptions tick on timers. Tests
work by **awaiting on that single headless UI thread**, never by blocking it.
Consumers: the complete harness is reproduced in §The minimal harness below —
the repository's test sources are not part of the NuGet package. Maintainers
working in this repository can also read `tests/MVUish.Avalonia.Tests`
(`TestSession.fs`, the harness in `ProgramTests.fs`). The user-facing version
of this guide is `docs/testing.md`.

## Suite invariants (breaking these breaks the suite)

1. **One headless session per process** (`HeadlessUnitTestSession.StartNew`
   in `TestSession.fs`): `Dispatcher.UIThread` is process-global; a second
   session posts the program loop's `InvokeAsync` work to the wrong
   dispatcher. Never create another.
2. **Test parallelization stays off**
   (`[<assembly: CollectionBehavior(DisableTestParallelization = true)>]`):
   tests share the one UI thread and would interleave programs on it.
3. **Facts are Task-returning and behavior-driven**: run a real program with
   `Program.runWithLifecycle` and assert user-visible outcomes (bound text,
   delivered messages, stopped timers, completion flags) — not internal
   library state.

## The two deadly mistakes

```fsharp
// ❌ Blocking the UI thread: pins the one thread the dispatcher needs, so the
// program loop's posts never run — the test deadlocks until the cap kills it.
let n = harness.Model.Task.Result   // or .Wait(), GetAwaiter().GetResult()
Thread.Sleep 200                     // same, plus it stops timer posts

// ❌ Asserting exact timing with a single fixed sleep: flaky on slow CI.
do! Task.Delay 200
Assert.Equal(3, parseText label)

// ✅ Await, then poll for the condition:
let! ok = waitFor 2000 (fun () -> parseText label >= 3)
Assert.True ok
```

Awaiting is the mechanism; the 8-second cancellation cap in `onUi` is only
the safety net that turns a stuck test into a failure.

## The minimal harness (complete — paste into your test project)

`TestSession.fs` — one headless session for the whole assembly:

```fsharp
module MyTests.TestSession

open Avalonia
open Avalonia.Headless

type TestApp() =
    inherit Application()

/// ONE session per test assembly: Dispatcher.UIThread is process-global.
let Session = HeadlessUnitTestSession.StartNew(typeof<TestApp>)

[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()
```

`TestHarness.fs` — the UI-thread runner, the poller, and the recording view:

```fsharp
module MyTests.TestHarness

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Avalonia.Controls
open MVUish.Avalonia
open MyTests.TestSession

/// Hard cap for one test body on the UI thread: a test that exceeds it
/// fails instead of hanging the suite.
let testTimeout = TimeSpan.FromSeconds 8.0

/// Run an async body on the headless UI thread. The body must await its
/// work (never block): the dispatcher only processes the program loop's
/// UI posts while the awaited task yields.
let onUi (work: unit -> Task<bool>) : Task<bool> =
    task {
        use cts = new CancellationTokenSource(testTimeout)
        try
            return! Session.Dispatch<bool>(Func<Task<bool>>(work), cts.Token)
        with
        | :? OperationCanceledException -> return false
        | :? AggregateException as ae when
            ae.InnerExceptions |> Seq.exists(fun e -> e :? OperationCanceledException) ->
            return false
    }

/// Poll every 10 ms until the condition holds or the timeout expires.
let waitFor (timeoutMs: int) (cond: unit -> bool) : Task<bool> =
    task {
        let sw = Stopwatch.StartNew()
        let mutable ok = cond()
        while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
            do! Task.Delay 10
            ok <- cond()
        return ok
    }

/// Records the last model, stream completion, and the dispatch function,
/// so tests can drive messages by hand and assert outcomes.
type Harness<'Model, 'Msg>() =
    let last = ref Unchecked.defaultof<'Model>
    let mutable dispatch = Unchecked.defaultof<Dispatch<'Msg>>
    let completed = ref false

    member _.View :
        IObservable<'Model> -> Dispatch<'Msg> -> Control =
        fun model d ->
            dispatch <- d
            model.Subscribe(
                (fun m -> last.Value <- m),
                (fun _ -> ()),
                (fun () -> completed.Value <- true))
            |> ignore
            Border()

    member _.Dispatch msg = dispatch msg
    member _.Model = last.Value
    member _.Completed = completed.Value

/// A real timer subscription whose Dispose is counted, so tests can
/// assert that a source stopped (needs `open System.Timers`).
let countingTicker (dispatch: Dispatch<'Msg>) (msg: 'Msg)
                   (intervalMs: float) (stops: int ref) =
    let timer = new Timers.Timer(intervalMs)
    timer.Elapsed.Add(fun _ -> dispatch msg)
    timer.Start()
    { new IDisposable with
        member _.Dispose() =
            incr stops
            timer.Stop()
            timer.Dispose() }
```

Notes:

- To record AND render, install the recording view and return the real
  control — the suite calls this `keeping`: one line,
  `fun model dispatch -> harness.View model dispatch |> ignore; control`.
- `Task.Delay` is only for real-time ticks (`Sub.interval` tests), kept
  small, always followed by a `waitFor` on the outcome.
- `Xunit.CollectionBehavior` needs a reference to `xunit` (and
  `Avalonia.Headless` needs `Avalonia.Diagnostics`-free headless packages:
  `Avalonia`, `Avalonia.Headless.XUnit`).

## Recipes

Run everything inside `onUi`; end every test with the program disposed.

**Ticks reach the view; teardown freezes it.** Bind a `TextBlock` to the
model, drive it with a `countingTicker` ticking every ~50 ms, then:

1. `waitFor` until the bound text changes — the tick arrived;
2. dispose the handle and record the current text;
3. `waitFor` the stop counter to reach 1, wait a little more, and assert the
   text is still the recorded value — frozen IS the assertion: a stopped
   subscription never updates again.

```fsharp
let! started = waitFor 2000 (fun () -> parseText label <> 0)
handle.Dispose()
let frozen = label.Text
let! stopped = waitFor 500 (fun () -> stops.Value >= 1)
do! Task.Delay 200
Assert.Equal(frozen, label.Text)
```

**Lifecycle contract.** With a `Harness`-driven program: dispose the handle
twice (the second call must be a no-op), dispatch a message after dispose
(nothing may throw, and the model must not change), and assert `Completed`
became true. If the view binds a property (`.Text(obs)`), assert it reverts
to its default after completion (`TextBlock.Text` becomes null) instead of
freezing on the last value.

**Subscription reconciliation.** Model-driven sources: start with the source
off, flip the model flag on, `waitFor` the tick counter to move; flip it off
and `waitFor` the stop counter to increment. For dependency restarts, embed a
value in the id (e.g. `[ "ticker"; string intervalMs ]`), change the value,
and assert stop count +1 with the new source ticking at the new rate. For
child composition, run two child modules whose subscriptions would share an
id under different `Sub.map` prefixes and assert both tick with no
duplicate-id report on the error handler.

**Command outcomes.** Two levels:

- unit level — call `update` directly and run the returned `Cmd` against
  recording functions: `cmd recordedDispatch recordedOnError`, then assert
  what `recordedDispatch` received (or what `recordedOnError` caught);
- program level — drive a real program through `Harness.Dispatch` and
  `waitFor` the user-visible effect of the command's messages.

**Error paths.** A command that throws (or `Cmd.OfFunc.attempt` over a
failing function) must surface as a call to the program's error handler —
record `onError` and assert the message name and exception — while the loop
keeps processing later messages.

Maintainers working in this repository: the canonical implementations are
`clockTicksIntoBoundViewAndDisposingFreezesIt`, `modelStreamCompletesOnDispose`,
`boundControlsRevertToDefaultsOnCompletion`, `subscriptionsFollowTheModel`,
`changingAnIdDependencyRestartsTheSubscription`, and
`childSubscriptionsComposeUnderScopedIds` in
`tests/MVUish.Avalonia.Tests/ProgramTests.fs`.

## When a test fails

- **Hangs to the 8s cap**: something blocked the UI thread (a `.Result`,
  `.Wait()`, `Thread.Sleep`, or a sync wait in the body) — find it first.
- **Flaky**: a single fixed `Task.Delay` where a `waitFor` belonged, or a
  timing window too tight for CI — widen the poll timeout, shrink timer
  intervals, never assert exact tick counts.
- **Wrong dispatcher / cross-test pollution**: a second session was created,
  parallelization got re-enabled, or a program handle was not disposed before
  the test returned. Every program a test starts must be disposed by its end.

## Consumers

Use the minimal harness above as-is and keep the same invariants — the
repository's test sources are not in the NuGet package, which is why the full
code lives in this skill. C# xUnit facts translate directly: `async Task`
tests, `await session.Dispatch(work, token)`, polled conditions, no blocking.
