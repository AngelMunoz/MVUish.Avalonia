module MVUish.Avalonia.Tests.ProgramTests

// End-to-end, behavior-driven tests: every test runs a real Program
// through Program.runWithLifecycle on a headless UI thread and asserts
// user-visible outcomes (bound views, delivered messages, frozen timers),
// not internal state. Tests return tasks the runner awaits; nothing
// blocks over sync.

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open System.Reactive.Subjects
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open MVUish.Avalonia
open MVUish.Avalonia.Tests.TestSession
open Xunit

// --- shared message and model types ----------------------------

type TermMsg =
  | Add of int
  | Stop

type ToggleModel = { Running: bool; Count: int }

type ToggleMsg =
  | Ticked
  | ToggleRunning

type PaceModel = { IntervalMs: int; Count: int }

type PaceMsg =
  | PaceTick
  | SetInterval of int

type TwinModel = { A: int; B: int }

type TwinMsg =
  | TickA
  | TickB

type BridgeMsg = Got of int

type KeyMsg = Pressed

type DetachMsg = Timed

type CrashMsg =
  | Bad
  | Good of int

type CmdMsg =
  | Started
  | Loaded of int
  | Failed of string

type EffectMsg =
  | ClearTextBox of TextBox
  | Completed

// --- harness ----------------------------------------------------

let private session = TestSession.Session

/// Hard cap for one test body on the UI thread: a test that exceeds it
/// fails instead of hanging the suite.
let private testTimeout = TimeSpan.FromSeconds 8.0

/// Run an async body on the headless UI thread. The body must await its
/// work (never block): the dispatcher only processes the program loop's
/// UI posts while the awaited task yields.
let private onUi (work: unit -> Task<bool>) : Task<bool> = task {
  use cts = new CancellationTokenSource(testTimeout)

  try
    return! session.Dispatch<bool>(Func<Task<bool>>(work), cts.Token)
  with
  | :? OperationCanceledException -> return false
  | :? AggregateException as ae when
    ae.InnerExceptions |> Seq.exists(fun e -> e :? OperationCanceledException)
    ->
    return false
}

/// Poll every 10 ms until the condition holds or the timeout expires.
let private waitFor (timeoutMs: int) (cond: unit -> bool) : Task<bool> = task {
  let sw = Stopwatch.StartNew()
  let mutable ok = cond()

  while not ok && sw.ElapsedMilliseconds < int64 timeoutMs do
    do! Task.Delay 10
    ok <- cond()

  return ok
}

let private parseText (tb: TextBlock) =
  match Int32.TryParse tb.Text with
  | true, n -> n
  | false, _ -> -1

/// Records the last model, stream completion, and the dispatch function,
/// for tests that drive messages by hand.
type Harness<'Model, 'Msg>() =
  let last = ref Unchecked.defaultof<'Model>
  let mutable dispatch = Unchecked.defaultof<Dispatch<'Msg>>
  let completed = ref false

  member _.View =
    fun (model: IObservable<'Model>) (d: Dispatch<'Msg>) ->
      dispatch <- d

      model.Subscribe(
        (fun m -> last.Value <- m),
        (fun _ -> ()),
        (fun () -> completed.Value <- true)
      )
      |> ignore

      Border()

  member _.Dispatch msg = dispatch msg

  member _.Model = last.Value

  member _.Completed = completed.Value

