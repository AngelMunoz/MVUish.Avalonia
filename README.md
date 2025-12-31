# Avalonia.MVU

**Avalonia.MVU** is a lightweight, performant **Model-View-Update (MVU)** library for Avalonia, designed for both **C#** and **F#**.

Unlike traditional MVU implementations (like Elm or Fabulous) that often rely on a Virtual DOM (VDOM) and tree diffing to update the UI, **Avalonia.MVU** leverages **Reactive Extensions (Rx)** to create direct, fine-grained bindings between your state and your controls.

## The "Reactive MVU" Difference

In a standard MVU loop, the `View` function is often called every time the `Model` changes, producing a new virtual tree which is then diffed against the previous one. While powerful, this can incur performance overhead and often requires wrappers around native controls.

**Avalonia.MVU** takes a different approach, effectively implementing a **Model-Update-Observable** pattern:

1.  **Model & Update (The "MU"):** Your application logic remains pure and immutable. You define a `Model` and an `Update` function that processes `Messages` to produce a new `Model`. This is the standard Elmish heart of the app.
2.  **View (The "Reactive Binding"):** The `View` function is called **only once** during initialization. Instead of receiving a snapshot of the `Model`, it receives an `IObservable<Model>`.
3.  **Direct Updates:** You build your UI using standard Avalonia controls (or helpers like [NXUI]) and bind specific properties (like `Text`, `Color`, `IsVisible`) directly to the observable stream.

### Why this approach?

*   **Zero VDOM Overhead:** There is no tree diffing. Changes propagate directly from the Model stream to the target properties.
*   **High Performance:** Only the specific properties that change are updated.
*   **Native Control Interop:** Since the View returns actual Avalonia controls, you can easily use any 3rd party control, existing behaviors, or complex layouts without needing specialized wrappers.

## Getting Started

This library provides a unified API for both C# and F# developers, treating both as first-class citizens.

### C# Example

We provide a specialized `Avalonia.Mvu.CSharp` module to hide F# specifics and provide a familiar, idiomatic C# API.

```csharp
using static Avalonia.Mvu.CSharp;

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
            TextBlock().Text(countText.ToBinding(), BindingMode.OneWay),
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
    | Increment -> { model with Count = model.Count + 1 }, Cmd.none

let view (model: IObservable<Model>) dispatch =
    let countText = model |> Observable.map (fun m -> sprintf "Count: %d" m.Count)

    StackPanel().Children(
        TextBlock().Text(countText),
        Button().Content("Increment").OnClick(fun _ obs ->
            obs |> Observable.subscribe (fun _ -> dispatch Increment) |> ignore
        )
    )
```

## Composition and Scaling

Avalonia.MVU supports scaling your application through **Module Composition**. You can nest components by mapping their Commands and Subscriptions, allowing you to build complex applications from small, isolated modules.

*   `Command.Map` / `Cmd.map`
*   `Subscription.Map` / `Sub.map`
*   `Subscription.Batch` / `Sub.batch`

Check the [Samples](./src/samples/) folder for a comprehensive **Dashboard** example showcasing parent-child communication, async commands, and subscriptions in both languages.

## Existing Alternatives

If you prefer the VDOM approach or need strict Elm architecture compliance (where View is a pure function of Model -> UI Tree):

- [Avalonia.FuncUI] - A VDOM-based MVU library, very similar to Elm/React.
- [Fabulous.Avalonia] - MVU framework for .NET MAUI and Avalonia using VDOM.
- [Elmish.Avalonia] - Elmish for Avalonia with static XAML views.

[Avalonia.FuncUI]: https://github.com/fsprojects/Avalonia.FuncUI
[NXUI]: https://github.com/wieslawsoltes/NXUI
[Fabulous.Avalonia]: https://github.com/fabulous-dev/Fabulous.Avalonia
[Elmish.Avalonia]: https://github.com/JordanMarr/Elmish.Avalonia