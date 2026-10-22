---
categoryindex: 0
index: 3
title: The Reactive MVU Pattern
category: Documentation
---

# The Reactive MVU Pattern

## Model-Update-Observable

In a standard MVU loop the `View` function runs on every `Model` change and produces a new virtual tree, which is then diffed against the previous one. MVUish.Avalonia keeps the Model and Update halves of Elm but replaces the diffing view with a direct binding:

1. **Model and Update** stay pure and immutable. `Update` processes a `Msg` and returns the new `Model` plus a command.
2. **View** runs **once** at startup. It receives an `IObservable<Model>` and a `dispatch` function.
3. **Bindings** carry the updates. Each control property subscribes to the slice of the stream it needs, so only the properties that change are updated.

There is no tree diffing and no wrapper controls: the view returns real Avalonia controls, so third-party controls and layouts work without special support.

## Programs

`Program.mkProgram` (F#) / `Program.MkProgram` (C#) takes `init`, `update`, and `view`. The builder functions add behavior:

| F# | C# | Purpose |
| --- | --- | --- |
| `Program.withSubscription` | `.WithSubscription(...)` | Register the keyed subscription set; re-evaluated per update |
| `Program.mapSubscription` | — | Accumulate instead of replace subscriptions |
| `Program.withTermination` | `.WithTermination(...)` | Exit message plus final-model handler |
| `Program.withTrace` | `.WithTrace(...)` | Trace message, model, and wanted sub ids |
| `Program.withConsoleTrace` | `.WithConsoleTrace()` | Log messages, models, and active sub ids |
| `Program.withErrorHandler` | `.WithErrorHandler(...)` | Handle command and update errors |
| `Program.run` / `runWith` | `.Run()` / `.RunWith(arg)` | Start the loop and get the view |
| `Program.runWithLifecycle` | `.RunWithLifecycle(arg)` | View plus an `IDisposable` that stops the program |
| `Program.runWithLifetime` | `.RunWithLifetime(control, arg)` | Program tied to a control's visual-tree lifetime |

Update and dispatch are marshalled to the UI thread, and `dispatch` is thread-safe from any thread.

## Commands

A `Cmd<'Msg>` (C# `Action<Action<'Msg>, Action<string, exn>>`) runs side effects and dispatches messages. `Program` routes errors from commands to the error handler with the message that caused them.

Common helpers:

| F# | C# | Purpose |
| --- | --- | --- |
| `Cmd.none` | `Command.None<Msg>()` | No side effect |
| `Cmd.ofMsg` | `Command.OfMsg(...)` | Dispatch a message immediately |
| `Cmd.ofEffect` | — | Effect that receives `dispatch` for follow-up messages |
| `Cmd.ofAction` | `Command.OfAction(...)` | Plain `unit -> unit` action (view-side effects) |
| `Cmd.batch` | `Command.Batch(...)` | Run several commands |
| `Cmd.map` | `Command.Map(...)` | Remap a child command for composition |
| `Cmd.OfFunc.perform` | `Command.Perform(...)` | Run a sync function, dispatch the result |
| `Cmd.OfFunc.attempt` | `Command.Attempt(...)` | Run a sync function, dispatch the error |
| `Cmd.OfFunc.either` | `Command.Either(...)` | Run a sync function, map success or error to messages |
| `Cmd.OfAsync.perform` | — | Run an `Async` workload, dispatch the result |
| `Cmd.OfAsyncWith.perform` | — | Same, with a custom async start |
| `Cmd.OfTask.perform` | `Command.PerformTask(...)` | Run a `Task` workload, dispatch the result |
| `Cmd.OfTask.either` | `Command.EitherTask(...)` | Run a `Task`, map success or error to messages |
| `Cmd.OfValueTask.*` | `Command.*ValueTask(...)` | Native ValueTask commands |

## Subscriptions

A `Sub<'Msg>` (C# `CSub<Msg>`, built with the `Subscription` helpers) is the set of external event sources the current model wants active. The program re-evaluates the set after every update and starts or stops subscriptions to match; see [Subscriptions](subscriptions.html) for the full guide.

| F# | C# | Purpose |
| --- | --- | --- |
| `Sub.none` | `Subscription.None<Msg>()` | No subscriptions |
| `Sub.batch` | `Subscription.Batch(...)` | Combine several subscription sets |
| `Sub.map` | `Subscription.Map("prefix", ...)` | Scope child ids and translate child messages |
| `Sub.interval` | `Subscription.Interval("id", ...)` | Dispatch a tick message on a timer |
| `Sub.fromEvent` | `Subscription.FromEvent(...)` | Bridge a .NET event into messages |
| `Sub.fromObservable` | `Subscription.FromObservable(...)` | Bridge any `IObservable<'Msg>` |
| `Sub.fromRoutedEvent` | `Subscription.FromRoutedEvent(...)` | Bridge an Avalonia routed event |
| `Sub.untilDetached` | `Subscription.UntilDetached(...)` | End the set when a control detaches |

## Lifecycle

Programs own resources: subscriptions, the update loop, and the model stream. `runWithLifecycle` returns the handle that releases all three; `runWithLifetime` ties that release to a control leaving the visual tree; `withTermination` makes a message do it. Completing the model stream ends every subscription derived from it and reverts bound properties to their defaults — details in [Subscriptions](subscriptions.html).

## Composition and scaling

Modules compose like standard Elmish modules. A parent model holds the child models, and the parent maps child commands, subscriptions, and dispatchers:

- `Command.Map` / `Cmd.map` routes child commands upward.
- `Subscription.Map` / `Sub.map` routes child subscriptions upward, scoping their ids with a prefix.
- `Subscription.Batch` / `Sub.batch` combines subscription sets.

In the view, slice the parent stream with `Observable.map` / `Select` and hand each child its own observable and dispatch function:

```fsharp
let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
  let clockObs = model |> Observable.map(fun m -> m.Clock)
  let dispatchClock = ClockMsg >> dispatch

  StackPanel().Children(
    Clock.view clockObs dispatchClock
  )
```

The [dashboard sample](https://github.com/AngelMunoz/MVUish.Avalonia/tree/main/src/samples) wires three child modules this way, in both languages.