/// A view that records through the harness and returns the given control.
let private keeping
  (h: Harness<'Model, 'Msg>)
  (control: 'View)
  : IObservable<'Model> -> Dispatch<'Msg> -> 'View =
  fun model dispatch ->
    h.View model dispatch |> ignore
    control

let private countingTicker
  (dispatch: Dispatch<'Msg>)
  (msg: 'Msg)
  (intervalMs: float)
  (stops: int ref)
  =
  let timer = new Timers.Timer(intervalMs)
  timer.Elapsed.Add(fun _ -> dispatch msg)
  timer.Start()

  {
    new IDisposable with
      member _.Dispose() =
        incr stops
        timer.Stop()
        timer.Dispose()
  }

// ---------------------------------------------------------------
// Use case: a clock page ticks into its bound view, and leaving the
// page stops it (issue 6).
// ---------------------------------------------------------------

[<Fact>]
let clockTicksIntoBoundViewAndDisposingFreezesIt () : Task = task {
  let! result =
    onUi(fun () -> task {
      let clock =
        Program.mkSimple
          (fun () -> 0)
          (fun _ count -> count + 1)
          (fun (model: IObservable<int>) _ ->
            let tb = TextBlock()

            tb.Bind(TextBlock.TextProperty, Observable.map string model)
            |> ignore

            tb
          )
        |> Program.withSubscription(fun _ ->
          Sub.interval "clock" (TimeSpan.FromMilliseconds 25.0) id
        )

      let view, lifetime = Program.runWithLifecycle () clock
      let! ticked = waitFor 5000 (fun () -> parseText view >= 5)
      let frozenAt = parseText view

      lifetime.Dispose()
      do! Task.Delay 200

      // Completing the model stream reverts bound properties to their
      // defaults (documented teardown behavior): the tick text clears.
      let reverted = String.IsNullOrEmpty view.Text

      return ticked && frozenAt >= 5 && reverted
    })

  Assert.True result
}

[<Fact>]
let disposeIsIdempotentAndDispatchAfterDisposeIsIgnored () : Task = task {
  let! result =
    onUi(fun () -> task {
      let h = Harness<int, unit>()

      let program = Program.mkSimple (fun () -> 0) (fun _ n -> n + 1) h.View

      let _, lifetime = Program.runWithLifecycle () program

      h.Dispatch()
      let! gotOne = waitFor 2000 (fun () -> h.Model = 1)

      lifetime.Dispose()
      lifetime.Dispose()
      h.Dispatch()
      do! Task.Delay 200

      return gotOne && h.Model = 1
    })

  Assert.True result
}

[<Fact>]
let modelStreamCompletesOnDispose () : Task = task {
  let! result =
    onUi(fun () -> task {
      let h = Harness<int, unit>()

      let program = Program.mkSimple (fun () -> 0) (fun _ n -> n + 1) h.View

      let _, lifetime = Program.runWithLifecycle () program

      lifetime.Dispose()

      return h.Completed
    })

  Assert.True result
}

[<Fact>]
let boundControlsRevertToDefaultsOnCompletion () : Task = task {
  let! result =
    onUi(fun () -> task {
      let h = Harness<int, unit>()
      let border = Border()

      let view model (dispatch: Dispatch<unit>) =
        border.Bind(
          Layoutable.WidthProperty,
          Observable.map (fun (n: int) -> float n + 5.0) model
        )
        |> ignore

        h.View model dispatch

      let program = Program.mkSimple (fun () -> 0) (fun _ n -> n + 1) view
      let _, lifetime = Program.runWithLifecycle () program

      h.Dispatch()
      h.Dispatch()
      let! grew = waitFor 2000 (fun () -> border.Width = 7.0)

      lifetime.Dispose()

      return
        grew
        && Double.IsNaN border.Width
        && not(border.IsSet Layoutable.WidthProperty)
    })

  Assert.True result
}

[<Fact>]
let terminationMessageStopsProgramWithFinalModel () : Task = task {
  let! result =
    onUi(fun () -> task {
      let h = Harness<int, TermMsg>()
      let finalModel = ref None

      let program =
        Program.mkProgram
          (fun () -> 0, Cmd.none)
          (fun msg n ->
            match msg with
            | Add v -> n + v, Cmd.none
            | Stop -> n, Cmd.none
          )
          h.View
        |> Program.withTermination
          (function
          | Stop -> true
          | _ -> false)
          (fun final -> finalModel.Value <- Some final)

      let _, lifetime = Program.runWithLifecycle () program

      h.Dispatch(Add 1)
      let! gotOne = waitFor 2000 (fun () -> h.Model = 1)

      h.Dispatch(Add 2)
      let! gotThree = waitFor 2000 (fun () -> h.Model = 3)

      h.Dispatch Stop
      let! stopped = waitFor 2000 (fun () -> finalModel.Value.IsSome)

      h.Dispatch(Add 9)
      do! Task.Delay 200

      return
        gotOne
        && gotThree
        && stopped
        && finalModel.Value = Some 3
        && h.Model = 3
        && h.Completed
    })

  Assert.True result
}

// ---------------------------------------------------------------
// Use case: subscriptions follow the model (Elmish v4 semantics).
// ---------------------------------------------------------------

[<Fact>]
let subscriptionsFollowTheModel () : Task = task {
  let! result =
    onUi(fun () -> task {
      let starts = ref 0
      let stops = ref 0
      let h = Harness<ToggleModel, ToggleMsg>()

      let ticker: Subscribe<ToggleMsg> =
        fun dispatch ->
          incr starts
          countingTicker dispatch Ticked 25.0 stops

      let program =
        Program.mkSimple
          (fun () -> { Running = true; Count = 0 })
          (fun msg m ->
            match msg with
            | Ticked -> { m with Count = m.Count + 1 }
            | ToggleRunning -> { m with Running = not m.Running }
          )
          h.View
        |> Program.withSubscription(fun m ->
          if m.Running then Sub.ofId "ticker" ticker else Sub.none
        )

      let _, lifetime = Program.runWithLifecycle () program
      let! ticking = waitFor 5000 (fun () -> h.Model.Count >= 3)

      h.Dispatch ToggleRunning
      let! stopped = waitFor 2000 (fun () -> stops.Value = 1)
      // A tick dispatched just before the dispose can still be queued in
      // the mailbox; let it land before freezing the observed value.
      do! Task.Delay 100
      let frozenAt = h.Model.Count
      do! Task.Delay 150

      let frozen = h.Model.Count = frozenAt && starts.Value = 1

      h.Dispatch ToggleRunning

      let! resumed =
        waitFor 5000 (fun () -> h.Model.Count > frozenAt && starts.Value = 2)

      lifetime.Dispose()

      return ticking && stopped && frozen && resumed && stops.Value = 2
    })

  Assert.True result
}

[<Fact>]
let changingAnIdDependencyRestartsTheSubscription () : Task = task {
  let! result =
    onUi(fun () -> task {
      let starts = ref 0
      let stops = ref 0
      let h = Harness<PaceModel, PaceMsg>()

      let subscribe: Subscribe<PaceMsg> =
        fun dispatch ->
          incr starts
          countingTicker dispatch PaceTick 40.0 stops

      let program =
        Program.mkSimple
          (fun () -> { IntervalMs = 40; Count = 0 })
          (fun msg m ->
            match msg with
            | PaceTick -> { m with Count = m.Count + 1 }
            | SetInterval v -> { m with IntervalMs = v }
          )
          h.View
        |> Program.withSubscription(fun m -> [
          [ "ticker"; string m.IntervalMs ], subscribe
        ])

      let _, lifetime = Program.runWithLifecycle () program

      let! firstRun =
        waitFor 5000 (fun () -> h.Model.Count >= 2 && starts.Value = 1)

      h.Dispatch(SetInterval 15)

      let! restarted =
        waitFor 5000 (fun () -> starts.Value = 2 && h.Model.Count >= 3)

      lifetime.Dispose()

      return firstRun && restarted
    })

  Assert.True result
}

[<Fact>]
let childSubscriptionsComposeUnderScopedIds () : Task = task {
  let! result =
    onUi(fun () -> task {
      let errors = ConcurrentQueue<string>()
      let h = Harness<TwinModel, TwinMsg>()

      let childTicker: Subscribe<unit> =
        fun dispatch -> countingTicker dispatch () 25.0 (ref 0)

      let program =
        Program.mkSimple
          (fun () -> { A = 0; B = 0 })
          (fun msg m ->
            match msg with
            | TickA -> { m with A = m.A + 1 }
            | TickB -> { m with B = m.B + 1 }
          )
          h.View
        |> Program.withSubscription(fun _ ->
          Sub.batch [
            Sub.map "a" (fun () -> TickA) (Sub.ofId "tick" childTicker)
            Sub.map "b" (fun () -> TickB) (Sub.ofId "tick" childTicker)
          ]
        )
        |> Program.withErrorHandler(fun (ctx, _) -> errors.Enqueue ctx)

      let _, lifetime = Program.runWithLifecycle () program

      let! bothTicking =
        waitFor 5000 (fun () -> h.Model.A >= 3 && h.Model.B >= 3)

      lifetime.Dispose()

      return bothTicking && errors.IsEmpty
    })

  Assert.True result
}

// ---------------------------------------------------------------
// Use case: bridging sources into the loop.
// ---------------------------------------------------------------

[<Fact>]
let fromObservableBridgesASubject () : Task = task {
  let! result =
    onUi(fun () -> task {
      let source = new Subject<BridgeMsg>()
      let h = Harness<int, BridgeMsg>()

      let program =
        Program.mkSimple (fun () -> 0) (fun (Got v) _ -> v) h.View
        |> Program.withSubscription(fun _ -> Sub.fromObservable "src" source)

      let _, lifetime = Program.runWithLifecycle () program

      source.OnNext(Got 7)
      let! delivered = waitFor 2000 (fun () -> h.Model = 7)

      lifetime.Dispose()
      source.OnNext(Got 9)
      do! Task.Delay 200

      return delivered && h.Model = 7
    })

  Assert.True result
}

[<Fact>]
let fromRoutedEventDeliversKeyEventsAndStopsOnDispose () : Task = task {
  let! result =
    onUi(fun () -> task {
      let h = Harness<int, KeyMsg>()
      let textBox = TextBox()
      textBox.Text <- "draft"

      let program =
        Program.mkSimple (fun () -> 0) (fun _ n -> n + 1) (keeping h textBox)
        |> Program.withSubscription(fun _ ->
          Sub.fromRoutedEvent
            "keys"
            textBox
            InputElement.KeyDownEvent
            (fun _ -> KeyMsg.Pressed)
        )

      let _, lifetime = Program.runWithLifecycle () program

      let raiseKey () =
        let args = KeyEventArgs(Key = Key.A)
        args.RoutedEvent <- InputElement.KeyDownEvent
        textBox.RaiseEvent args

      raiseKey()
      let! delivered = waitFor 2000 (fun () -> h.Model = 1)

      lifetime.Dispose()
      raiseKey()
      do! Task.Delay 200

      return delivered && h.Model = 1
    })

  Assert.True result
}

// ---------------------------------------------------------------
// Use case: page lifetimes tied to the visual tree.
// ---------------------------------------------------------------

[<Fact>]
let untilDetachedEndsSubscriptionsWhenControlLeavesTree () : Task = task {
  let! result =
    onUi(fun () -> task {
      let stops = ref 0
      let h = Harness<int, DetachMsg>()
      let border = Border()

      let ticker: Subscribe<DetachMsg> =
        fun dispatch -> countingTicker dispatch Timed 25.0 stops

      let program =
        Program.mkSimple (fun () -> 0) (fun _ n -> n + 1) (keeping h border)
        |> Program.withSubscription(fun _ ->
          Sub.untilDetached border (Sub.ofId "ticker" ticker)
        )

      let window = Window(Content = border)
      window.Show()

      let _, lifetime = Program.runWithLifecycle () program
      let! attachedTicks = waitFor 5000 (fun () -> h.Model >= 3)

      window.Content <- null
      let! stopped = waitFor 2000 (fun () -> stops.Value = 1)
      // A tick dispatched just before the detach disposed the timer can
      // still be queued in the mailbox; let it land.
      do! Task.Delay 100
      let frozenAt = h.Model
      do! Task.Delay 150

      window.Close()
      lifetime.Dispose()

      return attachedTicks && stopped && h.Model = frozenAt
    })

  Assert.True result
}

[<Fact>]
let runWithLifetimeStopsProgramOnDetach () : Task = task {
  let! result =
    onUi(fun () -> task {
      let h = Harness<int, DetachMsg>()
      let border = Border()

      let program =
        Program.mkSimple (fun () -> 0) (fun _ n -> n + 1) (keeping h border)
        |> Program.withSubscription(fun _ ->
          Sub.interval
            "ticker"
            (TimeSpan.FromMilliseconds 25.0)
            (fun () -> Timed)
        )

      let window = Window(Content = border)
      window.Show()

      let returnedBorder, handle = Program.runWithLifetime border () program
      let! ticking = waitFor 5000 (fun () -> h.Model >= 3)

      window.Content <- null
      let! detached = waitFor 2000 (fun () -> h.Completed)
      // Let any tick queued before the dispose land before freezing.
      do! Task.Delay 100
      let frozenAt = h.Model
      do! Task.Delay 150

      window.Close()

      return
        ticking
        && detached
        && obj.ReferenceEquals(returnedBorder, border)
        && h.Model = frozenAt
    })

  Assert.True result
}

// ---------------------------------------------------------------
// Use case: robustness of the loop and the command pipeline.
// ---------------------------------------------------------------

[<Fact>]
let loopSurvivesUpdateExceptions () : Task = task {
  let! result =
    onUi(fun () -> task {
      let errors = ConcurrentQueue<string>()
      let h = Harness<int, CrashMsg>()

      let program =
        Program.mkSimple
          (fun () -> 0)
          (fun msg n ->
            match msg with
            | Bad -> failwith "boom"
            | Good v -> n + v
          )
          h.View
        |> Program.withErrorHandler(fun (ctx, _) -> errors.Enqueue ctx)

      let _, lifetime = Program.runWithLifecycle () program

      h.Dispatch Bad
      let! reported = waitFor 2000 (fun () -> not errors.IsEmpty)

      h.Dispatch(Good 3)
      let! recovered = waitFor 2000 (fun () -> h.Model = 3)

      lifetime.Dispose()

      return reported && recovered
    })

  Assert.True result
}

[<Fact>]
let commandsRunEndToEnd () : Task = task {
  let! result =
    onUi(fun () -> task {
      let h = Harness<CmdMsg list, CmdMsg>()

      let load (_: unit) : Task<int> = task {
        do! Task.Delay 50
        return 5
      }

      let program =
        Program.mkProgram
          (fun () ->
            [],
            Cmd.batch [
              Cmd.ofMsg Started
              Cmd.OfTask.perform load () Loaded
              Cmd.OfFunc.either
                (fun (_: unit) -> failwith "kaboom": string)
                ()
                (fun _ -> Started)
                (fun ex -> Failed ex.Message)
            ]
          )
          (fun msg model -> msg :: model, Cmd.none)
          h.View

      let _, lifetime = Program.runWithLifecycle () program
      // The throwing either never dispatches its success branch, so the
      // pipeline produces exactly three messages.
      let! ran = waitFor 5000 (fun () -> List.length h.Model >= 3)

      lifetime.Dispose()

      return
        ran
        && List.contains Started h.Model
        && List.contains (Loaded 5) h.Model
        && List.exists
          (function
          | Failed "kaboom" -> true
          | _ -> false)
          h.Model
    })

  Assert.True result
}

[<Fact>]
let ofActionAndOfEffectRunViewSideEffectsFromUpdate () : Task = task {
  let! result =
    onUi(fun () -> task {
      let h = Harness<string, EffectMsg>()
      let textBox = TextBox()
      textBox.Text <- "draft"

      let program =
        Program.mkProgram
          (fun () -> "", Cmd.none)
          (fun msg model ->
            match msg with
            | ClearTextBox tb ->
              model,
              Cmd.batch [
                Cmd.ofAction tb.Clear
                Cmd.ofEffect(fun dispatch -> dispatch Completed)
              ]
            | Completed -> "completed", Cmd.none
          )
          (keeping h textBox)

      let _, lifetime = Program.runWithLifecycle () program

      h.Dispatch(ClearTextBox textBox)

      let! cleared =
        waitFor 2000 (fun () -> textBox.Text = "" && h.Model = "completed")

      lifetime.Dispose()

      return cleared
    })

  Assert.True result
}

[<Fact>]
let viewFactoryFailureReportsErrorInsteadOfCrashing () : Task = task {
  let! result =
    onUi(fun () -> task {
      let errors = ConcurrentQueue<string>()

      let program =
        Program.mkSimple
          (fun () -> 0)
          (fun _ n -> n + 1)
          (fun _ _ -> failwith "boom")
        |> Program.withErrorHandler(fun (ctx, _) -> errors.Enqueue ctx)

      let view, lifetime = Program.runWithLifecycle () program
      lifetime.Dispose()

      return
        isNull(box view)
        && (errors |> Seq.exists(fun ctx -> ctx.Contains "View error"))
    })

  Assert.True result
}
