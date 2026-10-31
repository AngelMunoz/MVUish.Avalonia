---
name: mvuish-performance
description: Optimize observable streams, view slices, update loops, and collection bindings in MVUish.Avalonia — multicasting shared streams with connectable observables (Observable.replayBuffer 1 >> Observable.refCount / .Replay(1).RefCount()), avoiding the Publish().RefCount() initial-value trap on BehaviorSubject model streams, cutting redundant emissions with distinctUntilChanged and ReferenceEqualityComparer, lensing child modules, reference-equality short-circuiting in update, throttling high-frequency inputs, and avoiding O(N*M) structural collection diffs or subtree rebuilds. Use whenever writing, reviewing, or optimizing MVUish views, observable pipelines, child module composition, large lists, or high-frequency subscriptions in F# (FSharp.Control.Reactive) or C# (System.Reactive).
---

# MVUish performance: observable streams, slices, and updates

MVUish.Avalonia runs `view` **once** and drives the UI through an
`IObservable<'Model>` backed by a `BehaviorSubject<'Model>`. Every performance
rule in an MVUish application follows from three runtime facts:

1. **The root `model` stream is hot and replays its latest value**, because it
   is a `BehaviorSubject<'Model>`.
2. **Every `Observable.map` / `.Select` slice is cold per subscriber**: each
   bound property or child view that subscribes to a derived stream runs the
   upstream operators independently unless the slice is multicasted.
3. **Every accepted message emits on the root `model` stream** unless `update`
   returns the exact same model reference (`obj.ReferenceEquals(current,
   newModel)`).

F# examples below use `FSharp.Control.Reactive` (`open FSharp.Control.Reactive`);
C# examples use Reactive Extensions (`using System.Reactive.Linq;`).

## 1. Share derived streams with connectables (`replayBuffer 1 >> refCount`)

`Observable.map` (C# `.Select`) does not cache or share its result. When $N$
control properties bind to one derived stream, subscribing $N$ controls creates
$N$ independent subscriptions and runs the mapping function $N$ times per model
update.

### The `BehaviorSubject` initial-value trap: `publish` vs `replayBuffer 1`

Because the program's `model` stream is a `BehaviorSubject<'Model>`, it emits
`initModel` synchronously inside `.Subscribe(...)`. If you multicast a derived
slice with plain `Observable.publish >> Observable.refCount` (C#
`.Publish().RefCount()`):

1. The **first** control to bind in `view` increments the subscriber count from
   `0` to `1`. `RefCount` connects to `model` immediately, `BehaviorSubject`
   pushes `initModel` synchronously through the inner `Subject`, and the first
   control receives the initial value.
2. The **second** control binds on the next line. Subscriber count goes from
   `1` to `2` on an already-connected `Subject`. Because a plain `Subject` has
   no buffer, the second control **misses the initial emission** and renders
   blank until the next message arrives.

Use a replay buffer of size `1` (`Observable.replayBuffer 1 >> Observable.refCount`
in F#, `.Replay(1).RefCount()` in C#) so the computed value is cached for all
controls that bind during `view`:

```fsharp
open FSharp.Control.Reactive
open NXUI.Extensions
open MVUish.Avalonia

let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) : Control =
    // ✅ Compute the expensive summary once per Items change and replay the
    // latest result to every control that binds to `summary` during view setup.
    let summary =
        model
        |> Observable.map (fun m -> m.Items)
        |> Observable.distinctUntilChanged
        |> Observable.map Stats.computeSummary
        |> Observable.replayBuffer 1
        |> Observable.refCount

    StackPanel().Children(
        TextBlock().Text(summary |> Observable.map (fun s -> $"Total: {s.Total}")),
        TextBlock().Text(summary |> Observable.map (fun s -> $"Mean: {s.Mean:F2}")),
        TextBlock().Text(summary |> Observable.map (fun s -> $"P95: {s.P95:F2}"))
    )

// ❌ Cold slice bound three times: Stats.computeSummary runs 3× on every model
// emission (even when m.Items is unchanged).
let badCold (model: IObservable<Model>) =
    let summary = model |> Observable.map (fun m -> Stats.computeSummary m.Items)
    StackPanel().Children(
        TextBlock().Text(summary |> Observable.map (fun s -> $"Total: {s.Total}")),
        TextBlock().Text(summary |> Observable.map (fun s -> $"Mean: {s.Mean:F2}")),
        TextBlock().Text(summary |> Observable.map (fun s -> $"P95: {s.P95:F2}"))
    )

// ❌ Plain publish + refCount on a BehaviorSubject upstream: the first TextBlock
// gets initModel; the second and third miss the synchronous initial emission.
let badPublish (model: IObservable<Model>) =
    model |> Observable.map (fun m -> Stats.computeSummary m.Items)
          |> Observable.publish |> Observable.refCount

