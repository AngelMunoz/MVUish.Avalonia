# MVUish.Avalonia

**MVUish.Avalonia** is a lightweight, performant **Model-View-Update (MVU)** library for Avalonia, designed for both **C#** and **F#**.

Unlike traditional MVU implementations (like Elm or Fabulous) that often rely on a Virtual DOM (VDOM) and tree diffing to update the UI, **MVUish.Avalonia** leverages **Reactive Extensions (Rx)** to create direct, fine-grained bindings between your state and your controls.

## The "Reactive MVU" Difference

In a standard MVU loop, the `View` function is often called every time the `Model` changes, producing a new virtual tree which is then diffed against the previous one. While powerful, this can incur performance overhead and often requires wrappers around native controls.

**MVUish.Avalonia** takes a different approach, effectively implementing a **Model-Update-Observable** pattern:

1.  **Model & Update (The "MU"):** Your application logic remains pure and immutable. You define a `Model` and an `Update` function that processes `Messages` to produce a new `Model`. This is the standard Elmish heart of the app.
2.  **View (The "Reactive Binding"):** The `View` function is called **only once** during initialization. Instead of receiving a snapshot of the `Model`, it receives an `IObservable<Model>`.
3.  **Direct Updates:** You build your UI using standard Avalonia controls (or helpers like [NXUI]) and bind specific properties (like `Text`, `Color`, `IsVisible`) directly to the observable stream.

### Why this approach?

- **Zero VDOM Overhead:** There is no tree diffing. Changes propagate directly from the Model stream to the target properties.
- **High Performance:** Only the specific properties that change are updated.
- **Native Control Interop:** Since the View returns actual Avalonia controls, you can easily use any 3rd party control, existing behaviors, or complex layouts without needing specialized wrappers.

## Getting Started

Requires **.NET 10** and **Avalonia 12**. The samples and templates also use [NXUI] 12 for code-built views.

This library provides a unified API for both C# and F# developers, treating both as first-class citizens.

Documentation is published at [angelmunoz.github.io/MVUish.Avalonia](https://angelmunoz.github.io/MVUish.Avalonia/).

### C# Example

We provide a specialized `MVUish.Avalonia.CSharp` module to hide F# specifics and provide a familiar, idiomatic C# API.

```csharp
using static MVUish.Avalonia.CSharp;

public static class CounterModule
{
    // 1. Model: Immutable state
    public record Model(int Count);

    // 2. Message: Actions that change state
    public record Msg { public record Increment : Msg; }

    // 3. Init: Initial state
    public static (Model, Action<Action<Msg>>) Init() =>
        (new Model(0), Command.None<Msg>());

    // 4. Update: State transition
    public static (Model, Action<Action<Msg>>) Update(Msg msg, Model model) =>
        msg switch {
            Msg.Increment => (model with { Count = model.Count + 1 }, Command.None<Msg>()),
            _ => (model, Command.None<Msg>())
        };

    // 5. View: Reactive UI construction (Called Once)
    public static Control View(IObservable<Model> model, Action<Msg> dispatch)
    {
        // Select specific data from the model stream
        var countText = model.Select(m => $"Count: {m.Count}");

        return StackPanel().Children(
            TextBlock().Text(countText),
            Button().Content("Increment").OnClick(e =>
                e.Subscribe(_ => dispatch(new Msg.Increment()))
            )
        );
    }
}
```

### F# Example

```fsharp
module Counter

type Model = { Count: int }
type Msg = Increment

let init () = { Count = 0 }, Cmd.none

let update msg model =
    match msg with
    | Increment ->
      { model with Count = model.Count + 1 }, Cmd.none

let view (model: IObservable<Model>) dispatch =
    let countText = model |> Observable.map(fun m -> sprintf "Count: %d" m.Count)

    StackPanel().Children(
        TextBlock().Text(countText),
        Button().Content("Increment").OnClick(fun _ obs ->
            obs |> Observable.add(fun _ -> dispatch Increment)
        )
    )
```

## Composition and Scaling

MVUish.Avalonia supports scaling your application through **Module Composition**. You can nest components by mapping their Commands and Subscriptions, allowing you to build complex applications from small, isolated modules.

- `Command.Map` / `Cmd.map`
- `Subscription.Map` / `Sub.map`
- `Subscription.Batch` / `Sub.batch`

Check the [Samples](./src/samples/) folder for a comprehensive **Dashboard** example showcasing parent-child communication, async commands, and subscriptions in both languages.

## Collections

Assigning a new list to `ItemsSource` on every model change rebuilds every item container: selection, scroll position, and any state a row holds are lost. The collection bindings instead keep one owned `ObservableCollection` as `ItemsSource` and apply each snapshot from the model as minimal deltas, so containers are reused.

Both bindings assign `ItemsSource` once and return the control, so they chain in NXUI-style builders like the rest of the library. There is no disposable to manage: a binding keeps applying snapshots while its control is alive — including while detached from the visual tree — and ends itself on the first emission after the control is collected.

### Keyed binding (preferred)

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

> NXUI ships its own `BindItemsSource` extension that binds the raw `ItemsSource` property. In F#, open `MVUish.Avalonia.Collections` after `NXUI.Extensions` so shadowing picks this adapter, as shown above. In C#, call `CollectionExtensions` statically as shown above.

### Structural binding

`BindItemsSourceStructural` (`Extensions.BindItemsSourceStructural` in F#, `CollectionExtensions.BindItemsSource` without a key in C#) diffs by structural equality instead of keys. Repeated values are allowed. Prefer the keyed binding: it also matches moved items.

### Whole-list fallback

For short lists where none of the row state matters, `BindStream` on `ItemsControl.ItemsSourceProperty` still works — every emission reassigns the whole list and Avalonia resets the control. That is the cheapest option for a handful of items.

### Threading

Both bindings apply changes on the thread that emits the snapshot. `Program` dispatches on the UI thread, so model streams need nothing extra. Sources that emit from background threads must hop first, for example with `Observable.observeOn` on `Avalonia.Threading.Dispatcher.UIThread`.

## Project templates

A `dotnet new` template pack lives in [templates/](./templates/):

```
dotnet pack templates/MVUish.Avalonia.Templates.csproj
dotnet new install <path-to-the-produced-nupkg>
dotnet new mvu-fsharp -o MyApp
dotnet new mvu-csharp -o MyApp
```

Each template generates a minimal clock app wired with this library and NXUI. Generated apps reference the published `MVUish.Avalonia` package (`1.*`); inside this repository the template projects build against the library source instead, through `templates/Directory.Build.props`.

## Existing Alternatives

If you prefer the VDOM approach or need strict Elm architecture compliance (where View is a pure function of Model -> UI Tree):

- [Avalonia.FuncUI] - A VDOM-based MVU library, very similar to Elm/React.
- [Fabulous.Avalonia] - MVU framework for .NET MAUI and Avalonia using VDOM.
- [Elmish.Avalonia] - Elmish for Avalonia with static XAML views.

[Avalonia.FuncUI]: https://github.com/fsprojects/Avalonia.FuncUI
[NXUI]: https://github.com/wieslawsoltes/NXUI
[Fabulous.Avalonia]: https://github.com/fabulous-dev/Fabulous.Avalonia
[Elmish.Avalonia]: https://github.com/JordanMarr/Elmish.Avalonia
