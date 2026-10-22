---
categoryindex: 0
index: 4
title: Collections
category: Documentation
---

# Collections

Assigning a new list to `ItemsSource` on every model change rebuilds every item container: selection, scroll position, and any state a row holds are lost. The MVUish.Avalonia collection bindings instead keep one owned `ObservableCollection` as `ItemsSource` and apply each snapshot from the model as minimal deltas, so containers are reused.

Both bindings assign `ItemsSource` once and return the control, so they chain in NXUI-style builders like the rest of the library. There is no disposable to manage: a binding keeps applying snapshots while its control is alive — including while detached from the visual tree — and ends itself on the first emission after the control is collected.

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

  // The list-typed overload accepts IObservable<Note list> directly and
  // returns the control, so it chains with the NXUI builders.
  ListBox().MinHeight(160.0).BindItemsSource(notes, (fun n -> n.Id))
```

```csharp
using MVUish.Avalonia.Collections;

// Returns the list box, so the call chains in NXUI-style builders.
var listBox = CollectionExtensions.BindItemsSource(
    new ListBox { MinHeight = 160 }, model.Select(m => m.Notes), n => n.Id);
```

A snapshot that repeats a key throws `ArgumentException`. Inside a `Program` view the throw surfaces through the program's `OnError` handler instead of crashing the app.

## Virtualization and row state

**Rule: any list that virtualizes and needs to keep row state must use a template that supports recycling.** Pass `supportsRecycling: true` to `FuncDataTemplate<...>`, keep the state on the item (the model), and rehydrate the row controls from the live `DataContext`. A template that refuses recycling (`supportsRecycling: false`) is only safe when the list is guaranteed not to scroll, for example a short list with a non-virtualizing `ItemsPanel`.

"Containers are reused" holds for containers that are realized. `ListBox` and friends virtualize by default: only the rows near the viewport are realized, and Avalonia recycles containers as the list scrolls. Two consequences:

- **Control-local state cannot survive virtualization.** With a non-recycling template (`supportsRecycling: false`) Avalonia rebuilds the row content for each newly assigned container, so a ticked checkbox or typed text is lost. With a recycling template (`supportsRecycling: true`) it reuses the control tree for a different item, so the stale state would show on the wrong row.
- **Row state that must survive scrolling belongs in the model.** Bind the row controls to model fields, and have handlers read the live `DataContext` instead of a captured item, so a recycled row rehydrates for the item it now shows.

The recyclable shape (code-built rows):

```fsharp
let rowTemplate =
  FuncDataTemplate<Note>(
    (fun (note: Note) (_: INameScope) ->
      let checkBox = CheckBox(IsChecked = note.Checked)
      let textBox = TextBox(Text = note.RowText)

      // Handlers dispatch against the live DataContext, never a captured item.
      checkBox.IsCheckedChanged.Add(fun _ ->
        match checkBox.DataContext with
        | :? Note as current when checkBox.IsChecked.GetValueOrDefault false <> current.Checked ->
          dispatch (SetChecked(current.Id, checkBox.IsChecked.GetValueOrDefault false))
        | _ -> ())

      // Commit text on focus loss. Dispatching per keystroke races the async
      // model update: the row is rebuilt with the previous model value and the
      // character being typed is overwritten.
      textBox.LostFocus.Add(fun _ ->
        match textBox.DataContext with
        | :? Note as current when textBox.Text <> current.RowText ->
          dispatch (SetRowText(current.Id, textBox.Text))
        | _ -> ())

      let row = StackPanel(Orientation = Orientation.Horizontal)
      row.Children.Add checkBox
      row.Children.Add textBox

      // Rehydrate when the container is recycled for another item.
      row.DataContextChanged.Add(fun _ ->
        match row.DataContext with
        | :? Note as current ->
          checkBox.IsChecked <- current.Checked
          textBox.Text <- current.RowText
        | _ -> ())
      row :> Control),
    true)
```

Two interaction notes:

- Sync explicitly in code instead of using `ReflectionBinding` when the app must trim or publish AOT: reflection bindings are annotated `RequiresUnreferencedCode`/`RequiresDynamicCode` and fail there. XAML keeps compiled bindings as the AOT-safe alternative.
- A model update replaces the item, and the row content is rebuilt even with a recycling template, so uncommitted text in a control survives only until the next update of that row. Commit on focus loss (as above) keeps typing stable.

The dashboard sample implements this shape in both languages: the template opts into recycling, the row syncs from `DataContextChanged`, and handlers read the live `DataContext`.

> NXUI ships its own `BindItemsSource` extension that binds the raw `ItemsSource` property. In F#, open `MVUish.Avalonia.Collections` after `NXUI.Extensions` so shadowing picks this adapter, as shown above. In C#, call `CollectionExtensions` statically as shown above.

## Structural binding

`BindItemsSourceStructural` (`Extensions.BindItemsSourceStructural` in F#, `CollectionExtensions.BindItemsSource` without a key in C#) diffs by structural equality instead of keys. Repeated values are allowed. Prefer the keyed binding: it also matches moved items.

## Whole-list fallback

For short lists where none of the row state matters, `BindStream` on `ItemsControl.ItemsSourceProperty` still works: every emission reassigns the whole list and Avalonia resets the control. That is the cheapest option for a handful of items.

## Threading

Both bindings apply changes on the thread that emits the snapshot. `Program` dispatches on the UI thread, so model streams need nothing extra. Sources that emit from background threads must hop first, for example with `Observable.observeOn` on `Avalonia.Threading.Dispatcher.UIThread`.
