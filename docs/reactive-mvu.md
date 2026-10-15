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
| `Program.withSubscription` | `.WithSubscription(...)` | Register a `Sub` for external events |
| `Program.withConsoleTrace` | `.WithConsoleTrace()` | Log every message and model to the console |
| `Program.withErrorHandler` | `.WithErrorHandler(...)` | Handle command and update errors |
| `Program.run` / `Program.runWith` | `.Run()` / `.RunWith(arg)` | Start the loop and get the view |

Update and dispatch are marshalled to the UI thread, and `dispatch` is thread-safe from any thread.

## Commands

A `Cmd<'Msg>` (C# `Action<Action<'Msg>, Action<string, exn>>`) runs side effects and dispatches messages. `Program` routes errors from commands to the error handler with the message that caused them.

Common helpers:

| F# | C# | Purpose |
| --- | --- | --- |
| `Cmd.none` | `Command.None<Msg>()` | No side effect |
| `Cmd.ofMsg` | `Command.OfMsg(...)` | Dispatch a message immediately |
| `Cmd.batch` | `Command.Batch(...)` | Run several commands |
| `Cmd.map` | `Command.Map(...)` | Remap a child command for composition |
| `Cmd.OfFunc.perform` | `Command.Perform(...)` | Run a sync function, dispatch the result |
| `Cmd.OfFunc.attempt` | — | Run a sync function, dispatch the error |
| `Cmd.OfAsync.perform` | — | Run an `Async` workload, dispatch the result |
| `Cmd.OfTask.perform` | `Command.PerformTask(...)` | Run a `Task` workload, dispatch the result |

## Subscriptions

A `Sub<'Msg>` (C# `Func<Action<'Msg>, IDisposable>`) registers external event sources and returns the disposable that unplugs them. `Program` subscribes with the initial model and disposes when you say so.

| F# | C# | Purpose |
| --- | --- | --- |
| `Sub.none` | — | No subscription |
| `Sub.batch` | `Subscription.Batch(...)` | Combine several subscriptions |
| `Sub.map` | `Subscription.Map(...)` | Remap a child subscription for composition |
| `Sub.interval` | `Subscription.Interval(...)` | Dispatch a tick message on a timer |
| `Sub.fromEvent` | `Subscription.FromEvent(...)` | Bridge a .NET event into messages |

## Composition and scaling

Modules compose like standard Elmish modules. A parent model holds the child models, and the parent maps child commands, subscriptions, and dispatchers:

- `Command.Map` / `Cmd.map` routes child commands upward.
- `Subscription.Map` / `Sub.map` routes child subscriptions upward.
- `Subscription.Batch` / `Sub.batch` combines subscriptions.

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
