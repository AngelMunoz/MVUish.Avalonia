namespace FSharpSample

open System
open System.Collections.Generic
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Templates
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.Data
open NXUI.Extensions
open FSharp.Control.Reactive
// Keep the MVU namespaces last: F# extension shadowing must pick the
// collection bindings over the NXUI property binder of the same name.
open Avalonia.Mvu
open Avalonia.Mvu.Collections

// ============================================================================
// MODULE 1: CLOCK (Subscription Demo)
// ============================================================================
module Clock =
  type Model = DateTime

  type Msg = Tick of DateTime

  let init () = DateTime.Now, Cmd.none

  let update (Tick time) _ = time, Cmd.none

  let subscribe _ =
    Sub.interval (TimeSpan.FromSeconds(1.0)) (fun () -> Tick DateTime.Now)

  let view (model: IObservable<Model>) (_: Dispatch<Msg>) =
    let timeStr = model |> Observable.map(fun t -> t.ToString "HH:mm:ss")

    Border()
      .CornerRadius(10.0)
      .Background(Brushes.Black)
      .Padding(20.0)
      .Child(
        TextBlock()
          .Text(timeStr)
          .FontSize(32.0)
          .HorizontalAlignment(HorizontalAlignment.Center)
          .Foreground(Brushes.LimeGreen)
      )

// ============================================================================
// MODULE 2: METRICS SIMULATOR (Cmd + Random Demo)
// ============================================================================
module Metrics =
  type Model = {
    CpuUsage: int
    RamUsage: int
    IsCritical: bool
  }

  type Msg =
    | GenerateMetrics
    | NewMetrics of int * int

  let init () =
    {
      CpuUsage = 0
      RamUsage = 0
      IsCritical = false
    },
    Cmd.ofMsg GenerateMetrics

  let update msg model =
    match msg with
    | GenerateMetrics ->
      let generate () =
        let rnd = Random.Shared
        let cpu = rnd.Next(10, 100)
        let ram = rnd.Next(200, 8000)
        (cpu, ram)

      model,
      Cmd.OfTask.perform
        (fun _ -> task {
          do! System.Threading.Tasks.Task.Delay(1000)
          return generate()
        })
        ()
        NewMetrics

    | NewMetrics(cpu, ram) ->
      let isCrit = cpu > 80 || ram > 7000

      {
        model with
            CpuUsage = cpu
            RamUsage = ram
            IsCritical = isCrit
      },
      Cmd.ofMsg GenerateMetrics

  let view (model: IObservable<Model>) (_: Dispatch<Msg>) =
    let cpuText = model |> Observable.map(fun m -> $"CPU: {m.CpuUsage}%%")
    let ramText = model |> Observable.map(fun m -> $"RAM: {m.RamUsage} MB")

    let color =
      model
      |> Observable.map(fun m ->
        if m.IsCritical then Brushes.Red else Brushes.Cyan
      )
      |> Observable.distinctUntilChanged

    Border()
      .CornerRadius(10.0)
      .BorderBrush(color.ToBinding())
      .BorderThickness(2.0)
      .Padding(15.0)
      .Margin(0.0, 10.0, 0.0, 0.0)
      .Child(
        StackPanel()
          .Spacing(5.0)
          .Children(
            TextBlock()
              .Text("System Metrics")
              .FontWeight(FontWeight.Bold)
              .Foreground(Brushes.White),
            ProgressBar()
              .Minimum(0.0)
              .Maximum(100.0)
              .Value(model |> Observable.map(fun m -> float m.CpuUsage))
              .Height(10.0),
            TextBlock().Text(cpuText).FontSize(12.0),
            TextBlock().Text(ramText).FontSize(12.0)
          )
      )

// ============================================================================
// MODULE 3: NOTES (Keyed Collection Binding Demo)
// ============================================================================
module Notes =

  type Note = { Id: Guid; Text: string }

  type Model = { Notes: Note list }

  type Msg =
    | Add of string
    | Remove of Guid
    | Reverse

  let init () =
    let seed text = { Id = Guid.NewGuid(); Text = text }

    {
      Notes = [ seed "First"; seed "Second"; seed "Third" ]
    },
    Cmd.none

  let update msg model =
    match msg with
    | Add text when not(String.IsNullOrWhiteSpace text) ->
      let note = {
        Id = Guid.NewGuid()
        Text = text.Trim()
      }

      { Notes = note :: model.Notes }, Cmd.none

    | Add _ -> model, Cmd.none

    | Remove id ->
      {
        Notes = model.Notes |> List.filter(fun n -> n.Id <> id)
      },
      Cmd.none

    | Reverse -> { Notes = List.rev model.Notes }, Cmd.none

  let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
    let notes = model |> Observable.map(fun m -> m.Notes)
    let count = model |> Observable.map(fun m -> $"{m.Notes.Length} notes")

    // Each row owns state the model never sees: the checkbox and the text
    // box. The keyed binding matches rows by Id and reuses containers, so
    // that state travels with its note through adds, removes, and reorders.
    let rowTemplate =
      FuncDataTemplate<Note>(
        (fun note _ ->
          StackPanel()
            .Orientation(Orientation.Horizontal)
            .Spacing(8.0)
            .Children(
              CheckBox(),
              TextBlock()
                .VerticalAlignment(VerticalAlignment.Center)
                .Text(Binding(nameof note.Text)),
              TextBox().Width(140.0).PlaceholderText("row state lives here"),
              Button()
                .Content("x")
                .OnClickHandler(fun _ _ -> dispatch(Remove note.Id))
            )
          :> Control
        ),
        false
      )

    let listBox =
      ListBox().ItemTemplate(rowTemplate).MinHeight(160.0).MaxHeight(200.0)

    // The list-typed overload accepts IObservable<Note list> directly.
    listBox.BindItemsSource(notes, (fun n -> n.Id)) |> ignore

    let mutable draft = ""

    Border()
      .CornerRadius(10.0)
      .BorderThickness(2.0)
      .BorderBrush(Brushes.Teal)
      .Padding(15.0)
      .Margin(0.0, 10.0, 0.0, 0.0)
      .Child(
        StackPanel()
          .Spacing(8.0)
          .Children(
            TextBlock()
              .Text("Notes")
              .FontWeight(FontWeight.Bold)
              .Foreground(Brushes.White),
            TextBlock().Text(count).FontSize(12.0),
            StackPanel()
              .Orientation(Orientation.Horizontal)
              .Spacing(5.0)
              .Children(
                TextBox()
                  .Width(160.0)
                  .PlaceholderText("new note text")
                  .OnText(fun _ obs ->
                    obs
                    |> Observable.subscribe(fun text -> draft <- text)
                    |> ignore
                  ),
                Button()
                  .Content("Add")
                  .OnClickHandler(fun _ _ -> dispatch(Add draft)),
                Button()
                  .Content("Reverse")
                  .OnClickHandler(fun _ _ -> dispatch Reverse)
              ),
            listBox
          )
      )

