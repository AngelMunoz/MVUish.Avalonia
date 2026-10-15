---
categoryindex: 0
index: 1
title: MVUish.Avalonia
category: Documentation
---

# MVUish.Avalonia

**MVUish.Avalonia** is a lightweight, performant **Model-View-Update (MVU)** library for Avalonia, designed for both **C#** and **F#**.

Traditional MVU implementations (Elm, Fabulous, Avalonia.FuncUI) rebuild a virtual tree on every model change and diff it against the previous one. MVUish.Avalonia implements a **Model-Update-Observable** pattern instead: your `view` function runs once, receives an `IObservable<Model>`, and binds control properties directly to the stream. There is no VDOM and no tree diffing.

## Installation

```bash
dotnet add package MVUish.Avalonia
```

The `dotnet new` project templates ship as a separate package:

```bash
dotnet new install MVUish.Avalonia.Templates
dotnet new mvu-fsharp -o MyApp
dotnet new mvu-csharp -o MyApp
```

## Requirements

- .NET 10
- Avalonia 12

The samples and templates also use [NXUI](https://github.com/wieslawsoltes/NXUI) 12 for code-built views.

## In these docs

- [Getting Started](./getting-started.md): your first counter in C# and F#, and how to wire it into an NXUI bootstrap.
- [The Reactive MVU Pattern](./reactive-mvu.md): the model-update-observable loop, commands, subscriptions, and module composition.
- [Collections](./collections.md): keyed and structural `BindItemsSource` bindings for efficient lists.
