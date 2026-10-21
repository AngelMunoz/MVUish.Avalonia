# Changelog

## [Unreleased]

## [2.0.0] - 2026-10-20

### Added

- Keyed, model-driven subscriptions (Elmish v4 semantics): `Sub<'Msg>` is a list of `(SubId * Subscribe<'Msg>)` pairs; the program re-evaluates `subscribe` after every update and starts or stops sources to match, reporting duplicate ids to the error handler. Fixes the 1.0 behavior where a subscription started once with the initial model and never stopped (issue 6's leak chain).
- Program lifetimes: `Program.runWithLifecycle` returns a view plus an `IDisposable` that stops subscriptions, completes the model stream, and stops the update loop; disposing twice is a no-op and dispatch after dispose is dropped.
- Visual-tree lifetimes: `Program.runWithLifetime control arg program` disposes the program when the control first detaches from the visual tree.
- Termination: `Program.withTermination` / `mapTermination` register an exit message plus a final-model handler.
- Subscription helpers: `Sub.ofId`, `Sub.fromObservable` (bridges any `IObservable<'Msg>`), `Sub.fromRoutedEvent` / `Sub.fromRoutedEventWith` (Avalonia routed events with routing strategies and handled-events-too), and `Sub.untilDetached`.
- Commands: `Cmd.ofEffect` (dispatch-aware effect), `Cmd.ofAction` (plain `unit -> unit` action for view-side effects), `either` variants for `OfFunc`/`OfAsync`/`OfTask`, `Cmd.OfAsyncWith` (custom async start), and `Cmd.OfValueTask` with completed-synchronously fast paths (ported from Elmish, Apache 2.0).
- Dev tools: `Program.withTrace` (message, model, wanted sub ids), `Program.mapSubscription`, `Program.mapErrorHandler`, `Program.onError`; `withConsoleTrace` now logs active sub ids.
- C#: `CSub<'Msg>` named subscription handle with the `Subscription` factories; `Command.OfAction/Attempt/Either/AttemptTask/EitherTask` and the ValueTask trio; builder `WithTermination`, `WithTrace`, `RunWithLifecycle`, `RunWithLifetime`; `ProgramHandle<'View>` result type.
- Documentation: a Subscriptions guide and a 1.0-to-2.0 migration guide.

### Changed

- **Breaking:** `Sub<'Msg>` changed from a single start function to a keyed list; `Sub.none` is `[]`, `Sub.batch` concatenates sets, and `Sub.map` gained an id-prefix parameter.
- **Breaking:** `Sub.interval` and `Sub.fromEvent` gained an id parameter.
- **Breaking:** the `Program` record gained a `Termination` field; records constructed by hand must add it (or use `mkProgram`/`mkSimple`).
- **Breaking:** C# `WithSubscription` now takes `Func<'Model, CSub<'Msg>>` built from the `Subscription` helpers instead of a raw delegate.
- The loop now renders before reconciling subscriptions and runs the init command after both (Elmish's order), instead of running the init command before the view and subscriptions exist.
- A failing view factory reports to `OnError` and yields the default view instead of throwing out of `runWithLifecycle`.
- Templates reference package version `2.*`.

## [1.0.1] - 2026-10-16

### Fixed

- `BindItemsSource` no longer disposes its subscription when the control detaches from the visual tree. Detach disposal froze detached-but-alive controls (for example views kept in a navigation cache) at their last snapshot; they now keep updating, matching how Avalonia's own observable bindings behave. To stop an abandoned binding from processing snapshots forever, the observer holds the control through a weak reference and ends the subscription on the first emission after the control is collected.

### Changed

- **Breaking:** collection bindings return the bound `ItemsControl` instead of `IDisposable`, following the NXUI DSL convention already used by `BindStream`, so calls chain in builder expressions. With the self-detaching subscription above, dropping the disposable is safe: a binding ends when its control is collected or the source completes. Callers that disposed the returned `IDisposable` for early teardown must drop that call.

## [1.0.0] - 2026-10-15

### Added

- Initial release of MVUish.Avalonia: a Model-Update-Observable (reactive MVU) library for Avalonia that binds control properties directly to the model stream instead of diffing a virtual tree.
- `Program` builder with `Cmd` and `Sub` helpers for F#, and `Command`/`Subscription` helpers plus a `ProgramBuilder` for C#.
- Module composition through `Cmd.map`, `Sub.map`, and `Sub.batch` (`Command.Map`, `Subscription.Map`, and `Subscription.Batch` in C#).
- Keyed and structural `BindItemsSource` collection bindings that apply model snapshots as minimal insert, remove, replace, and move deltas.
- `dotnet new` templates `mvu-fsharp` and `mvu-csharp`, shipped as the `MVUish.Avalonia.Templates` package.
