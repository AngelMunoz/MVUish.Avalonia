---
name: mvuish-element-shapes
description: Author MVUish elements and reusable controls in a consistent shape — stateless element functions that depend only on their inputs and return Control, stateful UserControl components that own a Program with props in and events out, parent notifications routed through commands (Cmd.ofAction / Command.OfAction) instead of update's compute part, props records as the surface grows, and NXUI builder specifics. Use whenever creating, reviewing, or refactoring reusable controls, child views or element functions, component classes, update functions that invoke callbacks, or deciding stateless-vs-stateful in MVUish.Avalonia code (F# or C#).
---

# MVUish element shapes

Reusable UI in MVUish.Avalonia comes in exactly two shapes, both flavored by
how React elements are authored:

1. **Stateless element functions** — pure functions of their inputs that
   return a `Control`. No state, no program: what they render is decided by
   their arguments alone.
2. **Stateful components** — a control (typically a `UserControl`) that owns
   its own `Program`: props come in, events go out, the `Msg`/`Model` machinery
   stays private.

Two rules span both shapes:

- **Props in, events out.** Data flows down as arguments/props; a component
  talks to its parent only through callbacks the parent passed in.
- **Notifying the outside is a side effect.** Callbacks that tell a parent
  something happened are never invoked in the compute part of `update`; they
  are wrapped in a command (`Cmd.ofAction`) so the program's command path
  runs them.

Consuming external reactive state or anything disposable is out of scope for
both shapes: it belongs to subscriptions and the right program lifetime — see
[../mvuish-controls/SKILL.md](../mvuish-controls/SKILL.md) §2, §4 and §7.

## 1. Stateless element functions

The contract, in signature form:

```fsharp
// val statelessCounter:
//   count: int -> onIncrement: (unit -> unit) -> onDecrement: (unit -> unit) -> Control
```

Rules:

1. **Depend only on inputs.** No captured services, singletons, globals, or
   ambient configuration. The same arguments render the same element.
2. **Return `Control`.** Containers accept `Control` (NXUI's `.Children(...)`
   takes a `Control` sequence), so a more specific return type forces casts at
   every mixing point.
3. **Events are callbacks named `on*`.** The element invokes them from its own
   controls' events; it never dispatches the parent's messages directly.
4. **A plain value renders once.** `count: int` is a snapshot — correct for
   static or per-item contexts (DataTemplate rows, badges, decorative text).
   If the value must track the model, take an `IObservable` slice instead:

```fsharp
// Any control-building DSL; shapes are what matter here.
let counter (count: IObservable<int>) (onIncrement: unit -> unit)
            (onDecrement: unit -> unit) : Control =
    column [
        label (count |> Observable.map string)
        button "+" onIncrement
        button "-" onDecrement
    ]
```

Good and bad shapes:

```fsharp
// ✅ Inputs only; the parent decides where values and events come from.
let badge (text: string) : Control = ...

// ❌ Reads ambient state: untestable, unchangeable, and the capture is a
// lifetime edge you did not mean to create.
let badge () : Control = ... Theme.current.BadgeText ... SessionStore.user.Name ...

// ❌ A static int where the caller expects liveness:
counter model.Count onInc onDec   // renders ONE snapshot; never updates.
// ✅ Pass the slice (or wrap in a stateful component, §2):
counter (model |> Observable.map (fun m -> m.Count)) onInc onDec
```

## 2. Stateful components: a control owning its own Program

When an element has behavior of its own, wrap a `UserControl`, run a private
program, and keep `Model`/`Msg` inside. Props in, events out:

```fsharp
module Counter =

    type Model = { Count: int }
    type Msg = Increment | Decrement

    /// update is INJECTED with the parent's callbacks. The model transition
    /// stays pure; notifications ride the command path (§4).
    let update (onIncrement: unit -> unit, onDecrement: unit -> unit)
               (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | Increment ->
            { model with Count = model.Count + 1 }, Cmd.ofAction onIncrement
        | Decrement ->
            { model with Count = model.Count - 1 }, Cmd.ofAction onDecrement

    /// The component's view reuses stateless elements; dispatch stays inside.
    let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
        Elements.counter
            (model |> Observable.map (fun m -> m.Count))
            (fun () -> dispatch Increment)
            (fun () -> dispatch Decrement)

    /// The component's program, assembled from the callbacks. Both hosting
    /// cases below build from this single place.
    let program (onIncrement: unit -> unit) (onDecrement: unit -> unit)
                (initial: int) =
        Program.mkProgram
            (fun () -> { Count = initial }, Cmd.none)
            (update (onIncrement, onDecrement))
            view
```