// ❌ Unbounded Observable.replay: retains every historical summary in memory for
// the entire lifetime of the program. Always cap the buffer at 1.
let badUnboundedReplay (model: IObservable<Model>) =
    model |> Observable.map (fun m -> Stats.computeSummary m.Items)
          |> Observable.replay |> Observable.refCount
```

```csharp
using System.Reactive.Linq;
using Avalonia.Controls;
using NXUI.Extensions;

public static Control View(IObservable<Model> model, Action<Msg> dispatch)
{
    // ✅ One upstream computation per Items change; every subscriber gets the
    // latest value immediately on subscription.
    var summary = model
        .Select(m => m.Items)
        .DistinctUntilChanged()
        .Select(Stats.ComputeSummary)
        .Replay(1)
        .RefCount();

    return StackPanel().Children(
        TextBlock().Text(summary.Select(s => $"Total: {s.Total}")),
        TextBlock().Text(summary.Select(s => $"Mean: {s.Mean:F2}")),
        TextBlock().Text(summary.Select(s => $"P95: {s.P95:F2}")));
}

// ❌ Do NOT use `Observable.publish` + `Observable.connect |> ignore` inside a
// view: `connect` returns the `IDisposable` that owns the upstream subscription.
// Discarding it violates the lifetime rules (see mvuish-controls §2): if this
// view is a child or dynamic subtree over a longer-lived parent stream, the
// orphaned connection keeps running on the parent stream after the child leaves.
// Always let `replayBuffer 1 >> refCount` (C# `.Replay(1).RefCount()`) manage
// the connection lifetime automatically.
```

## 2. Cut redundant emissions with `distinctUntilChanged`

Whenever *any* field of the root `Model` changes, the program pushes the new
`Model` to every subscriber of `model`. Without a deduplication gate, a 1 Hz
clock tick re-evaluates every string interpolation, brush lookup, and visibility
check in the window.

- **Primitives, enums, discriminated unions, and small flat records**: use
  `Observable.distinctUntilChanged` (C# `.DistinctUntilChanged()`), or
  `Observable.distinctUntilChangedKey` (C# `.DistinctUntilChanged(m => m.Key)`).
- **Sub-models or records that hold collections (`'T list`, `Map`, `Set`, arrays)**:
  F# structural equality (`=`) walks the collection elements whenever two
  instances are compared. When `update` only replaces a sub-model on its own
  messages (`{ model with Clock = newClock }` keeps `model.Notes` as the exact
  same object reference), gate with `ReferenceEqualityComparer.Instance` for an
  $O(1)$ pointer check:

```fsharp
open System.Collections.Generic
open FSharp.Control.Reactive

// ✅ O(1) reference check on a sub-model that holds a large list:
let notesModel =
    model
    |> Observable.map (fun m -> m.Notes)
    |> Observable.distinctUntilChangedCompare ReferenceEqualityComparer.Instance

// ✅ Keyed deduplication before allocating a formatted string:
let headerText =
    model
    |> Observable.distinctUntilChangedKey (fun m -> m.User.Id, m.UnreadCount)
    |> Observable.map (fun m -> $"{m.User.DisplayName} ({m.UnreadCount})")

// ❌ Deduplicating AFTER formatting allocates the string on every unrelated
// message just to compare it and throw it away:
let badHeaderText =
    model
    |> Observable.map (fun m -> $"{m.User.DisplayName} ({m.UnreadCount})")
    |> Observable.distinctUntilChanged
```

```csharp
using System.Collections.Generic;
using System.Reactive.Linq;

var notesModel = model
    .Select(m => m.Notes)
    .DistinctUntilChanged(ReferenceEqualityComparer.Instance);

var headerText = model
    .DistinctUntilChanged(m => (m.User.Id, m.UnreadCount))
    .Select(m => $"{m.User.DisplayName} ({m.UnreadCount})");
```

## 3. Lensing child modules: the complete slice pipeline

When a parent `view` slices `model: IObservable<ParentModel>` to hand a child
module its `IObservable<ChildModel>`, a plain `Observable.map (fun m -> m.Child)`
wakes every binding in the child on every sibling message, and each child
binding subscribes through the parent's `map` independently.

Combine `map`, `distinctUntilChangedCompare ReferenceEqualityComparer.Instance`
(or `distinctUntilChanged`), and `replayBuffer 1 >> refCount` at each child
boundary:

