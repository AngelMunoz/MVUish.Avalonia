---
categoryindex: 0
index: 2
title: Getting Started
category: Documentation
---

# Getting Started

MVUish.Avalonia requires **.NET 10** and **Avalonia 12**. The samples and templates also use [NXUI](https://github.com/wieslawsoltes/NXUI) 12 for code-built views.

```bash
dotnet add package MVUish.Avalonia
```

A module has the four standard MVU pieces: a `Model`, a `Msg`, an `init`, and an `update`. The difference is the `view`: it runs once and receives an `IObservable<Model>` instead of a model snapshot. You pick the slices of the stream each control needs and bind them directly.

## F#

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

Build the program with `Program.mkProgram` and hand it to your NXUI bootstrap:

```fsharp
module Program

open Avalonia
open Avalonia.Controls.ApplicationLifetimes

[<EntryPoint>]
let main argv =
  let program = Program.mkProgram Counter.init Counter.update Counter.view

  let view () = Program.run program

  AppBuilder
    .Configure<Application>()
    .UsePlatformDetect()
    .UseFluentTheme()
    .StartWithClassicDesktopLifetime(view, argv)
```

`Program.run` returns the view your `view` function produced, so it slots straight into `StartWithClassicDesktopLifetime`.

## C#

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

Build the program with `Program.MkProgram` and hand the resulting view to your NXUI bootstrap:

```csharp
using static MVUish.Avalonia.CSharp;

var root = Program.MkProgram(
    init: CounterModule.Init,
    update: CounterModule.Update,
    view: CounterModule.View)
    .Run();

Window View() =>
    Window()
        .Title("Counter")
        .Width(420)
        .Height(600)
        .Content(root)
        .Mount();

AppBuilder
    .Configure<Application>()
    .UsePlatformDetect()
    .UseFluentTheme()
    .StartWithClassicDesktopLifetime(View, args);
```

`Program.MkSimple` is available when your module has no commands: it takes plain `Func` members and wraps them for you.

## Next steps

- The [dashboard sample](https://github.com/AngelMunoz/MVUish.Avalonia/tree/main/src/samples) in the repository composes clock, metrics, and notes modules in both languages.
- [The Reactive MVU Pattern](./reactive-mvu.md) covers commands, subscriptions, and composition.
- [Collections](./collections.md) covers efficient list binding.