The component owns its `Msg`/`Model`. One hosting decision remains —
**who owns the program's handle** — and both cases are written out:

```fsharp
// CASE A — placed once, replaced when done, never hidden and re-shown.
// runWithLifetime hooks `this`'s FIRST detach and disposes the program
// there, so the returned handle is dropped on purpose (fst): nobody ever
// has to remember it, and nobody may ever re-show this control.
type CounterControl(?init: int, ?onIncrement: unit -> unit, ?onDecrement: unit -> unit) as this =
    inherit UserControl()

    let onIncrement = defaultArg onIncrement ignore
    let onDecrement = defaultArg onDecrement ignore

    do
        this.Content <-
            Counter.program onIncrement onDecrement (defaultArg init 0)
            |> Program.runWithLifetime this ()
            |> fst

// CASE B — the host hides/stashes the control and re-shows it later (tab
// strips, recycled pages, sliding panels). First detach must NOT end the
// program: run with runWithLifecycle, keep BOTH outputs, bind the view, and
// give the handle to whoever owns visibility. The owner calls End() when the
// control is truly gone — on close or replace, NEVER on hide/detach: a
// detached program does not restart on reattach (mvuish-controls §7).
type StashableCounterControl(?init: int, ?onIncrement: unit -> unit, ?onDecrement: unit -> unit) as this =
    inherit UserControl()

    let view, lifetime =
        Counter.program
            (defaultArg onIncrement ignore)
            (defaultArg onDecrement ignore)
            (defaultArg init 0)
        |> Program.runWithLifecycle ()

    do this.Content <- view

    /// The visibility owner (navigation host, tab strip) calls this.
    member _.End() = lifetime.Dispose()
```

Good and bad shapes:

```fsharp
// ✅ Msg/Model are private; the parent sees only props and on* callbacks.
// ❌ Exposing the model Subject or a mutable counter field "so the parent can
//    read the value": that re-couples lifetimes (mvuish-controls §2). If the
//    parent needs value changes, add an event prop (§4) raised via a command.
// ❌ CASE A hosting on a re-shown control: the first detach killed the
//    program; coming back shows a control bound to a completed stream, with
//    bound properties reverted to their defaults.
// ❌ CASE B hosting with nobody owning the handle: End() is never called and
//    the program keeps running after the control is gone.
// ❌ One control hosting TWO programs "for reactivity": one element, one
//    program. Compose elements instead (§1).
```

## 3. Routing notifications: commands, not compute

`update` answers one question: what is the next model? Everything that
reaches outside — parent callbacks, logging, navigation — is a command.

```fsharp
// ❌ Invoking the callback inside the compute part: a hidden side effect on
// every path through update, invisible to tests, ordered by accident.
| Increment ->
    onIncrement ()
    { model with Count = model.Count + 1 }, Cmd.none

// ✅ The same notification as a command: runs after the model is applied,
// on the program's command path, with its errors routed to the error handler.
| Increment ->
    { model with Count = model.Count + 1 }, Cmd.ofAction onIncrement
```

Notes:

- `Cmd.ofAction (unit -> unit)` is for exactly this; its action owns its own
  error handling (exceptions it throws are not caught by the program).
  Use `Cmd.ofEffect` when the callback must dispatch follow-up messages.
- C#: `Command.OfAction(...)` / `Command.OfEffect(...)` on the same paths.

## 4. Props records as the surface grows

Three-plus parameters — especially optionals and callbacks — become a record.
The component keeps the same contract: props in, events out.

