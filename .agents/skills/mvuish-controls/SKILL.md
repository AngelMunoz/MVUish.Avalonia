---
name: mvuish-controls
description: Structure Avalonia controls and views for MVUish.Avalonia — view functions that run once over an IObservable<Model>, NXUI-style property and event bindings, BindItemsSource collections, and control lifetimes (runWithLifecycle / runWithLifetime). Use whenever writing, reviewing, or changing view functions, bindings, lists, event handlers, or page lifecycles in F# or C# code that uses MVUish, or when asked about MVUish bindings, subscriptions, or NXUI builders in this repo.
---

# MVUish controls: views, bindings, and lifetimes

MVUish.Avalonia is Model-Update-Observable, not VDOM: the `view` function runs
**once**, receives `IObservable<Model>` plus `dispatch`, and every control
property that changes subscribes to its own slice of the stream. There is no
tree diffing and no wrapper controls — views return real Avalonia controls,
usually built with NXUI's code-first builders.

Sources of truth for this skill: `src/MVUish.Avalonia/Library.fs` and the docs
in this repository (`docs/reactive-mvu.md`, `docs/collections.md`,
`docs/subscriptions.md`, `docs/getting-started.md`).

## 1. The view function

A module has the four MVU pieces; the view differs from classic Elmish by
running once and taking a stream:

```fsharp
let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
    let countText = model |> Observable.map (fun m -> sprintf "Count: %d" m.Count)

    StackPanel().Children(
        TextBlock().Text(countText),
        Button().Content("Increment").OnClick(fun _ obs ->
            obs |> Observable.add (fun _ -> dispatch Increment))
    )
```

```csharp
public static Control View(IObservable<Model> model, Action<Msg> dispatch)
{
    var countText = model.Select(m => $"Count: {m.Count}");
    return StackPanel().Children(
        TextBlock().Text(countText),
        Button().Content("Increment").OnClick(e =>
            e.Subscribe(_ => dispatch(new Msg.Increment()))));
}
```

- Slice per property with `Observable.map` / `Select`; hand each child module
  its own slice and dispatch (`ClockMsg >> dispatch`) for composition.
