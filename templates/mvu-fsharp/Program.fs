module MvuApp

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open FSharp.Control.Reactive
open NXUI.Extensions
open MVUish.Avalonia

module Clock =
  type Model = DateTime

  type Msg = Tick of DateTime

  let init () = DateTime.Now, Cmd.none

  let update (Tick time) _ = time, Cmd.none

  let subscribe _ =
    Sub.interval (TimeSpan.FromSeconds 1.0) (fun () -> Tick DateTime.Now)

  let view (model: IObservable<Model>) (_: Dispatch<Msg>) =
    let time = model |> Observable.map(fun t -> t.ToString "HH:mm:ss")

    Border()
      .CornerRadius(10.0)
      .Background(Brushes.Black)
      .Padding(20.0)
      .Child(
        TextBlock()
          .Text(time)
          .FontSize(32.0)
          .HorizontalAlignment(HorizontalAlignment.Center)
          .Foreground(Brushes.LimeGreen)
      )

[<EntryPoint>]
let main argv =
  let program =
    Program.mkProgram Clock.init Clock.update Clock.view
    |> Program.withSubscription Clock.subscribe

  let view () =
    Window()
      .Title("MVUish.Avalonia F# Clock")
      .Width(360.0)
      .Height(180.0)
      .Content(Program.run program)

  AppBuilder
    .Configure<Application>()
    .UsePlatformDetect()
    .UseFluentTheme()
    .StartWithClassicDesktopLifetime(view, argv)
