# Changelog

## [Unreleased]

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