```fsharp
type CounterProps =
    { Init: int
      OnIncrement: unit -> unit
      OnDecrement: unit -> unit
      OnChanged: int -> unit }          // events out scale with the surface

type CounterControl(props: CounterProps) as this =
    inherit UserControl()
    // Case A hosting (§2): this component is replaced when done, never
    // re-shown, so the handle can be dropped.
    do
        this.Content <-
            Counter.program props.OnIncrement props.OnDecrement props.Init
            |> Program.runWithLifetime this ()
            |> fst

    // ...and in update: { model with Count = n }, Cmd.ofAction (fun () -> props.OnChanged n)
```

```fsharp
// ❌ Positional sprawl — every caller fights the order, every addition breaks them:
type Stepper(?init, ?step, ?min, ?max, ?onUp, ?onDown, ?onChanged, ?format) as this = ...
// ✅ Same surface as one props record with an init helper:
type StepperProps = { Init: int; Step: int; Min: int; Max: int; ... }
Stepper(StepperProps.step defaultConfig 5)
```

## 5. External reactive state inside a component

The rule: a store or service stream is bridged by a **program's** `subscribe`
— never bound from a view, never passed through props as the store itself.
The bridge lives in exactly one of two places, depending on who the component
belongs to.

**Wiring 1 — the component is app infrastructure and owns the bridge.** Its
own program registers the subscription: the engine keys it, reconciles it
after every update, and disposes it at teardown, so the component's controls
are released with its program (mvuish-controls §2 and §4).

```fsharp
module PresenceBadge =

    type Model = { Status: string }
    type Msg = PresenceChanged of string

    /// The store never reaches a view or a prop. It enters here, keyed.
    let subscribe _ =
        PresenceStore.changes                      // app-lifetime source
        |> Observable.map PresenceChanged
        |> Sub.fromObservable "presence"

    let update msg model =
        match msg with
        | PresenceChanged status -> { model with Status = status }, Cmd.none

    let view (model: IObservable<Model>) dispatch =
        Elements.badge (model |> Observable.map (fun m -> m.Status))

type PresenceBadge() as this =
    inherit UserControl()

    do
        let program =
            Program.mkProgram
                (fun () -> { PresenceBadge.Model.Status = "offline" }, Cmd.none)
                PresenceBadge.update
                PresenceBadge.view
            |> Program.withSubscription PresenceBadge.subscribe

        this.Content <-
            program
            |> Program.runWithLifetime this ()     // Case A hosting (§2)
            |> fst
```

The cost: this component names `PresenceStore`, so it is app-specific. That
is fine for widgets like clocks, session badges, or theme surfaces; wrong for
anything meant to be reused across contexts — use wiring 2.

**Wiring 2 — the component is reusable and the PARENT owns the bridge.**
The store never reaches the component: the parent's program bridges it into
the parent's `Model`, and the component receives values (or a slice of the
parent's model stream) plus callbacks. The full loop, every hop labeled:

```fsharp
// ─── The store: app-lifetime, never completes, never bound by a view. ───
module SessionStore =
    let changes: IObservable<User> = ...   // rooted for the whole process

// ─── The PARENT program: the only place the store is named. ─────────────
module Page =

    type Model = { UserName: string }
    type Msg = SessionChanged of User

    /// Hop 1 — store → Msg. A keyed subscription; the engine owns it and
    /// disposes it when the parent program ends.
    let subscribe _ =
        SessionStore.changes
        |> Observable.map SessionChanged
        |> Sub.fromObservable "session"

    /// Hop 2 — Msg → Model. Store data becomes owned, plain state.
    let update msg model =
        match msg with
        | SessionChanged user -> { model with UserName = user.Name }, Cmd.none

    /// Hop 3 — Model → component. The parent's view runs ONCE, so anything
    /// live is handed over as a slice of the parent's own model stream,
    /// plus callbacks for events out.
    let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
        UserCard {
            Name   = model |> Observable.map (fun m -> m.UserName)
            OnEdit = fun () -> dispatch EditRequested
        }

// ─── The component: values and callbacks in, events out. No store. ──────
type UserCardProps =
    { Name: IObservable<string>     // a slice of the PARENT's model stream
      OnEdit: unit -> unit }        // events out

module UserCard =

    type Model = { Name: string }
    type Msg = NameChanged of string | EditClicked

    /// Hop 4 — the slice arrives as messages; the parent's callback leaves
    /// as a command (§3). The store is nowhere in this module.
    let update (onEdit: unit -> unit) msg model =
        match msg with
        | NameChanged name -> { model with Name = name }, Cmd.none
        | EditClicked -> model, Cmd.ofAction onEdit

    let view (model: IObservable<Model>) dispatch =
        Elements.card
            (model |> Observable.map (fun m -> m.Name))
            (fun () -> dispatch EditClicked)

type UserCard(props: UserCardProps) as this =
    inherit UserControl()

    do
        let program =
            Program.mkProgram
                (fun () -> { UserCard.Model.Name = "" }, Cmd.none)
                (UserCard.update props.OnEdit)
                UserCard.view
            |> Program.withSubscription (fun _ ->
                props.Name
                |> Observable.map UserCard.NameChanged
                |> Sub.fromObservable "name")

        this.Content <-
            program
            |> Program.runWithLifetime this ()  // Case A hosting (§2)
            |> fst
```