// ============================================================================
// MAIN PARENT MODULE: DASHBOARD (Composition Demo)
// ============================================================================
module Dashboard =
  type Model = {
    Clock: Clock.Model
    Metrics: Metrics.Model
    Notes: Notes.Model
    Title: string
  }

  type Msg =
    | ClockMsg of Clock.Msg
    | MetricsMsg of Metrics.Msg
    | NotesMsg of Notes.Msg
    | Rename of string

  let init () =
    let cModel, cCmd = Clock.init()
    let mModel, mCmd = Metrics.init()
    let nModel, nCmd = Notes.init()

    {
      Clock = cModel
      Metrics = mModel
      Notes = nModel
      Title = "My MVU Dashboard"
    },
    Cmd.batch [
      Cmd.map ClockMsg cCmd
      Cmd.map MetricsMsg mCmd
      Cmd.map NotesMsg nCmd
    ]

  let update msg model =
    match msg with
    | ClockMsg cMsg ->
      let cModel, cCmd = Clock.update cMsg model.Clock
      { model with Clock = cModel }, Cmd.map ClockMsg cCmd

    | MetricsMsg mMsg ->
      let mModel, mCmd = Metrics.update mMsg model.Metrics
      { model with Metrics = mModel }, Cmd.map MetricsMsg mCmd

    | NotesMsg nMsg ->
      let nModel, nCmd = Notes.update nMsg model.Notes
      { model with Notes = nModel }, Cmd.map NotesMsg nCmd

    | Rename newTitle -> { model with Title = newTitle }, Cmd.none

  // SUBSCRIPTION COMPOSITION
  let subscribe model =
    Sub.batch [
      Clock.subscribe model.Clock |> Sub.map ClockMsg
    // Metrics doesn't have a subscription, it uses Cmd loop, but we could add one here if needed
    ]

  let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
    // LENSING: Slice the parent observable for children
    let clockObs = model |> Observable.map(fun m -> m.Clock)
    let metricsObs = model |> Observable.map(fun m -> m.Metrics)
    let notesObs = model |> Observable.map(fun m -> m.Notes)

    let dispatchClock = ClockMsg >> dispatch
    let dispatchMetrics = MetricsMsg >> dispatch
    let dispatchNotes = NotesMsg >> dispatch

    Window()
      .Title("F# MVU Dashboard")
      .Width(420.0)
      .Height(840.0)
      .Background(Brushes.DarkSlateGray)
      .Content(
        DockPanel()
          .LastChildFill(true)
          .Children(
            TextBlock()
              .Dock(Dock.Top)
              .Text(model |> Observable.map(fun m -> m.Title))
              .FontSize(24.0)
              .HorizontalAlignment(HorizontalAlignment.Center)
              .Margin(10.0)
              .Foreground(Brushes.White),

            StackPanel()
              .Margin(20.0)
              .Spacing(20.0)
              .Children(
                // Child 1: Clock
                Clock.view clockObs dispatchClock,

                // Child 2: Metrics
                Metrics.view metricsObs dispatchMetrics,

                // Child 3: Notes
                Notes.view notesObs dispatchNotes,

                // Parent Control
                StackPanel()
                  .Spacing(5.0)
                  .Children(
                    Label()
                      .Content("Dashboard Settings")
                      .Foreground(Brushes.LightGray),
                    TextBox()
                      .Text(model |> Observable.map(fun m -> m.Title))
                      .OnText(fun _ obs ->
                        obs
                        |> Observable.subscribe(Rename >> dispatch)
                        |> ignore
                      )
                  )
              )
          )
      )

module Program =
  [<EntryPoint>]
  let main argv =
    let program =
      Program.mkProgram Dashboard.init Dashboard.update Dashboard.view
      |> Program.withSubscription Dashboard.subscribe
    // |> Program.withConsoleTrace

    let view () = Program.run program

    AppBuilder
      .Configure<Application>()
      .UsePlatformDetect()
      .UseFluentTheme()
      .StartWithClassicDesktopLifetime(view, argv)
