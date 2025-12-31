namespace FSharpSample

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.Data
open NXUI.Extensions
open FSharp.Control.Reactive
open Avalonia.Mvu

// ============================================================================
// MODULE 1: CLOCK (Subscription Demo)
// ============================================================================
module Clock =
    type Model = DateTime

    type Msg = Tick of DateTime

    let init () = DateTime.Now, Cmd.none

    let update (Tick time) _ = time, Cmd.none

    let subscribe _ =
        let tick time = Tick time
        // Create a timer subscription that ticks every second
        let sub dispatch =
            let timer = new DispatcherTimer(TimeSpan.FromSeconds(1.0), DispatcherPriority.Background, EventHandler(fun _ _ -> dispatch(tick DateTime.Now)))
            timer.Start()
            { new IDisposable with member _.Dispose() = timer.Stop() }
        sub

    let view (model: IObservable<Model>) (_: Dispatch<Msg>) =
        let timeStr = model |> Observable.map (fun t -> t.ToString("HH:mm:ss"))
        
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
                    .FontFamily("Courier New")
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
        { CpuUsage = 0; RamUsage = 0; IsCritical = false }, 
        Cmd.ofMsg GenerateMetrics

    let update msg model =
        match msg with
        | GenerateMetrics ->
            let generate _ =
                let rnd = Random()
                let cpu = rnd.Next(10, 100)
                let ram = rnd.Next(200, 8000)
                (cpu, ram)
            
            model, Cmd.OfTask.perform (fun _ -> System.Threading.Tasks.Task.Delay(1000).ContinueWith(fun _ -> generate())) () NewMetrics

        | NewMetrics (cpu, ram) ->
            let isCrit = cpu > 80 || ram > 7000
            { model with CpuUsage = cpu; RamUsage = ram; IsCritical = isCrit }, 
            Cmd.ofMsg GenerateMetrics

    let view (model: IObservable<Model>) (_: Dispatch<Msg>) =
        let cpuText = model |> Observable.map (fun m -> $"CPU: {m.CpuUsage}%%")
        let ramText = model |> Observable.map (fun m -> $"RAM: {m.RamUsage} MB")
        
        let color = 
            model 
            |> Observable.map (fun m -> if m.IsCritical then Brushes.Red else Brushes.Cyan)
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
                        TextBlock().Text("System Metrics").FontWeight(FontWeight.Bold).Foreground(Brushes.White),
                        ProgressBar().Minimum(0.0).Maximum(100.0).Value(model |> Observable.map (fun m -> float m.CpuUsage)).Height(10.0),
                        TextBlock().Text(cpuText).FontSize(12.0),
                        TextBlock().Text(ramText).FontSize(12.0)
                    )
            )

// ============================================================================
// MAIN PARENT MODULE: DASHBOARD (Composition Demo)
// ============================================================================
module Dashboard =
    type Model = {
        Clock: Clock.Model
        Metrics: Metrics.Model
        Title: string
    }

    type Msg =
        | ClockMsg of Clock.Msg
        | MetricsMsg of Metrics.Msg
        | Rename of string

    let init () =
        let cModel, cCmd = Clock.init ()
        let mModel, mCmd = Metrics.init ()
        
        { Clock = cModel; Metrics = mModel; Title = "My MVU Dashboard" },
        Cmd.batch [
            Cmd.map ClockMsg cCmd
            Cmd.map MetricsMsg mCmd
        ]

    let update msg model =
        match msg with
        | ClockMsg cMsg ->
            let cModel, cCmd = Clock.update cMsg model.Clock
            { model with Clock = cModel }, Cmd.map ClockMsg cCmd
        
        | MetricsMsg mMsg ->
            let mModel, mCmd = Metrics.update mMsg model.Metrics
            { model with Metrics = mModel }, Cmd.map MetricsMsg mCmd

        | Rename newTitle ->
            { model with Title = newTitle }, Cmd.none

    // SUBSCRIPTION COMPOSITION
    let subscribe model =
        Sub.batch [
            Clock.subscribe model.Clock |> Sub.map ClockMsg
            // Metrics doesn't have a subscription, it uses Cmd loop, but we could add one here if needed
        ]

    let view (model: IObservable<Model>) (dispatch: Dispatch<Msg>) =
        // LENSING: Slice the parent observable for children
        let clockObs = model |> Observable.map (fun m -> m.Clock)
        let metricsObs = model |> Observable.map (fun m -> m.Metrics)
        
        let dispatchClock = ClockMsg >> dispatch
        let dispatchMetrics = MetricsMsg >> dispatch

        Window()
            .Title("F# MVU Dashboard")
            .Width(400.0)
            .Height(600.0)
            .Background(Brushes.DarkSlateGray)
            .Content(
                DockPanel()
                    .LastChildFill(true)
                    .Children(
                        TextBlock()
                            .Dock(Dock.Top)
                            .Text(model |> Observable.map (fun m -> m.Title))
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

                                // Parent Control
                                StackPanel()
                                    .Spacing(5.0)
                                    .Children(
                                        Label().Content("Dashboard Settings").Foreground(Brushes.LightGray),
                                        TextBox()
                                            .Text(model |> Observable.map (fun m -> m.Title))
                                            .OnText(fun _ obs -> 
                                                obs |> Observable.subscribe (Rename >> dispatch) |> ignore
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
