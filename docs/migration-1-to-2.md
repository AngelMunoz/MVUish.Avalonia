---
categoryindex: 0
index: 6
title: Migrating 1.0 to 2.0
category: Documentation
---

# Migrating 1.0 to 2.0

2.0 replaces the 1.0 subscription model (start once with the initial model, never stop) with the keyed, model-driven subscriptions Elmish has used since v4, adds program lifetimes and termination, and extends the command set. The library still targets Avalonia 12 and .NET 10, and the observable-first view is unchanged.

## Quick list of breaking changes

| Area | 1.0 | 2.0 |
| --- | --- | --- |
| `Sub<'Msg>` | one start function `Dispatch -> IDisposable` | list of keyed subscriptions `(SubId * Subscribe<'Msg>) list` |
| `Sub.none` | a function | `[]` |
| `Sub.batch` | combines functions | concatenates keyed sets |
| `Sub.map` | `mapping -> sub -> sub` | `idPrefix -> mapping -> sub -> sub` |
| `Sub.interval` | `interval -> tick -> Sub` | `id -> interval -> tick -> Sub` |
| `Sub.fromEvent` | `add -> remove -> map -> Sub` | `id -> add -> remove -> map -> Sub` |
| Program record | five fields | adds `Termination` |
| C# `WithSubscription` | `Func<Model, Func<Action<Msg>, IDisposable>>` | `Func<Model, CSub<Msg>>` (build with the `Subscription` helpers) |
| Collection bindings | return `IDisposable` (1.0.x) | return the control (already shipped in the 1.0.x fix; unchanged here) |

## Subscriptions: the main migration

**1.0** — one subscription, started once with the initial model, running forever:

```fsharp
let subscribe _ =
    Sub.interval (TimeSpan.FromSeconds 1.0) (fun () -> Tick DateTime.Now)
```

**2.0** — a keyed set, re-evaluated as the model changes, started and stopped to match:

```fsharp
let subscribe _ =
    Sub.interval "clock" (TimeSpan.FromSeconds 1.0) (fun () -> Tick DateTime.Now)
```

Add an id to every helper call. If you wrote a custom start function, wrap it with `Sub.ofId`:

```fsharp
// 1.0
let subscribe _ = fun dispatch -> startMySource dispatch

// 2.0
let subscribe _ = Sub.ofId "my-source" (fun dispatch -> startMySource dispatch)
```

Child composition gains the prefix argument:

```fsharp
// 1.0
Clock.subscribe model.Clock |> Sub.map ClockMsg

// 2.0
Clock.subscribe model.Clock |> Sub.map "clock" ClockMsg
```

Custom `subscribe` functions that build lists by hand should return `(SubId * Subscribe<'Msg>) list` tuples instead of a single function.

## Programs: lifetimes and termination

`Program.run` and `Program.runWith` keep their signatures and behavior, except subscriptions now stop when the model no longer asks for them, and a termination message (if you register one) stops the whole program.

New in 2.0:

```fsharp
// manual teardown: view plus a lifetime handle
let view, lifetime = Program.runWithLifecycle arg program
lifetime.Dispose ()   // stops subs, completes the model stream, stops the loop

// teardown tied to a control leaving the visual tree
let view, handle = Program.runWithLifetime control arg program

// model-driven exit
program |> Program.withTermination (fun msg -> ...) (fun finalModel -> ...)
```

Behavior notes:

- Disposing twice is a no-op; dispatch after dispose is dropped.
- Completing the model stream resets controls bound to it to their default property values. Bind a long-lived control to a new program instead of keeping it on a terminated one.

## Commands

Everything from 1.0 still exists (`none`, `batch`, `ofMsg`, `map`, `OfFunc.perform/attempt`, `OfAsync.perform/attempt`, `OfTask.perform/attempt`). New:

| F# | Purpose |
| --- | --- |
| `Cmd.ofEffect` | effect that receives `dispatch` (for follow-up messages) |
| `Cmd.ofAction` | plain `unit -> unit` action, e.g. `Cmd.ofAction tb.Clear` |
| `Cmd.OfFunc.either` | success or exception mapped to messages |
| `Cmd.OfAsync.either`, `Cmd.OfTask.either` | same for async and task workloads |
| `Cmd.OfAsyncWith.*` | custom async start (use `Async.Start` to migrate Elmish's thread-pool `OfAsync`) |
| `Cmd.OfValueTask.*` | native ValueTask commands |

The error policy is unchanged: `perform` reports exceptions to the error handler; `either` turns them into messages; `attempt` dispatches only the error.

## C#

`Subscription.Map` and `Subscription.Batch` keep their names. All factories now return `CSub<'Msg>`, a named handle for the F# subscription set — build sets with the `Subscription` helpers and pass them to `WithSubscription`. New factories: `FromObservable`, `FromRoutedEvent`, `FromRoutedEventWith`, `UntilDetached`. `Command` gains `OfAction`, `Attempt`, `Either`, `AttemptTask`, `EitherTask`, and the ValueTask trio. The builder gains `WithTermination`, `WithTrace`, `RunWithLifecycle`, and `RunWithLifetime`.

## Dev tools

`Program.withTrace` reports message, resulting model, and the ids of the wanted subscriptions; `Program.withConsoleTrace` logs active sub ids too; `mapSubscription`, `mapErrorHandler`, and `mapTermination` compose over existing configuration.
