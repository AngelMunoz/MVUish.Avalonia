---
categoryindex: 0
index: 6
title: Testing
category: Documentation
---

# Testing MVU applications

MVUish programs are async by construction: `update` runs on a UI-thread loop,
dispatch is marshalled with `InvokeAsync`, and subscriptions tick on timers.
Tests that block the UI thread (`Task.Result`, `.Wait()`, `Thread.Sleep`)
deadlock the very loop they want to observe. The library's own test suite
(`tests/MVUish.Avalonia.Tests`) contains a harness that works; this page
explains its pieces so you can reuse them in your application.

## The headless session

One `Avalonia.Headless` session serves the whole test assembly:

```fsharp
module MyTests.TestSession

open Avalonia
open Avalonia.Headless

type TestApp() =
  inherit Application()

let Session = HeadlessUnitTestSession.StartNew(typeof<TestApp>)

[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()
```

Two invariants:

- **One session per process.** `Dispatcher.UIThread` is process-global; a
  second session posts the program loop's `InvokeAsync` work to the wrong
  dispatcher.
- **Parallelization stays off.** All tests share the one UI thread; concurrent
  tests would interleave programs on it. xUnit's
  `DisableTestParallelization` is set at the assembly level.

## Running a test body on the UI thread

Tests are `Task`-returning facts. The body is dispatched onto the headless UI
thread and must **await its work — never block**: the dispatcher only
processes the program loop's UI posts while the awaited task yields. A
`.Result` or `.Wait()` pins the thread and deadlocks the test. A
cancellation cap turns a stuck test into a failure instead of a hang:

```fsharp
let private testTimeout = TimeSpan.FromSeconds 8.0

let private onUi (work: unit -> Task<bool>) : Task<bool> = task {
  use cts = new CancellationTokenSource(testTimeout)
  return! session.Dispatch<bool>(Func<Task<bool>>(work), cts.Token)
}
```

```fsharp
[<Fact>]
let myFeatureWorks () : Task = task {
  let! ok = onUi(fun () -> task {
    // build the program, run it, assert, dispose
    return true
  })
  Assert.True ok
}
```

## Waiting for async outcomes

Dispatch, ticks, and command effects land asynchronously. Poll instead of
sleeping once:

```fsharp
let private waitFor (timeoutMs: int) (cond: unit -> bool) : Task<bool> = task {
  let sw = Stopwatch.StartNew()
  let mutable ok = cond()
  while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
    do! Task.Delay 10
    ok <- cond()
  return ok
}
```

Timer-based behavior (`Sub.interval`, a ticking subscription) uses real time:
keep intervals small and assert "the condition holds within the timeout",
never exact counts after a fixed sleep.

## Driving a program by hand

A recording view captures the model stream and the dispatch function, so a
test can drive messages by hand and assert outcomes:

```fsharp
type Harness<'Model, 'Msg>() =
  let last = ref Unchecked.defaultof<'Model>
  let mutable dispatch = Unchecked.defaultof<Dispatch<'Msg>>
  let completed = ref false

  member _.View =
    fun (model: IObservable<'Model>) (d: Dispatch<'Msg>) ->
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
```

Combine it with `Program.runWithLifecycle` inside `onUi`, dispatch messages,
`waitFor` the model to settle, then dispose the handle and assert the
completion flag. The suite's `ProgramTests.fs` shows this end to end —
`modelStreamCompletesOnDispose`, `boundControlsRevertToDefaultsOnCompletion`,
`subscriptionsFollowTheModel` — and `countingTicker` shows a recording
subscription whose `Dispose` is counted so teardown can be asserted.

## What to assert

Behavior, not internals:

- **user-visible outcomes**: bound `TextBlock` text, delivered messages,
  timers that stopped;
- **lifecycle**: disposing the handle freezes bound views, completes the model
  stream (bound properties revert to defaults), dispatch after dispose is
  dropped, and dispose is idempotent;
- **subscriptions**: a source starts when the model wants it and stops when it
  does not; changing an id dependency restarts it; child sets compose under
  scoped ids;
- **commands**: what they dispatch and report — run the returned `Cmd` against
  recording `dispatch`/`onError` functions, or drive a real program and
  assert the messages it delivers;
- **error paths**: a failing command reports to the program's error handler
  instead of throwing.

## C#

The same harness translates directly to C# xUnit `async Task` facts: start
one `HeadlessUnitTestSession`, `await session.Dispatch(work, token)` to run on
the UI thread, poll conditions with small delays, and never block the thread.
