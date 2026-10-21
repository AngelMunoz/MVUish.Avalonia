---
categoryindex: 0
index: 5
title: Subscriptions
category: Documentation
---

# Subscriptions

Subscriptions connect outside event sources — timers, stores, keyboard input — to your `update` function. A subscription set is a function of the model: every time the model changes, the program re-evaluates it and starts or stops subscriptions to match what it returns.

A subscription has an **id** and a **start function**. The start function receives `dispatch` and returns the `IDisposable` that unplugs the source.

```fsharp
type SubId = string list
type Subscribe<'Msg> = Dispatch<'Msg> -> IDisposable
type Sub<'Msg> = (SubId * Subscribe<'Msg>) list
```

## A first subscription

A clock that ticks every second:

```fsharp
module Clock =
    type Model = { Current: DateTime }
    type Msg = Tick of DateTime

    let init () = { Current = DateTime.MinValue }, Cmd.none

    let update (Tick now) model = { model with Current = now }, Cmd.none

    let subscribe _ =
        Sub.interval "clock" (TimeSpan.FromSeconds 1.0) (fun () -> Tick DateTime.Now)

Program.mkProgram Clock.init Clock.update view
|> Program.withSubscription Clock.subscribe
|> Program.run
```

`subscribe` answers one question: *which subscriptions should run for this model?* The program calls it after every update and reconciles. In C#:

```csharp
CSub<Msg> Subscribe(Clock.Model _) =>
    Subscription.Interval<Msg>("clock", TimeSpan.FromSeconds(1),
        () => new Msg.Tick(DateTime.Now));
```

## Conditional subscriptions

Return the subscription only when the model wants it. Toggling the model stops and starts the source automatically:

```fsharp
let subscribe model =
    [ if model.Enabled then
        Sub.ofId "timer" (fun dispatch ->
            let timer = new Timers.Timer(25.0)
            timer.Elapsed.Add(fun _ -> dispatch (Tick DateTime.Now))
            timer.Start()
            { new IDisposable with
                member _.Dispose() = timer.Stop(); timer.Dispose() }) ]
```

When `Enabled` turns false, the engine disposes the timer. When it turns true again, a new one starts.

## Ids and dependencies

The id is a **list of strings** for two reasons.

**Composition.** `Sub.map` scopes a child's ids with a prefix, so two children that both use `"tick"` never collide:

```fsharp
let subscribe model =
    Sub.batch [
        Sub.map "seconds" SecondsMsg (Seconds.subscribe model.Seconds)
        Sub.map "hours" HoursMsg (Hours.subscribe model.Hours)
    ]
```

`"seconds"` maps to id `["seconds"; "tick"]`; `"hours"` to `["hours"; "tick"]`.

**Dependencies.** Anything you put in the id beyond uniqueness restarts the subscription when it changes — the Elm pattern for settings that affect the source:

```fsharp
let subscribe model =
    [ [ "ticker"; string model.IntervalMs ],
      fun dispatch -> startTicker model.IntervalMs dispatch ]

// IntervalMs 500 -> id ["ticker"; "500"]
// IntervalMs 250 -> old id disappears, engine stops it, ["ticker"; "250"] starts
```

Duplicate ids inside one set are reported to the program's error handler.

## Built-in helpers

| F# | C# | Purpose |
| --- | --- | --- |
| `Sub.none` | `Subscription.None<Msg>()` | Empty set |
| `Sub.batch` | `Subscription.Batch(...)` | Combine sets |
| `Sub.map` | `Subscription.Map("prefix", ...)` | Scope and translate child messages |
| `Sub.ofId` | `Subscription.OfId(...)` | One keyed start function |
| `Sub.interval` | `Subscription.Interval("id", ...)` | Timer ticks |
| `Sub.fromEvent` | `Subscription.FromEvent(...)` | .NET add/remove events |
| `Sub.fromObservable` | `Subscription.FromObservable(...)` | Bridge any `IObservable<'Msg>` |
| `Sub.fromRoutedEvent` | `Subscription.FromRoutedEvent(...)` | Avalonia routed events (Direct/Bubble) |
| `Sub.fromRoutedEventWith` | `Subscription.FromRoutedEventWith(...)` | Routed events with explicit `RoutingStrategies` / handled-events-too |
| `Sub.untilDetached` | `Subscription.UntilDetached(...)` | End the set when a control detaches from the visual tree |

### Observables

`Sub.fromObservable` turns any `IObservable<'Msg>` into one keyed subscription; the engine unsubscribes when the id leaves the set. This is the reactive answer to "my source is already a stream":

```fsharp
let subscribe _ =
    Sub.fromObservable "session" sessionStore // IObservable<Msg>
```

### Routed events

`Sub.fromRoutedEvent` subscribes to an Avalonia `RoutedEvent` on a control, with Direct/Bubble routing by default. Since the view builds controls before subscriptions start, keep the control in a reference the subscribe function closes over:

```fsharp
let textBox = TextBox ()

let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
    // build the UI around textBox
    textBox

let subscribe _ =
    Sub.fromRoutedEvent "keys" textBox InputElement.KeyDownEvent (fun _ -> KeyPressed)
```

## Program lifetimes

`Program.runWith` keeps the 1.0 contract: the view comes back and the program runs until its termination message. Two new entry points own the teardown:

- `Program.runWithLifecycle arg program : 'View * IDisposable` — dispose the handle to stop all subscriptions, complete the model stream, and stop the update loop. Disposing twice is a no-op.
- `Program.runWithLifetime control arg program` — the same handle, disposed automatically when the control first detaches from the visual tree.

```fsharp
let page, lifetime = Program.runWithLifecycle arg program
// navigation host, later:
lifetime.Dispose ()
```

For pages, `runWithLifetime` covers the navigate-away case. For controls that detach and reattach by design (cached tabs), use `runWithLifecycle` and let your navigation host own the handle — a detached program does not restart on reattach; run a new program instead.

A termination message gives model-driven exit the Elm way: stop everything and hand the final model to a callback.

```fsharp
program
|> Program.withTermination
    (function Stop -> true | _ -> false)
    (fun finalModel -> ...)
```

### What completing the stream does

Disposing the lifetime (or terminating) completes the model `BehaviorSubject`:

- every subscription derived from the model stream receives `OnCompleted` and ends;
- controls bound to the model stream revert the bound property to its default (`Width` becomes NaN, `Text` becomes null) — the last emission does not stick;
- messages dispatched after the stop are dropped.

If you keep a control alive after terminating its program, re-bind it to a new program's stream rather than relying on the frozen values.
