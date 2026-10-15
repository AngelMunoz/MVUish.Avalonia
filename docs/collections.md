---
categoryindex: 0
index: 4
title: Collections
category: Documentation
---

# Collections

Assigning a new list to `ItemsSource` on every model change rebuilds every item container: selection, scroll position, and any state a row holds are lost. The MVUish.Avalonia collection bindings instead keep one owned `ObservableCollection` as `ItemsSource` and apply each snapshot from the model as minimal deltas, so containers are reused.

Both bindings return `IDisposable`, assign `ItemsSource` once, and unsubscribe when the control detaches from the visual tree.

## Keyed binding (preferred)

Match items by a stable key. Items whose key disappears are removed, new keys are inserted, a surviving key with a changed item raises a `Replace`, and reorders raise `Move` events.

```fsharp
open NXUI.Extensions
// Open the MVU namespaces after NXUI: F# extension shadowing must pick
// these bindings over the NXUI property binder of the same name.
open MVUish.Avalonia
open MVUish.Avalonia.Collections

type Note = { Id: Guid; Text: string }

let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
  let notes = model |> Observable.map (fun m -> m.Notes)
  let listBox = ListBox().MinHeight(160.0)

  // The list-typed overload accepts IObservable<Note list> directly.
  listBox.BindItemsSource(notes, (fun n -> n.Id)) |> ignore

  listBox
```

```csharp
using MVUish.Avalonia.Collections;

var listBox = new ListBox { MinHeight = 160 };
CollectionExtensions.BindItemsSource(listBox, model.Select(m => m.Notes), n => n.Id);
```

A snapshot that repeats a key throws `ArgumentException`. Inside a `Program` view the throw surfaces through the program's `OnError` handler instead of crashing the app.

> NXUI ships its own `BindItemsSource` extension that binds the raw `ItemsSource` property. In F#, open `MVUish.Avalonia.Collections` after `NXUI.Extensions` so shadowing picks this adapter, as shown above. In C#, call `CollectionExtensions` statically as shown above.

## Structural binding

`BindItemsSourceStructural` (`Extensions.BindItemsSourceStructural` in F#, `CollectionExtensions.BindItemsSource` without a key in C#) diffs by structural equality instead of keys. Repeated values are allowed. Prefer the keyed binding: it also matches moved items.

## Whole-list fallback

For short lists where none of the row state matters, `BindStream` on `ItemsControl.ItemsSourceProperty` still works: every emission reassigns the whole list and Avalonia resets the control. That is the cheapest option for a handful of items.

## Threading

Both bindings apply changes on the thread that emits the snapshot. `Program` dispatches on the UI thread, so model streams need nothing extra. Sources that emit from background threads must hop first, for example with `Observable.observeOn` on `Avalonia.Threading.Dispatcher.UIThread`.