- The program builds the view with `Program.mkProgram init update view`
  (C# `Program.MkProgram`; `MkSimple` when there are no commands) and
  `Program.run` returns the built control for `StartWithClassicDesktopLifetime`.
- Keep side effects in `update`/commands. The view only dispatches.

## 2. Property bindings and the lifetime of their source

Bind properties through the stream: NXUI-style setters (`.Text(obs)`,
`.IsVisible(obs)`, …) or the generic `Extensions.BindStream(control, property,
source)`. Which source you bind decides whether the control can ever be
collected. This is the single most important lifetime rule in the library.

### The mechanism

`BindStream` forwards to Avalonia's `AvaloniaObject.Bind`, which registers an
observer with the SOURCE and writes arriving values into the control's
property entry. While that subscription exists, the source's observer list
references the binding's observer, and that observer holds the control entry
it writes into: **the root is the source, and the control hangs off it.** The
GC cannot collect the control while the source is alive.

A binding ends in only one of these ways:

1. the source **completes or errors** — completion releases the observer list;
2. someone **disposes the subscription** — but `BindStream` and every NXUI
   fluent setter discard the `IDisposable` Avalonia returns, so in normal view
   code this door is closed;
3. the property is **re-bound** — the new binding replaces the entry;
4. the source itself becomes unreachable — only possible when nothing roots it.

Detaching the control from the visual tree ends nothing: the binding keeps
receiving and applying values while detached.

### Dispose is not release

For subjects, calling `Dispose()` does not remove subscribers — **completion
is what unroots**. That is why program teardown does both,
`state.OnCompleted(); state.Dispose()` (in `Program.runWithLifecycle`):
completion releases every observer of the model `BehaviorSubject`, so every
control bound to the model stream becomes collectible, and each bound property
reverts to its default (`Text` → null, `Width` → NaN) instead of freezing on
the last value.

### The app-lifetime case

A store, service, or singleton exposing an `IObservable` / `BehaviorSubject`
is rooted for the whole process and never completes. Bind any control property
to it and that control is retained for the life of the process: a scrolled-off
feed row, a closed flyout, a swapped-out page — each pinned by one
`.Text(obs)` / `.Background(obs)` whose disposable nobody holds. This is the
classic MVUish consumer leak.

Rules that follow:

1. Bind view properties only to the program's model stream or streams derived
   from it (`Observable.map` / `Select` slices) — teardown completes the model
   stream and releases every binding with the program.
2. App-lifetime state reaches the UI as **messages**: bridge the source in the
   program's subscription set (`Sub.fromObservable` / `Sub.ofId`, see 4) and
   project it into the model. The view keeps binding only model slices.
3. Static or themed values (brushes, icons, fixed text) need no binding at
   all: use style classes or plain assignments.
4. If a control truly must observe an external stream, keep the subscription
   `Bind` returns yourself and dispose it on the same path that tears the
   control down. Fluent setters and `BindStream` throw that disposable away;
   never rely on detach to clean up.

### Good and bad shapes

Model slice vs app-lifetime source:

```fsharp
// ✅ A slice of THIS view's model stream — completes with the program.
let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
    TextBlock().Text(model |> Observable.map (fun m -> m.Title))

// ❌ A store / service / singleton stream — never completes, so this
// TextBlock (and its whole subtree) is retained for the life of the process.
let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
    TextBlock().Text(SessionStore.user) // app-lifetime subject
```

(C# is the same shape: `model.Select(m => m.Title)` vs `SessionStore.User`.)

Dispose vs completion:

```fsharp
// ❌ Disposing the subject does not free the views bound to it:
subject.Dispose() // observers stay attached, controls stay rooted

// ✅ Completion is what unroots (what Program.runWithLifecycle does):
subject.OnCompleted() // every binding ends, bound properties revert
subject.Dispose()
```

Detach is not cleanup:

```fsharp
// ❌ "It's off the visual tree, it will be collected" — it will not:
// the binding keeps applying values to the detached control.
panel.Children.Remove(label)
```

Kept disposable vs orphaned subscription:

```fsharp
// ✅ The one external-binding shape: keep the IDisposable, end it on teardown.
let attachTicker (host: StackPanel) (ticks: IObservable<string>) =
    let label = TextBlock()
    let sub = label.Bind(TextBlock.TextProperty, ticks) // kept, not discarded
    host.Children.Add label
    sub // the caller disposes this when the host is torn down

// ❌ Same intent, orphaned subscription — nothing can ever end it:
let attachTicker (host: StackPanel) (ticks: IObservable<string>) =
    let label = TextBlock()
    ticks.Subscribe(fun t -> label.Text <- t) |> ignore
    host.Children.Add label
```

## 3. Events and dispatch

NXUI event builders hand you an observable of the event args for that
control's own event — subscribing to it is the one allowed `Observable.add`
inside a view; it lives and dies with the control.

`dispatch` is thread-safe: update and dispatch are marshalled to the UI
thread, so handlers can fire from any thread.

## 4. External sources enter through Sub

Anything that produces messages from outside the model — timers, .NET events,
shared services, `IObservable`s, routed events — belongs in the program's
subscription set (`Program.withSubscription`), never in a view binding:

- Build with `Sub.none` / `ofId` / `batch` / `map` / `interval` / `fromEvent` /
  `fromObservable` / `fromRoutedEvent` / `untilDetached`
  (C# `Subscription.*` producing `CSub<Msg>`). The engine re-evaluates the set
  after every update and starts/stops sources to match; teardown disposes them.
- Ids are string lists: `Sub.map` prefixes child ids so sibling modules never
  collide, and embedding a changing value in an id (e.g.
  `["ticker"; string intervalMs]`) restarts the source when it changes.
- `Sub.untilDetached visual sub` ends the set when the control detaches — use
  only for work that is genuinely per-attach.
- A view function must not carry an external observable for binding, and no
  `Observable.add` / `.Subscribe` against long-lived streams in view code.

### Good and bad shapes

```fsharp
// ✅ App-lifetime source bridged as messages; the view still binds only slices.
let subscribe _ =
    SessionStore.changes                 // IObservable<UserChange>, rooted forever
    |> Observable.map SessionChanged
    |> Sub.fromObservable "session"      // the engine owns and disposes this

let update msg model =
    match msg with
    | SessionChanged u -> { model with User = u }, Cmd.none

let view (model: IObservable<Model>) dispatch =
    TextBlock().Text(model |> Observable.map (fun m -> m.User.Name))

// ❌ Subscribing to the store inside the view — the store now roots this label.
let view (model: IObservable<Model>) dispatch =
    let label = TextBlock()
    SessionStore.changes.Subscribe(fun u -> label.Text <- u.Name) |> ignore
    label

// ❌ Smuggling the observable through props/records for the view to bind —
// the retention chain is identical, only harder to see.
type PageProps = { User: IObservable<User> }
```

## 5. Common leak classes

A binding roots its control from the source, so every long-lived source a view
touches is a potential retention. Audit new view code against these classes:

1. **App-lifetime sources behind property bindings** (see 2): the control
   survives for the process, even after detaching. Model-stream bindings are
   safe — they die with the program's teardown.
2. **Decoded images.** Every `new Bitmap` / `DecodeToWidth` site needs an
   owner and a drop path: dispose the old bitmap on replace and on detach.
3. **Timers and long-lived callbacks.** Anything started inside a control must
   stop on every path (replace, detach, dispose). Prefer `Sub.interval` in the
   program over ad-hoc timers inside views.
4. **Inline-hosted controls.** Controls hosted in a `TextBlock` inline
   collection detach and re-attach on every layout pass
   (`ArrangeOverride` rebuilds inlines) — never key lifecycle or teardown
   logic on detach for those.
5. **Rebuilt content without an identity guard.** `ContentControl().Content(obs)`
   rebuilds whole subtrees per emission; gate the rebuild on an identity
   (user id, page id) so per-item programs and bindings don't accumulate.
6. **Stashed programs.** A page program kept alive "for later" keeps its whole
   binding graph alive. End it (dispose the `runWithLifecycle` handle) and let
   returning state live in a service, restored as model data on re-entry.

### Good and bad shapes

Decoded images (one owner, two drop paths — replace and detach):

```csharp
// ✅
void ShowImage(string path)
{
    (image.Source as IDisposable)?.Dispose();
    image.Source = Bitmap.DecodeToWidth(File.OpenRead(path), 400);
}
image.DetachedFromVisualTree += (_, _) => {
    (image.Source as IDisposable)?.Dispose();
    image.Source = null;
};

// ❌ A new bitmap per emission, nothing ever disposed:
model.Select(m => LoadBitmap(m.Path)).Subscribe(b => image.Source = b);
```

Timers:

```fsharp
// ✅ The program owns the timer; teardown stops it.
let subscribe _ =
    Sub.interval "autosave" (TimeSpan.FromSeconds 30.) (fun () -> AutosaveTick)

// ❌ An ad-hoc timer with a single stop path (detach): replacing the control
// while attached leaves the timer — and its closure — running forever.
let timer =
    DispatcherTimer(TimeSpan.FromSeconds 1., DispatcherPriority.Background,
        fun _ -> label.Text <- stamp ())
timer.Start()
label.DetachedFromVisualTree.Add(fun _ -> timer.Stop())
```

Inline-hosted controls:

```fsharp
// ❌ Teardown keyed on detach inside a TextBlock inline: inlines rebuild on
// every layout pass, so this fires constantly and re-runs forever.
run.DetachedFromVisualTree.Add(fun _ -> cts.Cancel())

// ✅ Scope the work to the owning page program (Sub / program handle), not to
// the attach/detach cycles of an inline-hosted control.
```

Identity guards:

```csharp
// ✅ Rebuild chrome only when the identity actually changes.
var chrome = session
    .Select(s => s.UserId)
    .DistinctUntilChanged()
    .Select(_ => BuildChrome(session));
chromeHost.Bind(ContentControl.ContentProperty, chrome);

// ❌ Rebuild per emission: every update constructs a new subtree — and if each
// subtree hosts a program, the old programs keep running underneath.
var chrome = session.Select(_ => BuildChrome(session));
```

Stashed programs:

```fsharp
// ✅ End the page program on navigate-away; return-state lives in a service.
let leavingPage () = lifetime.Dispose ()

// ❌ Cache the finished page "to come back faster": its program keeps running
// and every binding it created stays alive the whole time.
let pageCache = Dictionary<string, Control>()
```

## 6. Lists: BindItemsSource, not ItemsSource

Assigning a new list to `ItemsSource` per update rebuilds every container
(selection, scroll, row state lost). Use the collection bindings; both assign
`ItemsSource` once and apply minimal deltas, returning the control so they
chain like any NXUI builder. The binding edge is control-owned: snapshots keep
applying while the control is detached, and the binding ends itself on the
first emission after the control is collected — rows release with the program,
with no per-row disposable to track.

| Situation | Use |
|---|---|
| Items have a stable identity (default choice) | `BindItemsSource(items, key)` — keyed diff, handles moves |
| No natural key; duplicate values allowed | `BindItemsSourceStructural` (structural diff) |
| Short list, no row state that matters | `BindStream` on `ItemsControl.ItemsSourceProperty` — whole-list reset |

Rules for rows:

1. `ListBox` and friends virtualize and Avalonia recycles containers. Row state
   that must survive scrolling lives in the model; handlers read the **live
   `DataContext`**, never a captured item, and rehydrate in
   `DataContextChanged`.
2. Virtualized lists with state need `FuncDataTemplate(..., supportsRecycling
   = true)`. `supportsRecycling = false` is only safe when the list cannot
   scroll, or rows are short-lived by design (a consumer tradeoff — do not
   "fix" it blindly in app repos).
3. Commit text on focus loss: dispatching per keystroke races the model update
   and overwrites the character being typed.
4. Repeated keys throw `ArgumentException`; inside a program view it surfaces
   through the program's error handler instead of crashing.
5. Bindings apply on the emitting thread. Model streams are already on the UI
   thread; background sources must hop first (`Observable.observeOn` /
   `ObserveOn` the Avalonia UI thread dispatcher).
6. F# open order matters: `open NXUI.Extensions` **before**
   `open MVUish.Avalonia` / `open MVUish.Avalonia.Collections`, so F# extension
   shadowing picks the MVUish `BindItemsSource` over NXUI's same-named binder.
   In C#, call `CollectionExtensions.BindItemsSource(...)` statically.
7. Trim/AOT: keep row sync in code, not `ReflectionBinding`.

### Good and bad shapes

```fsharp
// ✅ MVUish keyed binding: one owned collection, per-item deltas, reused containers.
ListBox().BindItemsSource(notes, (fun n -> n.Id))

// ❌ NXUI's same-named binder (or BindStream on ItemsSourceProperty):
// reassigns the whole list per emission, rebuilding every container
// (selection, scroll position, and row state go with it).
ListBox().BindItemsSource(notes)
```

Captured item vs live `DataContext`:

```fsharp
// ❌ The template closure's note: after recycling, the handler edits the wrong row.
checkBox.IsCheckedChanged.Add(fun _ ->
    dispatch (SetChecked(note.Id, checkBox.IsChecked.GetValueOrDefault false)))

// ✅ Read the control's live DataContext at event time.
checkBox.IsCheckedChanged.Add(fun _ ->
    match checkBox.DataContext with
    | :? Note as current ->
        dispatch (SetChecked(current.Id, checkBox.IsChecked.GetValueOrDefault false))
    | _ -> ())
```

## 7. Hosting a program in a control or page

- `Program.runWithLifecycle arg program : 'View * IDisposable` — returns the
  control and the handle that stops subscriptions, completes the model stream,
  and stops the update loop. Disposing twice is a no-op. Use it when your
  navigation host owns the lifetime.
- `Program.runWithLifetime control arg program` — same handle, disposed
  automatically when the control **first** detaches. Only for hosts that are
  replaced and never re-shown.
- A detached program does not restart on reattach: a control that detaches and
  re-attaches by design (cached/stashed page) must use `runWithLifecycle` with
  a host-owned handle, or run a fresh program when shown again.
- `Program.withTermination predicate handler` ends the program from a message
  (the Elm exit pattern) and hands the final model to the handler.
- After teardown (dispose or termination), bound properties revert to their
  defaults (`Text` → null, `Width` → NaN). Don't rely on frozen values; re-bind
  to a new program's stream if the control lives on.

### Good and bad shapes

```fsharp
// ✅ Replace-only page: release on first detach, nothing to track.
let content, _lifetime = Program.runWithLifetime host arg pageProgram

// ✅ Cached/stashed tab: the HOST owns the handle and disposes it when the tab
// truly closes (hiding the tab only detaches it).
let content, lifetime = Program.runWithLifecycle arg pageProgram

// ❌ runWithLifetime on a re-shown host: the first detach killed the program;
// returning shows a control bound to a completed stream — default values.

// ❌ A fresh program per navigation while old handles are never disposed:
// every old program keeps running and every old binding graph stays alive.

// ❌ Expecting a control to keep its values after its program ended:
// bound properties revert to defaults — re-bind to a new program's stream.
```

## 8. C# surface parity

`MVUish.Avalonia.CSharp` mirrors the F# API: `Program.MkProgram` / `MkSimple` /
`Run` / `RunWith` / `RunWithLifecycle` / `RunWithLifetime`,
`Command.None/OfMsg/Batch/Map/Perform...`, `Subscription.None/Interval/
FromEvent/FromObservable/UntilDetached...` (`CSub<Msg>`), and
`CollectionExtensions.BindItemsSource`. When adding or changing an F# API in
this library, add the C# counterpart in the same change and keep the naming
table in `docs/reactive-mvu.md` accurate.
