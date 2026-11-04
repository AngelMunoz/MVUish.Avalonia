---
categoryindex: 0
index: 6
title: Testing
category: Documentation
---

# Testing MVU applications

MVUish programs are async by construction: `update` runs on the rendering
thread, one message at a time, and effects complete whenever they complete.
Never block that thread (`Task.Result`, `.Wait()`, `Thread.Sleep`) — you
would deadlock the loop you are trying to observe. This page covers the
threading rule, the two kinds of tests it implies, and the harness pieces
the library's own suite uses.

## The threading rule

All async work a command starts runs on the rendering thread — the
program's bound dispatcher's thread, `Dispatcher.UIThread` unless bound to
something else. `Cmd.OfAsync` starts on it, `Cmd.OfTask` /
`Cmd.OfValueTask` resume on it, so CPU-bound segments between suspensions
run there too. Real background work must be explicit (`Task.Run`,
`backgroundTask`, or a context round-trip — see
[Threading](reactive-mvu.html#threading)); tests feel the rule immediately,
because everything the program does serializes on one thread.

## Two kinds of tests

| | Logic-only | Control tests |
| --- | --- | --- |
| Dispatcher | a fake `IDispatcher` | the headless session's |
| Session | none | one per process |
| Parallelizable | yes, with anything | no — sequenced |
| Asserts | models, commands, subscriptions | rendered text, clicks, layout |

## Logic-only tests (no UI thread)

Bind a fake that runs every post inline. No session, no shared thread,
nothing global — these tests can run in parallel with anything:

```fsharp
let fake =
  { new IDispatcher with
      member _.CheckAccess() = true
      member _.VerifyAccess() = ()
      member _.Post(action: Action, _priority: DispatcherPriority) =
        action()
  }

let mutable dispatch = Unchecked.defaultof<Dispatch<unit>>
let mutable last = 0

let view (model: IObservable<int>) (d: Dispatch<unit>) =
  dispatch <- d
  model.Subscribe(fun m -> last <- m) |> ignore

let program =
  Program.mkSimple (fun () -> 0) (fun _ n -> n + 1) view
  |> Program.withDispatcher fake

let _, lifetime = Program.runWithLifecycle () program

dispatch()
dispatch()
// poll until last = 2, then dispose the lifetime
```

When ordering across threads matters, give the fake its own thread and a
queue instead — the suite's `PumpingDispatcher` is the pattern, and it is
also how two programs run on two dispatchers in one process.

Code that calls `InvokeAsync` or `Dispatcher.UIThread` directly bypasses
the binding. Under a fake, with no session pumping the global, such a call
queues on a dispatcher nothing drains: the await never completes and the
test fails at its timeout — a loud detector for the coupling. Keep direct
dispatch on a captured, pumped dispatcher, or route it through the bound
`IDispatcher` (which offers `Post`, not `InvokeAsync`).

## Control tests (the headless session)

One `Avalonia.Headless` session serves the whole test assembly, and every
program the harness starts is bound to that session's dispatcher:

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

```fsharp
let ui: Dispatcher =
  Session.Dispatch(
    Func<Dispatcher>(fun () -> Dispatcher.UIThread),
    CancellationToken.None)
    .GetAwaiter().GetResult()

// inside each test:
let program =
  Program.mkProgram init update view
  |> Program.withDispatcher ui
```

Binding keeps each program on the session's dispatcher even when headless
isolation resets the global — a program can no longer end up stranded on a
ghost dispatcher.

Why these tests stay sequenced, even with binding:

- they all share the one rendering thread, so parallel tests interleave
  programs on it and timing waits miss;
- focus, input, and the clipboard are process-global in Avalonia, and so is
  the managed heap a memory-budget test measures;
- the session's own dispatch loop pumps the global dispatcher.

`DisableTestParallelization` exists for these suites; logic-only tests
bound to their own fake carry no such constraint.

## The harness

Run the body on the UI thread with a cap that fails instead of hanging:

```fsharp
let private testTimeout = TimeSpan.FromSeconds 8.0

let private onUi (work: unit -> Task<bool>) : Task<bool> = task {
  use cts = new CancellationTokenSource(testTimeout)
  return! session.Dispatch<bool>(Func<Task<bool>>(work), cts.Token)
}
```

Poll, never sleep — dispatch, ticks, and command effects land
asynchronously, and timer-based subscriptions use real time, so assert
"the condition holds within the timeout", never exact counts after a fixed
delay:

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

Record the model stream and the dispatch function to drive a program by
hand:

```fsharp
type Harness<'Model, 'Msg>() =
  let mutable last = Unchecked.defaultof<'Model>
  let mutable dispatch = Unchecked.defaultof<Dispatch<'Msg>>
  let mutable completed = false

  member _.View =
    fun (model: IObservable<'Model>) (d: Dispatch<'Msg>) ->
      dispatch <- d
      model.Subscribe(
        (fun m -> last <- m),
        (fun _ -> ()),
        (fun () -> completed <- true))
      |> ignore
      Border()

  member _.Dispatch msg = dispatch msg
  member _.Model = last
  member _.Completed = completed
```

Never dispatch from inside a dispatch: a `session.Dispatch` made from the
UI thread queues work the blocked thread can never serve, and the test
freezes. The suite's `ProgramTests.fs` shows the pieces end to end.

## What to assert

Behavior, not internals:

- **user-visible outcomes**: bound `TextBlock` text, delivered messages,
  timers that stopped;
- **lifecycle**: disposing the handle freezes bound views, completes the
  model stream (bound properties revert to defaults), dispatch after
  dispose is dropped, dispose is idempotent;
- **subscriptions**: a source starts when the model wants it, stops when it
  does not, restarts when its id dependency changes; child sets compose
  under scoped ids;
- **commands**: what they dispatch and what they report — drive the
  program and assert the messages, or run the returned `Cmd` against
  recording `dispatch`/`onError` functions;
- **error paths**: a failing command reports to the program's error handler
  instead of throwing.

## C#

The same harness translates to C# xUnit `async Task` facts: one session,
`await session.Dispatch(work, token)` to run on the UI thread, small delays
in polls, and never a blocking wait.