```fsharp
let view (model: IObservable<DashboardModel>) (dispatch: Dispatch<DashboardMsg>) : Control =
    // ✅ Stops unrelated parent updates in O(1) and shares one upstream
    // subscription across all controls inside the child view.
    let clockObs =
        model
        |> Observable.map (fun m -> m.Clock)
        |> Observable.distinctUntilChanged
        |> Observable.replayBuffer 1
        |> Observable.refCount

    let notesObs =
        model
        |> Observable.map (fun m -> m.Notes)
        |> Observable.distinctUntilChangedCompare ReferenceEqualityComparer.Instance
        |> Observable.replayBuffer 1
        |> Observable.refCount

    StackPanel().Children(
        Clock.view clockObs (ClockMsg >> dispatch),
        Notes.view notesObs (NotesMsg >> dispatch)
    )
```

```csharp
public static Control View(IObservable<DashboardModel> model, Action<DashboardMsg> dispatch)
{
    var clockObs = model.Select(m => m.Clock).DistinctUntilChanged().Replay(1).RefCount();
    var notesObs = model.Select(m => m.Notes)
        .DistinctUntilChanged(ReferenceEqualityComparer.Instance)
        .Replay(1).RefCount();

    return StackPanel().Children(
        ClockModule.View(clockObs, msg => dispatch(new DashboardMsg.Clock(msg))),
        NotesModule.View(notesObs, msg => dispatch(new DashboardMsg.Notes(msg))));
}
```

## 4. Short-circuit no-op updates in `update`

MVUish's program loop compares the previous and next model references before
emitting (`if not (obj.ReferenceEquals(current, newModel)) then state.OnNext newModel`).
When a message does not change the model, return the existing `model` reference
instead of constructing a new record with identical values. Check inside the
branch body rather than with a `when` guard so `match` / `switch` stays
exhaustively checked by the compiler:

```fsharp
let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | SelectTab tab ->
        // ✅ Exhaustive match arm; returns the existing `model` reference when
        // the tab is unchanged so the program skips OnNext.
        let nextModel =
            if tab = model.ActiveTab then model
            else { model with ActiveTab = tab }

        nextModel, Cmd.none

    | Decrement ->
        // ❌ Allocates a new record even when Count is already 0:
        // ReferenceEquals is false, so state.OnNext wakes every subscriber.
        { model with Count = max 0 (model.Count - 1) }, Cmd.none
```

```csharp
public static (Model, Command<Msg>) Update(Msg msg, Model model) =>
    msg switch
    {
        // ✅ Exhaustive switch arm with no `when` filter; returning `model`
        // preserves reference equality and short-circuits state.OnNext:
        Msg.SelectTab t =>
            (t.Tab == model.ActiveTab ? model : model with { ActiveTab = t.Tab },
             Command.None<Msg>()),

        // ❌ `model with { ... }` allocates a distinct clone even when Count
        // is already 0, triggering OnNext on every subscriber:
        Msg.Decrement =>
            (model with { Count = Math.Max(0, model.Count - 1) },
             Command.None<Msg>())
    };
```

## 5. Collection bindings and diffing cost

`BindItemsSource` diffs `oldItems` against `newItems` on every emission of the
observable you pass it. Gate the collection slice with `distinctUntilChanged`
(or `ReferenceEqualityComparer.Instance`) so unrelated model changes do not run
the diff pass:

```fsharp
open System.Collections.Generic
open FSharp.Control.Reactive
open NXUI.Extensions
open MVUish.Avalonia
open MVUish.Avalonia.Collections

let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) : Control =
    // ✅ O(1) reference gate: BindItemsSource only diffs when m.Items actually
    // points to a new list instance.
    let items =
        model
        |> Observable.map (fun m -> m.Items)
        |> Observable.distinctUntilChangedCompare ReferenceEqualityComparer.Instance

    ListBox().BindItemsSource(items, fun item -> item.Id)

// ❌ No distinctUntilChanged: every unrelated message runs an O(N) diff over m.Items.
let badList (model: IObservable<Model>) =
    ListBox().BindItemsSource(model |> Observable.map (fun m -> m.Items), fun item -> item.Id)
```

```csharp
var items = model
    .Select(m => m.Items)
    .DistinctUntilChanged(ReferenceEqualityComparer.Instance);

var list = CollectionExtensions.BindItemsSource(new ListBox(), items, item => item.Id);
```

| Binding | Time & space per emission | When to use |
|---|---|---|
| `BindItemsSource(items, keyOf)` | $O(N)$ with `HashSet<'K>` key lookups; emits `Insert`, `Remove`, `Set`, and `Move` deltas | Default for any list whose items have a stable ID |
| `BindItemsSourceStructural(items)` | $O(N \times M)$ time and space (allocates an `(oldCount + 1) * (newCount + 1)` LCS table per emission) | Only for small unkeyed lists (a few dozen items) where duplicates exist |
| `BindStream` on `ItemsSourceProperty` | Whole-list reset (rebuilds all item containers) | Tiny static/read-only lists where selection and scroll state do not matter |