If the card were purely presentational, skip the stateful component
entirely: a stateless element (§1) taking the same slice renders with one
program and no mirroring. Reach for a component when it owns behavior, not
because values change.

The only forbidden hop is skipping the bridge — handing the store itself to
the component as a prop, or to a view as a binding:

```fsharp
// ❌ The bridge is skipped: this prop IS the app-lifetime source, so any
// view that binds it is retained for the life of the process
// (mvuish-controls §2) — and the component now secretly depends on
// SessionStore while someone else owns its lifetime.
UserCard { Name = SessionStore.changes; OnEdit = ignore }
```

Rule of thumb: **observables in props are fine; observables that outlive the
parent program are not.** A slice of the parent's model stream dies with the
parent program; a store observable never dies.

Mechanisms, ids, and lifetime trade-offs:
[../mvuish-controls/SKILL.md](../mvuish-controls/SKILL.md) §2, §4, §7.

## 6. NXUI specifics

The DSL-agnostic shapes above translate to NXUI builders like this:

```fsharp
open NXUI.Extensions

module Elements =

    /// val counter: count: IObservable<int> -> onIncrement: ... -> onDecrement: ... -> Control
    let counter (count: IObservable<int>) (onIncrement: unit -> unit)
                (onDecrement: unit -> unit) : Control =
        StackPanel().Children(
            TextBlock().Text(count |> Observable.map string),
            Button().Content("+").OnClick(fun _ obs ->
                obs |> Observable.add (fun _ -> onIncrement ())),
            Button().Content("-").OnClick(fun _ obs ->
                obs |> Observable.add (fun _ -> onDecrement ()))
        )
```

- **Builders return the control.** An element function is one chained
  expression ending in its root control. Annotate the signature `: Control` —
  F# converts at the annotated return, so no `:> Control` noise in the body —
  and every caller can mix the result into `.Children(...)`. Signature docs
  (the `val` line above) are how these shapes are communicated; the
  implementation stays upcast-free.
- **`.Children(...)` takes `Control`s** — the reason rule §1.2 exists.
- **Event builders hand an args observable** — `.OnClick(fun _ obs -> ...)`.
  Subscribing there is the only `Observable.add` allowed in view code; it
  lives and dies with the control (mvuish-controls §3).
- **F# open order**: `open NXUI.Extensions` *before* `open MVUish.Avalonia*`
  namespaces so shadowing resolves to MVUish bindings (mvuish-controls §6).
- C# mirrors it with `Action` callbacks and `static Control Counter(...)`:

```csharp
public static Control Counter(IObservable<int> count, Action onIncrement, Action onDecrement) =>
    StackPanel().Children(
        TextBlock().Text(count.Select(c => c.ToString())),
        Button().Content("+").OnClick(e => e.Subscribe(_ => onIncrement())),
        Button().Content("-").OnClick(e => e.Subscribe(_ => onDecrement())));
```

- In C#, stateful components are the same idea with a `UserControl` subclass:
  `Content = Program.MkProgram(...).RunWithLifetime(this, arg).View;` —
  `RunWithLifetime` returns a `ProgramHandle` exposing `.View` and `.Lifetime`,
  so a host that must own disposal keeps the handle instead of taking `.View`.
  Use `Command.OfAction` for the notification commands, and a props class or
  record when the parameter list grows.
