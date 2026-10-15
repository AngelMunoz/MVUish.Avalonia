# Changelog

## [Unreleased]

## [1.0.0] - 2026-10-15

### Added

- Initial release of MVUish.Avalonia: a Model-Update-Observable (reactive MVU) library for Avalonia that binds control properties directly to the model stream instead of diffing a virtual tree.
- `Program` builder with `Cmd` and `Sub` helpers for F#, and `Command`/`Subscription` helpers plus a `ProgramBuilder` for C#.
- Module composition through `Cmd.map`, `Sub.map`, and `Sub.batch` (`Command.Map`, `Subscription.Map`, and `Subscription.Batch` in C#).
- Keyed and structural `BindItemsSource` collection bindings that apply model snapshots as minimal insert, remove, replace, and move deltas.
- `dotnet new` templates `mvu-fsharp` and `mvu-csharp`, shipped as the `MVUish.Avalonia.Templates` package.