## 6. High-frequency inputs and background streams (`throttle` and `sample`)

Every `dispatch` queues a message on the program's `MailboxProcessor`, posts to
`Dispatcher.UIThread.InvokeAsync`, runs `update`, and diffs the active
subscription set. Flooding `dispatch` from per-pixel pointer events, rapid
keystrokes, or high-rate sensors starves the UI thread. Shape the stream
**before** `dispatch`:

- **Search / filter text boxes**: debounce with `Observable.throttle` (C#
  `.Throttle`) so `dispatch` fires only after typing pauses.
- **Editable rows inside `BindItemsSource`**: commit on `LostFocus` rather than
  per keystroke so the row is not replaced mid-keystroke.
- **Pointer move / scroll / telemetry streams**: rate-limit with
  `Observable.sample` (C# `.Sample`) inside `Sub.fromObservable`.
- **Expensive transformations on external streams**: run filtering/parsing
  upstream before `Observable.observeOn` so background work stays off the UI
  thread, or move the work into `Cmd.OfTask` / `Cmd.OfValueTask`.

```fsharp
// ✅ Debounce text input before dispatching to the program loop:
let searchBox (dispatch: Dispatch<Msg>) : Control =
    TextBox().OnText(fun _ textObs ->
        textObs
        |> Observable.throttle (TimeSpan.FromMilliseconds 200.)
        |> Observable.distinctUntilChanged
        |> Observable.add (SearchQueryChanged >> dispatch))

// ✅ Sample a high-frequency sensor stream in Sub before it becomes messages:
let subscribe _ =
    SensorService.readings
    |> Observable.sample (TimeSpan.FromMilliseconds 50.)
    |> Observable.map ReadingArrived
    |> Sub.fromObservable "sensor-sampled"
```

```csharp
public static Control SearchBox(Action<Msg> dispatch) =>
    TextBox().OnText((_, textObs) =>
        textObs
            .Throttle(TimeSpan.FromMilliseconds(200))
            .DistinctUntilChanged()
            .Subscribe(q => dispatch(new Msg.SearchQueryChanged(q))));

public static CSub<Msg> Subscribe(Model _) =>
    Subscription.FromObservable(
        "sensor-sampled",
        SensorService.Readings
            .Sample(TimeSpan.FromMilliseconds(50))
            .Select(r => new Msg.ReadingArrived(r)));
```

## 7. Avoid control-tree rebuilds and hot-path command allocations

### Never rebuild controls per emission without a key guard

MVUish views build controls once. Binding `ContentControl.ContentProperty` (or
`Border.ChildProperty`) to `model |> Observable.map buildSubtree` allocates a
brand-new control tree on every message in the application:

```fsharp
// ✅ Gate by route discriminant so the page control is built only when the
// active page actually changes:
let pageHost (model: IObservable<Model>) (dispatch: Dispatch<Msg>) : Control =
    let page =
        model
        |> Observable.distinctUntilChangedKey (fun m -> m.Route)
        |> Observable.map (fun m -> renderPage m.Route model dispatch)

    ContentControl().Content(page)

// ❌ Rebuilds the page (and leaks any sub-programs inside it) on every message:
let badPageHost (model: IObservable<Model>) (dispatch: Dispatch<Msg>) : Control =
    ContentControl().Content(
        model |> Observable.map (fun m -> renderPage m.Route model dispatch))
```

```csharp
var page = model
    .DistinctUntilChanged(m => m.Route)
    .Select(m => RenderPage(m.Route, model, dispatch));
var host = ContentControl().Content(page);
```

### Use `Cmd.OfValueTask` / `Command.PerformValueTask` on cache-hit hot paths

When an async command frequently completes synchronously (such as reading from
an in-memory cache or a pooled channel), prefer `Cmd.OfValueTask.perform` (C#
`Command.PerformValueTask`). When `vt.IsCompleted` is true, MVUish dispatches the
result immediately without allocating a `Task` state machine:

```fsharp
let loadItem (id: ItemId) : ValueTask<Item> =
    match cache.TryGetValue id with
    | true, cached -> ValueTask<Item>(cached)
    | false, _ -> ValueTask<Item>(fetchFromNetworkAsync id)

let update msg model =
    match msg with
    | RequestItem id ->
        model, Cmd.OfValueTask.perform loadItem id ItemLoaded
```

```csharp
ValueTask<Item> LoadItem(ItemId id) =>
    cache.TryGetValue(id, out var cached)
        ? new ValueTask<Item>(cached)
        : new ValueTask<Item>(FetchFromNetworkAsync(id));

// In Update:
Command.PerformValueTask<ItemId, Item, Msg>(LoadItem, id, item => new Msg.ItemLoaded(item));
```
