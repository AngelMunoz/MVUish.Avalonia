module MVUish.Avalonia

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Runtime.CompilerServices
open System.Threading.Tasks
open System.Reactive.Subjects
open System.Reactive.Linq
open Avalonia
open Avalonia.Controls
open Avalonia.Interactivity
open Avalonia.Threading

// The keyed subscription engine (Sub.Internal.diff/change/NewSubs) and the
// ValueTask completed-synchronously fast paths are ported from Elmish
// (https://github.com/elmish/elmish), Copyright elmish contributors,
// Apache License 2.0. Everything else in this file is MIT-licensed.

// -----------------------------------------------------------
// 1. TYPES
// -----------------------------------------------------------

/// Dispatch is a function in F# for natural usage.
type Dispatch<'Msg> = 'Msg -> unit

/// Cmd accepts a Dispatch function and an Error handler.
type Cmd<'Msg> = Dispatch<'Msg> -> (string * exn -> unit) -> unit

/// SubId identifies one subscription inside a program. It is a list of
/// strings so callers can scope child subscriptions with a prefix and
/// embed dependency values (for example ["timer"; "100"]) that restart
/// the subscription when they change.
type SubId = string list

/// Subscribe starts one subscription and returns the disposable that stops it.
type Subscribe<'Msg> = Dispatch<'Msg> -> IDisposable

/// Sub is the set of subscriptions a program wants active for the current
/// model. The engine starts and stops subscriptions to match this set on
/// every model change; ids must be unique within the set.
type Sub<'Msg> = (SubId * Subscribe<'Msg>) list

/// The Program type that holds the configuration of the MVU app.
type Program<'Arg, 'Model, 'Msg, 'View> = {
  Init: 'Arg -> 'Model * Cmd<'Msg>
  Update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>
  View: IObservable<'Model> -> Dispatch<'Msg> -> 'View
  Subscribe: 'Model -> Sub<'Msg>
  OnError: (string * exn) -> unit
  /// A message matching the predicate terminates the program: all
  /// subscriptions stop, the model stream completes, and the handler runs
  /// with the final model.
  Termination: ('Msg -> bool) * ('Model -> unit)
}

// -----------------------------------------------------------
// 2. CMD HELPERS
// -----------------------------------------------------------

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Cmd =

  let none: Cmd<'Msg> = fun _ _ -> ()

  let inline batch (cmds: seq<Cmd<'Msg>>) : Cmd<'Msg> =
    fun dispatch onError ->
      for cmd in cmds do
        cmd dispatch onError

  let inline ofMsg (msg: 'Msg) : Cmd<'Msg> = fun dispatch _ -> dispatch msg

  /// Lift a raw effect that can also dispatch messages when it finishes.
  /// The effect owns its own error handling; exceptions it throws are not
  /// caught.
  let inline ofEffect
    ([<InlineIfLambda>] effect: Dispatch<'Msg> -> unit)
    : Cmd<'Msg> =
    fun dispatch _ -> effect dispatch

  /// Run a plain action as a command, for view-side interactions such as
  /// clearing a text box from update. The action owns its own error
  /// handling; exceptions it throws are not caught.
  let inline ofAction ([<InlineIfLambda>] action: unit -> unit) : Cmd<'Msg> =
    fun _ _ -> action()

  let inline map
    ([<InlineIfLambda>] mapping: 'A -> 'Msg)
    (cmd: Cmd<'A>)
    : Cmd<'Msg> =
    fun dispatch onError ->
      let dispatchA a = dispatch(mapping a)
      cmd dispatchA onError

  module OfFunc =
    /// Run a sync function and dispatch its result. An exception is
    /// reported to the error handler.
    let inline perform
      ([<InlineIfLambda>] f: 'Arg -> 'Result)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        try
          let result = f arg
          dispatch(ofSuccess result)
        with ex ->
          onError("Sync command failed", ex)

    /// Run a sync function for its effect. Only an exception produces a
    /// message.
    let inline attempt
      ([<InlineIfLambda>] f: 'Arg -> unit)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        try
          f arg
        with ex ->
          try
            dispatch(ofError ex)
          with ex2 ->
            onError("Error handler failed", ex2)

    /// Run a sync function and map success or exception into messages.
    let inline either
      ([<InlineIfLambda>] f: 'Arg -> 'Result)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        try
          dispatch(ofSuccess(f arg))
        with ex ->
          try
            dispatch(ofError ex)
          with ex2 ->
            onError("Error handler failed", ex2)

  module OfAsyncWith =
    /// Run an async workload with a custom start function and dispatch its
    /// result. An exception is reported to the error handler. Use
    /// 'Async.Start' here to migrate from Elmish's thread-pool OfAsync.
    let inline perform
      (start: Async<unit> -> unit)
      ([<InlineIfLambda>] f: 'Arg -> Async<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        start(
          async {
            try
              let! result = f arg
              dispatch(ofSuccess result)
            with ex ->
              onError("Async command failed", ex)
          }
        )

    /// Run an async workload with a custom start function for its effect.
    /// Only an exception produces a message.
    let inline attempt
      (start: Async<unit> -> unit)
      ([<InlineIfLambda>] f: 'Arg -> Async<unit>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        start(
          async {
            try
              do! f arg
            with ex ->
              try
                dispatch(ofError ex)
              with ex2 ->
                onError("Error handler failed", ex2)
          }
        )

    /// Run an async workload with a custom start function and map success
    /// or exception into messages.
    let inline either
      (start: Async<unit> -> unit)
      ([<InlineIfLambda>] f: 'Arg -> Async<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        start(
          async {
            try
              let! result = f arg
              dispatch(ofSuccess result)
            with ex ->
              try
                dispatch(ofError ex)
              with ex2 ->
                onError("Error handler failed", ex2)
          }
        )

  module OfAsync =
    /// Run an async workload on the current thread and dispatch its result.
    /// An exception is reported to the error handler.
    let inline perform
      ([<InlineIfLambda>] f: 'Arg -> Async<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      OfAsyncWith.perform Async.StartImmediate f arg ofSuccess

    /// Run an async workload on the current thread for its effect. Only an
    /// exception produces a message.
    let inline attempt
      ([<InlineIfLambda>] f: 'Arg -> Async<unit>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      OfAsyncWith.attempt Async.StartImmediate f arg ofError

    /// Run an async workload on the current thread and map success or
    /// exception into messages.
    let inline either
      ([<InlineIfLambda>] f: 'Arg -> Async<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      OfAsyncWith.either Async.StartImmediate f arg ofSuccess ofError

  module OfTask =
    /// Run a task and dispatch its result. An exception is reported to the
    /// error handler.
    let inline perform
      ([<InlineIfLambda>] f: 'Arg -> Task<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        let run () = task {
          try
            let! result = f arg
            dispatch(ofSuccess result)
          with ex ->
            onError("Task command failed", ex)
        }

        run() |> ignore

    /// Run a task for its effect. Only an exception produces a message.
    let inline attempt
      ([<InlineIfLambda>] f: 'Arg -> Task)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        let run () = task {
          try
            do! f arg
          with ex ->
            try
              dispatch(ofError ex)
            with ex2 ->
              onError("Error handler failed", ex2)
        }

        run() |> ignore

    /// Run a task and map success or exception into messages.
    let inline either
      ([<InlineIfLambda>] f: 'Arg -> Task<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        let dispatchError (ex: exn) =
          try
            dispatch(ofError ex)
          with ex2 ->
            onError("Error handler failed", ex2)

        let run () = task {
          try
            let! result = f arg
            dispatch(ofSuccess result)
          with ex ->
            dispatchError ex
        }

        try
          run() |> ignore
        with ex ->
          dispatchError ex

  module OfValueTask =
    /// Run a value task and dispatch its result. Value tasks that are
    /// already completed dispatch without allocating a continuation. An
    /// exception is reported to the error handler.
    let inline perform
      ([<InlineIfLambda>] f: 'Arg -> ValueTask<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        try
          let vt: ValueTask<'Result> = f arg

          if vt.IsCompleted then
            if not vt.IsCompletedSuccessfully then
              try
                vt.GetAwaiter().GetResult() |> ignore
              with ex ->
                onError("ValueTask command failed", ex)
            else
              dispatch(ofSuccess vt.Result)
          else
            task {
              try
                let! result = vt
                dispatch(ofSuccess result)
              with ex ->
                onError("ValueTask command failed", ex)
            }
            |> ignore
        with ex ->
          onError("ValueTask command failed", ex)

    /// Run a value task for its effect. Only an exception produces a
    /// message.
    let inline attempt
      ([<InlineIfLambda>] f: 'Arg -> ValueTask)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        let dispatchError (ex: exn) =
          try
            dispatch(ofError ex)
          with ex2 ->
            onError("Error handler failed", ex2)

        try
          let vt: ValueTask = f arg

          if vt.IsCompleted then
            if not vt.IsCompletedSuccessfully then
              try
                vt.GetAwaiter().GetResult()
              with ex ->
                dispatchError ex
          else
            task {
              try
                let! _ = vt
                ()
              with ex ->
                dispatchError ex
            }
            |> ignore
        with ex ->
          dispatchError ex

    /// Run a value task and map success or exception into messages.
    let inline either
      ([<InlineIfLambda>] f: 'Arg -> ValueTask<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        let dispatchError (ex: exn) =
          try
            dispatch(ofError ex)
          with ex2 ->
            onError("Error handler failed", ex2)

        try
          let vt: ValueTask<'Result> = f arg

          if vt.IsCompleted then
            if not vt.IsCompletedSuccessfully then
              try
                vt.GetAwaiter().GetResult() |> ignore
              with ex ->
                dispatchError ex
            else
              dispatch(ofSuccess vt.Result)
          else
            task {
              try
                let! result = vt
                dispatch(ofSuccess result)
              with ex ->
                dispatchError ex
            }
            |> ignore
        with ex ->
          dispatchError ex

// -----------------------------------------------------------
// 2.1 SUB HELPERS
// -----------------------------------------------------------

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Sub =

  /// No subscriptions.
  let none: Sub<'Msg> = []

  /// Aggregate multiple subscription sets into one.
  let inline batch (subs: seq<Sub<'Msg>>) : Sub<'Msg> =
    subs |> Seq.toList |> List.concat

  /// Map child messages into parent messages, scoping every child SubId
  /// with a prefix so children with the same ids never collide.
  let inline map
    (idPrefix: string)
    ([<InlineIfLambda>] mapping: 'A -> 'Msg)
    (sub: Sub<'A>)
    : Sub<'Msg> =
    sub
    |> List.map(fun (subId, subscribe) ->
      idPrefix :: subId, fun dispatch -> subscribe(mapping >> dispatch)
    )

  /// Build a single keyed subscription from a start function.
  let ofId (id: string) (subscribe: Subscribe<'Msg>) : Sub<'Msg> = [
    [ id ], subscribe
  ]

  /// Bridge any observable of messages into a subscription. Disposing the
  /// subscription unsubscribes; the source keeps its own lifetime.
  let fromObservable (id: string) (source: IObservable<'Msg>) : Sub<'Msg> =
    ofId id (fun dispatch -> source.Subscribe dispatch)

  /// Subscribe to a .NET-style event (add/remove handler) and map its args
  /// into messages. dispatch is thread-safe; messages are processed on the
  /// UI thread by the program loop.
  let inline fromEvent
    (id: string)
    (addHandler: EventHandler<'EventArgs> -> unit)
    (removeHandler: EventHandler<'EventArgs> -> unit)
    ([<InlineIfLambda>] map: 'EventArgs -> 'Msg)
    : Sub<'Msg> =
    ofId
      id
      (fun dispatch ->
        let handler =
          EventHandler<'EventArgs>(fun _ args -> dispatch(map args))

        addHandler handler

        {
          new IDisposable with
            member _.Dispose() = removeHandler handler
        }
      )

  /// Dispatch a tick message periodically. Ticks arrive on a thread-pool
  /// thread; the program loop marshals them to the UI thread.
  let inline interval
    (id: string)
    (interval: TimeSpan)
    ([<InlineIfLambda>] tick: unit -> 'Msg)
    : Sub<'Msg> =
    ofId
      id
      (fun dispatch ->
        let timer = new Timers.Timer(interval.TotalMilliseconds)
        timer.Elapsed.Add(fun _ -> dispatch(tick()))
        timer.AutoReset <- true
        timer.Start()

        {
          new IDisposable with
            member _.Dispose() =
              timer.Stop()
              timer.Dispose()
        }
      )

  /// Subscribe to a routed event on a control with full control over
  /// routing strategies and handled-events-too behavior.
  let inline fromRoutedEventWith<'Msg, 'EventArgs, 'Target
    when 'EventArgs :> RoutedEventArgs and 'Target :> Interactive>
    (id: string)
    (target: 'Target)
    (routedEvent: RoutedEvent<'EventArgs>)
    (routes: RoutingStrategies)
    (handledEventsToo: bool)
    ([<InlineIfLambda>] map: 'EventArgs -> 'Msg)
    : Sub<'Msg> =
    ofId
      id
      (fun dispatch ->
        let handler =
          EventHandler<'EventArgs>(fun _ args -> dispatch(map args))

        target.AddHandler(routedEvent, handler, routes, handledEventsToo)

        {
          new IDisposable with
            member _.Dispose() =
              target.RemoveHandler(routedEvent, handler)
        }
      )

  /// Subscribe to a routed event on a control with Direct/Bubble routing,
  /// without receiving handled events.
  let inline fromRoutedEvent<'Msg, 'EventArgs, 'Target
    when 'EventArgs :> RoutedEventArgs and 'Target :> Interactive>
    (id: string)
    (target: 'Target)
    (routedEvent: RoutedEvent<'EventArgs>)
    ([<InlineIfLambda>] map: 'EventArgs -> 'Msg)
    : Sub<'Msg> =
    fromRoutedEventWith
      id
      target
      routedEvent
      (RoutingStrategies.Direct ||| RoutingStrategies.Bubble)
      false
      map

  /// End every subscription in the set when the visual detaches from the
  /// visual tree. The engine may also dispose the same disposables later;
  /// disposal runs once. All starts, stops, and detach events run on the
  /// UI thread, so there is no detach race. A detached subscription stays
  /// in the active set until the next diff removes it; its start function
  /// simply stops producing.
  let untilDetached (visual: Visual) (sub: Sub<'Msg>) : Sub<'Msg> =
    sub
    |> List.map(fun (subId, subscribe) ->
      subId,
      fun dispatch ->
        let mutable inner: IDisposable = Unchecked.defaultof<IDisposable>
        let mutable stopped = false

        let mutable detach =
          Unchecked.defaultof<EventHandler<VisualTreeAttachmentEventArgs>>

        let stop () =
          if not stopped then
            stopped <- true
            visual.DetachedFromVisualTree.RemoveHandler detach

            if not(isNull inner) then
              inner.Dispose()

        detach <-
          EventHandler<VisualTreeAttachmentEventArgs>(fun _ _ -> stop())

        visual.DetachedFromVisualTree.AddHandler detach
        inner <- subscribe dispatch

        {
          new IDisposable with
            member _.Dispose() = stop()
        }
    )

  // Engine: reconciles the active subscription set with the requested set.
  // Ported from Elmish.
  module Internal =

    let idToString (subId: SubId) = String.Join("/", subId)

    let warnDupe onError subId =
      onError("Duplicate SubId: " + idToString subId, exn "Duplicate SubId")

    let tryStop onError (subId, sub: IDisposable) =
      try
        sub.Dispose()
      with ex ->
        onError("Error stopping subscription: " + idToString subId, ex)

    let tryStart
      onError
      dispatch
      (subId, start)
      : (SubId * IDisposable) option =
      try
        Some(subId, start dispatch)
      with ex ->
        onError("Error starting subscription: " + idToString subId, ex)
        None

    /// Stop a set of active subscriptions, reporting each failure.
    let stop onError subs = subs |> List.iter(tryStop onError)

    /// Apply a diff: warn on duplicates, stop removed, start added, keep
    /// the rest. Returns the new active set.
    let change onError dispatch (dupes, toStop, toKeep, toStart) =
      dupes |> List.iter(warnDupe onError)
      toStop |> List.iter(tryStop onError)
      let started = toStart |> List.choose(tryStart onError dispatch)
      toKeep @ started

    module NewSubs =
      let calculate (subs: Sub<'Msg>) =
        List.foldBack
          (fun (subId, start) (dupes, keys, acc) ->
            if Set.contains subId keys then
              subId :: dupes, keys, acc
            else
              dupes, Set.add subId keys, (subId, start) :: acc
          )
          subs
          ([], Set.empty, [])

    /// Compare the active set with the requested set. Returns duplicates,
    /// subs to stop, subs to keep, and subs to start.
    let diff (activeSubs: (SubId * IDisposable) list) (sub: Sub<'Msg>) =
      let keys = activeSubs |> List.map fst |> Set.ofList
      let dupes, newKeys, newSubs = NewSubs.calculate sub

      if keys = newKeys then
        dupes, [], activeSubs, []
      else
        let toKeep, toStop =
          activeSubs |> List.partition(fun (k, _) -> Set.contains k newKeys)

        let toStart =
          newSubs |> List.filter(fun (k, _) -> not(Set.contains k keys))

        dupes, toStop, toKeep, toStart

// -----------------------------------------------------------
// 3. PROGRAM MODULE
// -----------------------------------------------------------

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Program =

  let mkProgram
    (init: 'Arg -> 'Model * Cmd<'Msg>)
    (update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>)
    (view: IObservable<'Model> -> Dispatch<'Msg> -> 'View)
    = {
    Init = init
    Update = update
    View = view
    Subscribe = fun _ -> Sub.none
    OnError = fun (msg, ex) -> printfn "MVUish.Avalonia Error: %s\n%O" msg ex
    Termination = (fun _ -> false), ignore
  }

  let mkSimple
    (init: 'Arg -> 'Model)
    (update: 'Msg -> 'Model -> 'Model)
    (view: IObservable<'Model> -> Dispatch<'Msg> -> 'View)
    =
    mkProgram
      (fun arg -> init arg, Cmd.none)
      (fun msg model -> update msg model, Cmd.none)
      view

  /// Register the subscription set for the program, replacing any existing
  /// one. 'subscribe' runs with every new model; the engine starts and
  /// stops subscriptions to match what it returns.
  let withSubscription
    (subscribe: 'Model -> Sub<'Msg>)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    = { program with Subscribe = subscribe }

  /// Transform the existing subscription function, to accumulate instead
  /// of replace.
  let mapSubscription
    (map: ('Model -> Sub<'Msg>) -> 'Model -> Sub<'Msg>)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    = {
    program with
        Subscribe = map program.Subscribe
  }

  /// Set the exit criteria and the handler, replacing any existing
  /// termination. A message matching the predicate terminates the program:
  /// subscriptions stop, the model stream completes, and the handler runs
  /// with the final model.
  let withTermination
    (predicate: 'Msg -> bool)
    (onTerminate: 'Model -> unit)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    = {
    program with
        Termination = predicate, onTerminate
  }

  /// Transform the existing termination criteria and handler.
  let mapTermination
    (map: ('Msg -> bool) * ('Model -> unit) -> ('Msg -> bool) * ('Model -> unit))
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    = {
    program with
        Termination = map program.Termination
  }

  /// Trace every update: message, resulting model, and the ids of the
  /// subscriptions the new model wants active.
  let withTrace
    (trace: 'Msg -> 'Model -> SubId list -> unit)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    =
    let tracedUpdate msg model =
      let newModel, cmd = program.Update msg model
      trace msg newModel (program.Subscribe newModel |> List.map fst)
      newModel, cmd

    { program with Update = tracedUpdate }

  /// Trace all the updates to the console, including active sub ids.
  let withConsoleTrace (program: Program<'Arg, 'Model, 'Msg, 'View>) =
    let tracedUpdate msg model =
      let newModel, cmd = program.Update msg model
      System.Console.WriteLine($"[Msg] {msg}")
      System.Console.WriteLine($"[Model] {newModel}")
      newModel, cmd

    let tracedSubscribe model =
      let sub = program.Subscribe model
      System.Console.WriteLine(sprintf "[Active subs] %A" (sub |> List.map fst))
      sub

    {
      program with
          Update = tracedUpdate
          Subscribe = tracedSubscribe
    }

  /// Handle command and update errors. Replaces any existing handler.
  let withErrorHandler
    (onError: string * exn -> unit)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    = { program with OnError = onError }

  /// Transform the existing error handler.
  let mapErrorHandler
    (map: ((string * exn) -> unit) -> (string * exn) -> unit)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    = {
    program with
        OnError = map program.OnError
  }

  /// Get the current error handler.
  let onError (program: Program<'Arg, 'Model, 'Msg, 'View>) = program.OnError

  /// Start the program and return the view plus its lifetime handle:
  /// disposing the handle stops subscriptions, completes the model stream,
  /// and stops the update loop. Disposing twice is a no-op. Prefer calling
  /// Dispose on the UI thread. Controls bound to the model stream revert
  /// to their default property values when the stream completes.
  let runWithLifecycle
    (arg: 'Arg)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    : 'View * IDisposable =
    let initModel, initCmd = program.Init arg
    let state = new BehaviorSubject<'Model>(initModel)
    let toTerminate, terminate = program.Termination
    let mutable activeSubs: (SubId * IDisposable) list = []
    let mutable stopped = false
    let mutable enqueue = ignore

    let teardown viaTermination =
      if not stopped then
        stopped <- true
        let finalModel = state.Value
        Sub.Internal.stop program.OnError activeSubs
        state.OnCompleted()
        state.Dispose()

        if viaTermination then
          try
            terminate finalModel
          with ex ->
            program.OnError("Termination handler error", ex)

    let dispatch (msg: 'Msg) =
      // Best-effort guard: messages posted while stopping are dropped.
      if not stopped then
        enqueue(Some msg)

    // Runs on the UI thread for every accepted message.
    let processMsg (msg: 'Msg) =
      try
        if stopped then
          ()
        elif toTerminate msg then
          teardown true
        else
          let current = state.Value
          let newModel, cmd = program.Update msg current

          if not(obj.ReferenceEquals(current, newModel)) then
            state.OnNext newModel

          try
            let sub = program.Subscribe newModel

            activeSubs <-
              Sub.Internal.change
                program.OnError
                dispatch
                (Sub.Internal.diff activeSubs sub)
          with ex ->
            program.OnError($"Subscription error [Context: {msg}]", ex)

          cmd
            dispatch
            (fun (ctx, ex) -> program.OnError($"{ctx} [Context: {msg}]", ex))
      with exn ->
        program.OnError($"Update loop error [Context: {msg}]", exn)

    let mailbox =
      MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
          let! wrapped = inbox.Receive()

          match wrapped with
          | None ->
            teardown false
            return ()
          | Some msg ->
            let op = Dispatcher.UIThread.InvokeAsync(fun () -> processMsg msg)
            do! op.GetTask() |> Async.AwaitTask

            if stopped then return () else return! loop()
        }

        loop()
      )

    enqueue <- mailbox.Post

    // View first (it is the reactive "setState"), then subscriptions,
    // then the init command — the same order Elmish uses.
    let view =
      try
        program.View (state :> IObservable<'Model>) dispatch
      with ex ->
        program.OnError("View error", ex)
        Unchecked.defaultof<'View>

    activeSubs <-
      try
        let sub = program.Subscribe initModel
        Sub.Internal.change program.OnError dispatch (Sub.Internal.diff [] sub)
      with ex ->
        program.OnError("Subscription error [Context: Init]", ex)
        []

    initCmd
      dispatch
      (fun (ctx, ex) -> program.OnError($"{ctx} [Context: Init]", ex))

    let lifetime = {
      new IDisposable with
        member _.Dispose() =
          enqueue None
          teardown false
    }

    view, lifetime

  /// Start the program and return the view. The program runs until its
  /// termination message arrives; to stop it externally, use
  /// runWithLifecycle and dispose the returned handle.
  let runWith (arg: 'Arg) (program: Program<'Arg, 'Model, 'Msg, 'View>) =
    runWithLifecycle arg program |> fst

  /// Start the program bound to a visual's tree lifetime: the returned
  /// handle stops the program when the visual first detaches from the
  /// visual tree. Re-attaching does not restart it; run a new program
  /// instead. For visuals that detach and reattach by design (cached
  /// tabs), use runWithLifecycle and own the disposal yourself.
  let runWithLifetime
    (visual: Visual)
    (arg: 'Arg)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    : 'View * IDisposable =
    let view, lifetime = runWithLifecycle arg program
    let mutable fired = false

    let mutable detach =
      Unchecked.defaultof<EventHandler<VisualTreeAttachmentEventArgs>>

    let disposeOnce () =
      if not fired then
        fired <- true
        visual.DetachedFromVisualTree.RemoveHandler detach
        lifetime.Dispose()

    detach <-
      EventHandler<VisualTreeAttachmentEventArgs>(fun _ _ -> disposeOnce())

    visual.DetachedFromVisualTree.AddHandler detach

    view,
    {
      new IDisposable with
        member _.Dispose() = disposeOnce()
    }

  /// Start the dispatch loop with 'unit' for the init() function.
  let run (program: Program<unit, 'Model, 'Msg, 'View>) = runWith () program

// -----------------------------------------------------------
// 4. EXTENSIONS
// -----------------------------------------------------------

[<Extension>]
module Extensions =

  [<Extension>]
  let BindStream<'T>
    (
      this: AvaloniaObject,
      property: AvaloniaProperty<'T>,
      source: IObservable<'T>
    ) =
    this.Bind(property, source) |> ignore
    this


// -----------------------------------------------------------
// 5. C# INTEROP
// -----------------------------------------------------------

module CSharp =

  // Alias for the F# Program module to avoid shadowing issues
  module FsProgram = Program

  // Convert Action to F# Func
  let inline internal toFs (action: Action<'T>) : ('T -> unit) = action.Invoke
  // Convert F# Func to Action
  let inline internal toAction (f: 'T -> unit) : Action<'T> = Action<'T>(f)

  /// The result of starting a program with a lifetime: the built view and
  /// the handle whose Dispose stops the program.
  type ProgramHandle<'View>(view: 'View, lifetime: IDisposable) =
    member _.View = view
    member _.Lifetime = lifetime

  type Command =
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member None<'Msg>() : Action<Action<'Msg>, Action<string, exn>> =
      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        Cmd.none (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfMsg<'Msg>
      (msg: 'Msg)
      : Action<Action<'Msg>, Action<string, exn>> =
      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        Cmd.ofMsg msg (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Batch<'Msg>
      (cmds: seq<Action<Action<'Msg>, Action<string, exn>>>)
      : Action<Action<'Msg>, Action<string, exn>> =
      let fsCmds =
        cmds
        |> Seq.map(fun c ->
          fun d e ->
            c.Invoke(toAction d, Action<string, exn>(fun s ex -> e(s, ex)))
        )

      let batch = Cmd.batch fsCmds

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        batch (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Map<'A, 'Msg>
      (mapping: Func<'A, 'Msg>, cmd: Action<Action<'A>, Action<string, exn>>)
      : Action<Action<'Msg>, Action<string, exn>> =
      let fsCmd =
        fun d e ->
          cmd.Invoke(toAction d, Action<string, exn>(fun s ex -> e(s, ex)))

      let mapped = Cmd.map mapping.Invoke fsCmd

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        mapped (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Perform<'Arg, 'Result, 'Msg>
      (func: Func<'Arg, 'Result>, arg: 'Arg, ofSuccess: Func<'Result, 'Msg>)
      =
      let cmd = Cmd.OfFunc.perform func.Invoke arg ofSuccess.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    /// Run a plain action as a command, for view-side interactions such
    /// as clearing a text box from update.
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfAction<'Msg>
      (action: Action)
      : Action<Action<'Msg>, Action<string, exn>> =
      let cmd = Cmd.ofAction action.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Attempt<'Arg, 'Msg>
      (func: Func<'Arg, unit>, arg: 'Arg, ofError: Func<exn, 'Msg>)
      =
      let cmd = Cmd.OfFunc.attempt func.Invoke arg ofError.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Either<'Arg, 'Result, 'Msg>
      (
        func: Func<'Arg, 'Result>,
        arg: 'Arg,
        ofSuccess: Func<'Result, 'Msg>,
        ofError: Func<exn, 'Msg>
      ) =
      let cmd =
        Cmd.OfFunc.either func.Invoke arg ofSuccess.Invoke ofError.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member PerformTask<'Arg, 'Result, 'Msg>
      (
        func: Func<'Arg, Task<'Result>>,
        arg: 'Arg,
        ofSuccess: Func<'Result, 'Msg>
      ) =
      let cmd = Cmd.OfTask.perform func.Invoke arg ofSuccess.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member AttemptTask<'Arg, 'Msg>
      (func: Func<'Arg, Task>, arg: 'Arg, ofError: Func<exn, 'Msg>)
      =
      let cmd = Cmd.OfTask.attempt func.Invoke arg ofError.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member EitherTask<'Arg, 'Result, 'Msg>
      (
        func: Func<'Arg, Task<'Result>>,
        arg: 'Arg,
        ofSuccess: Func<'Result, 'Msg>,
        ofError: Func<exn, 'Msg>
      ) =
      let cmd =
        Cmd.OfTask.either func.Invoke arg ofSuccess.Invoke ofError.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member PerformValueTask<'Arg, 'Result, 'Msg>
      (
        func: Func<'Arg, ValueTask<'Result>>,
        arg: 'Arg,
        ofSuccess: Func<'Result, 'Msg>
      ) =
      let cmd = Cmd.OfValueTask.perform func.Invoke arg ofSuccess.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member AttemptValueTask<'Arg, 'Msg>
      (func: Func<'Arg, ValueTask>, arg: 'Arg, ofError: Func<exn, 'Msg>)
      =
      let cmd = Cmd.OfValueTask.attempt func.Invoke arg ofError.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member EitherValueTask<'Arg, 'Result, 'Msg>
      (
        func: Func<'Arg, ValueTask<'Result>>,
        arg: 'Arg,
        ofSuccess: Func<'Result, 'Msg>,
        ofError: Func<exn, 'Msg>
      ) =
      let cmd =
        Cmd.OfValueTask.either func.Invoke arg ofSuccess.Invoke ofError.Invoke

      Action<Action<'Msg>, Action<string, exn>>(fun d e ->
        cmd (toFs d) (fun (s, ex) -> e.Invoke(s, ex))
      )

  /// A C#-nameable subscription set. The F# Sub type is a list
  /// abbreviation and cannot be named from C#; build CSub values with the
  /// Subscription helpers and pass them to WithSubscription.
  type CSub<'Msg>(subs: Sub<'Msg>) =
    member _.Unwrap() = subs

  /// C#-friendly subscription factories returning CSub<'Msg>.
  type Subscription =
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member None<'Msg>() : CSub<'Msg> = CSub Sub.none

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Batch<'Msg>(subs: seq<CSub<'Msg>>) : CSub<'Msg> =
      CSub(subs |> Seq.map(fun s -> s.Unwrap()) |> Sub.batch)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Map<'A, 'Msg>
      (idPrefix: string, mapping: Func<'A, 'Msg>, sub: CSub<'A>)
      : CSub<'Msg> =
      CSub(Sub.map idPrefix mapping.Invoke (sub.Unwrap()))

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfId<'Msg>
      (id: string, subscribe: Func<Action<'Msg>, IDisposable>)
      : CSub<'Msg> =
      CSub(Sub.ofId id (fun d -> subscribe.Invoke(toAction d)))

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Interval<'Msg>
      (id: string, interval: TimeSpan, tick: Func<'Msg>)
      : CSub<'Msg> =
      CSub(Sub.interval id interval tick.Invoke)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member FromEvent<'Msg, 'EventArgs>
      (
        id: string,
        addHandler: Action<EventHandler<'EventArgs>>,
        removeHandler: Action<EventHandler<'EventArgs>>,
        map: Func<'EventArgs, 'Msg>
      ) : CSub<'Msg> =
      CSub(Sub.fromEvent id addHandler.Invoke removeHandler.Invoke map.Invoke)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member FromObservable<'Msg>
      (id: string, source: IObservable<'Msg>)
      : CSub<'Msg> =
      CSub(Sub.fromObservable id source)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member FromRoutedEvent<'Msg, 'EventArgs
      when 'EventArgs :> RoutedEventArgs>
      (
        id: string,
        target: Interactive,
        routedEvent: RoutedEvent<'EventArgs>,
        map: Func<'EventArgs, 'Msg>
      ) : CSub<'Msg> =
      CSub(Sub.fromRoutedEvent id target routedEvent map.Invoke)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member FromRoutedEventWith<'Msg, 'EventArgs
      when 'EventArgs :> RoutedEventArgs>
      (
        id: string,
        target: Interactive,
        routedEvent: RoutedEvent<'EventArgs>,
        routes: RoutingStrategies,
        handledEventsToo: bool,
        map: Func<'EventArgs, 'Msg>
      ) : CSub<'Msg> =
      CSub(
        Sub.fromRoutedEventWith
          id
          target
          routedEvent
          routes
          handledEventsToo
          map.Invoke
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member UntilDetached<'Msg>
      (visual: Visual, sub: CSub<'Msg>)
      : CSub<'Msg> =
      CSub(Sub.untilDetached visual (sub.Unwrap()))

  type ProgramBuilder<'Arg, 'Model, 'Msg, 'View>
    (
      init:
        Func<'Arg, ValueTuple<'Model, Action<Action<'Msg>, Action<string, exn>>>>,
      update:
        Func<
          'Msg,
          'Model,
          ValueTuple<'Model, Action<Action<'Msg>, Action<string, exn>>>
         >,
      view: Func<IObservable<'Model>, Action<'Msg>, 'View>
    ) =

    let mutable _subscribe: Func<'Model, CSub<'Msg>> =
      Func<_, _>(fun _ -> Subscription.None<'Msg>())

    let mutable _trace = false

    let mutable _customTrace: Action<'Msg, 'Model, IReadOnlyList<string>> option =
      None

    let mutable _termination: (Func<'Msg, bool> * Action<'Model>) option = None

    let mutable _onError: Action<string, exn> =
      Action<_, _>(fun msg ex -> printfn "MVUish.Avalonia Error: %s\n%O" msg ex)

    let build () =
      let fsInit a =
        let struct (m, c) = init.Invoke(a)

        m,
        (fun d e ->
          c.Invoke(toAction d, Action<string, exn>(fun s ex -> e(s, ex)))
        )

      let fsUpdate m s =
        let struct (m2, c) = update.Invoke(m, s)

        m2,
        (fun d e ->
          c.Invoke(toAction d, Action<string, exn>(fun s ex -> e(s, ex)))
        )

      let fsView obs d = view.Invoke(obs, toAction d)

      let withSub =
        FsProgram.mkProgram fsInit fsUpdate fsView
        |> FsProgram.withSubscription(fun m -> (_subscribe.Invoke(m)).Unwrap())

      let traced =
        if _trace then
          FsProgram.withConsoleTrace withSub
        else
          withSub

      let customTraced =
        match _customTrace with
        | Some t ->
          let fsTrace (m: 'Msg) (mid: 'Model) (ids: SubId list) =
            let joined = ids |> List.map(fun i -> String.Join("/", i))
            t.Invoke(m, mid, ResizeArray joined :> IReadOnlyList<string>)

          FsProgram.withTrace fsTrace traced
        | None -> traced

      let terminated =
        match _termination with
        | Some(predicate, onTerminate) ->
          FsProgram.withTermination
            predicate.Invoke
            onTerminate.Invoke
            customTraced
        | None -> customTraced

      FsProgram.withErrorHandler
        (fun (msg, ex) -> _onError.Invoke(msg, ex))
        terminated

    member this.WithSubscription(subscribe: Func<'Model, CSub<'Msg>>) =
      _subscribe <- subscribe
      this

    member this.WithConsoleTrace() =
      _trace <- true
      this

    member this.WithTrace(trace: Action<'Msg, 'Model, IReadOnlyList<string>>) =
      _customTrace <- Some trace
      this

    member this.WithTermination
      (predicate: Func<'Msg, bool>, onTerminate: Action<'Model>)
      =
      _termination <- Some(predicate, onTerminate)
      this

    member this.WithErrorHandler(handler: Action<string, exn>) =
      _onError <- handler
      this

    member _.RunWith(arg: 'Arg) : 'View = FsProgram.runWith arg (build())

    member _.RunWithLifecycle(arg: 'Arg) : ProgramHandle<'View> =
      let view, lifetime = FsProgram.runWithLifecycle arg (build())
      ProgramHandle(view, lifetime)

    member _.RunWithLifetime(visual: Visual, arg: 'Arg) : ProgramHandle<'View> =
      let view, lifetime = FsProgram.runWithLifetime visual arg (build())
      ProgramHandle(view, lifetime)

    member this.Run() = this.RunWith(Unchecked.defaultof<'Arg>)

  type ProgramBuilderSimple<'Arg, 'Model, 'Msg, 'View>
    (
      init: Func<'Arg, 'Model>,
      update: Func<'Msg, 'Model, 'Model>,
      view: Func<IObservable<'Model>, Action<'Msg>, 'View>
    ) =

    member _.ToFull() =
      let fullInit arg =
        struct (init.Invoke(arg), Command.None<'Msg>())

      let fullUpdate msg model =
        struct (update.Invoke(msg, model), Command.None<'Msg>())

      ProgramBuilder<_, _, _, _>(
        Func<_, _>(fullInit),
        Func<_, _, _>(fullUpdate),
        view
      )

    member this.RunWith(arg: 'Arg) = this.ToFull().RunWith(arg)

    member this.Run() = this.ToFull().Run()

  type Program =
    static member MkProgram<'Arg, 'Model, 'Msg, 'View>
      (
        init:
          Func<
            'Arg,
            ValueTuple<'Model, Action<Action<'Msg>, Action<string, exn>>>
           >,
        update:
          Func<
            'Msg,
            'Model,
            ValueTuple<'Model, Action<Action<'Msg>, Action<string, exn>>>
           >,
        view: Func<IObservable<'Model>, Action<'Msg>, 'View>
      ) =
      ProgramBuilder<_, _, _, _>(init, update, view)

    static member MkProgram<'Model, 'Msg, 'View>
      (
        init:
          Func<ValueTuple<'Model, Action<Action<'Msg>, Action<string, exn>>>>,
        update:
          Func<
            'Msg,
            'Model,
            ValueTuple<'Model, Action<Action<'Msg>, Action<string, exn>>>
           >,
        view: Func<IObservable<'Model>, Action<'Msg>, 'View>
      ) =
      let wrappedInit (_: unit) = init.Invoke()
      ProgramBuilder<unit, _, _, _>(Func<_, _>(wrappedInit), update, view)

    static member MkSimple<'Arg, 'Model, 'Msg, 'View>
      (
        init: Func<'Arg, 'Model>,
        update: Func<'Msg, 'Model, 'Model>,
        view: Func<IObservable<'Model>, Action<'Msg>, 'View>
      ) =
      ProgramBuilderSimple<_, _, _, _>(init, update, view)

    static member MkSimple<'Model, 'Msg, 'View>
      (
        init: Func<'Model>,
        update: Func<'Msg, 'Model, 'Model>,
        view: Func<IObservable<'Model>, Action<'Msg>, 'View>
      ) =
      let wrappedInit (_: unit) = init.Invoke()

      ProgramBuilderSimple<unit, _, _, _>(Func<_, _>(wrappedInit), update, view)

// -----------------------------------------------------------
// 6. COLLECTION BINDINGS
// -----------------------------------------------------------

module Collections =

  /// One minimal mutation to apply to an items collection.
  /// Indices refer to the collection state at the moment the change applies.
  type Change<'T> =
    | Insert of index: int * item: 'T
    | Remove of index: int
    | Set of index: int * item: 'T
    | Move of oldIndex: int * newIndex: int

  /// Keys that appear more than once on one side of a keyed diff.
  type DuplicateKeys<'K> = { Old: 'K list; New: 'K list }

  /// Pure diff functions between item snapshots. No UI types involved.
  module Diff =

    let private duplicateKeys (keys: 'K array) : 'K list =
      let seen = HashSet<'K>()
      let duplicates = HashSet<'K>()

      for key in keys do
        if not(seen.Add key) then
          duplicates.Add key |> ignore

      Seq.toList duplicates

    /// Compute the minimal changes that turn oldItems into newItems,
    /// matching items by keyOf. A key that survives while its item changed
    /// produces a Set. Returns DuplicateKeys when either side repeats a key.
    let keyed
      (keyOf: 'T -> 'K)
      (oldItems: IReadOnlyList<'T>)
      (newItems: IReadOnlyList<'T>)
      : Result<Change<'T> list, DuplicateKeys<'K>> =
      let oldKeys = Array.init oldItems.Count (fun i -> keyOf oldItems[i])
      let newKeys = Array.init newItems.Count (fun i -> keyOf newItems[i])

      let oldDuplicates = duplicateKeys oldKeys
      let newDuplicates = duplicateKeys newKeys

      if not(List.isEmpty oldDuplicates && List.isEmpty newDuplicates) then
        Error {
          Old = oldDuplicates
          New = newDuplicates
        }
      else
        let newKeySet = HashSet<'K>(newKeys)
        let changes = ResizeArray<Change<'T>>()

        // Removes run from the tail down so each index stays valid as the
        // changes apply in order.
        for i = oldKeys.Length - 1 downto 0 do
          if not(newKeySet.Contains oldKeys[i]) then
            changes.Add(Remove i)

        // Surviving items in old order.
        let working = ResizeArray<'T>()
        let workingKeys = ResizeArray<'K>()

        for i = 0 to oldKeys.Length - 1 do
          if newKeySet.Contains oldKeys[i] then
            working.Add(oldItems[i])
            workingKeys.Add(oldKeys[i])

        // Walk the new list. The working prefix [0..j-1] always matches
        // the new prefix, so a key found here sits after j.
        for j = 0 to newKeys.Length - 1 do
          let key = newKeys[j]
          let item = newItems[j]
          let aligned = j < workingKeys.Count && workingKeys[j] = key

          if not aligned then
            match workingKeys |> Seq.tryFindIndex(fun k -> k = key) with
            | Some sourceIndex ->
              changes.Add(Move(sourceIndex, j))
              working.RemoveAt(sourceIndex)
              workingKeys.RemoveAt(sourceIndex)
              working.Insert(j, item)
              workingKeys.Insert(j, key)
            | None ->
              changes.Add(Insert(j, item))
              working.Insert(j, item)
              workingKeys.Insert(j, key)

          if working[j] <> item then
            changes.Add(Set(j, item))
            working[j] <- item

        Ok(List.ofSeq changes)

    /// Compute changes that turn oldItems into newItems using structural
    /// equality, without keys. A remove and insert that meet at one
    /// position collapse into a single Set. Repeated values are allowed.
    /// Cost is O(old count * new count) time and space.
    let structural
      (oldItems: IReadOnlyList<'T>)
      (newItems: IReadOnlyList<'T>)
      : Change<'T> list =
      let oldCount = oldItems.Count
      let newCount = newItems.Count

      // table[i, j] holds the longest common subsequence length of
      // oldItems[i..] and newItems[j..].
      let table = Array2D.zeroCreate (oldCount + 1) (newCount + 1)

      for i = oldCount - 1 downto 0 do
        for j = newCount - 1 downto 0 do
          table[i, j] <-
            if oldItems[i] = newItems[j] then
              1 + table[i + 1, j + 1]
            else
              max table[i + 1, j] table[i, j + 1]

      let changes = ResizeArray<Change<'T>>()
      let mutable i = 0
      let mutable j = 0
      // Live index shift: items inserted so far minus items removed so far.
      let mutable shift = 0

      while i < oldCount && j < newCount do
        if oldItems[i] = newItems[j] then
          i <- i + 1
          j <- j + 1
        elif table[i + 1, j + 1] = table[i, j] then
          // Substituting costs no subsequence length, so one Set beats a
          // remove and insert pair.
          changes.Add(Set(j, newItems[j]))
          i <- i + 1
          j <- j + 1
        elif table[i + 1, j] >= table[i, j + 1] then
          changes.Add(Remove(i + shift))
          shift <- shift - 1
          i <- i + 1
        else
          changes.Add(Insert(j, newItems[j]))
          shift <- shift + 1
          j <- j + 1

      while i < oldCount do
        changes.Add(Remove(i + shift))
        shift <- shift - 1
        i <- i + 1

      while j < newCount do
        changes.Add(Insert(j, newItems[j]))
        j <- j + 1

      List.ofSeq changes

    /// Apply changes to a collection, in order.
    let apply
      (collection: ObservableCollection<'T>)
      (changes: Change<'T> list)
      : unit =
      for change in changes do
        match change with
        | Insert(index, item) -> collection.Insert(index, item)
        | Remove index -> collection.RemoveAt(index)
        | Set(index, item) -> collection[index] <- item
        | Move(oldIndex, newIndex) -> collection.Move(oldIndex, newIndex)

  [<Extension>]
  module Extensions =

    let internal bindItems
      (control: ItemsControl)
      (source: IObservable<IReadOnlyList<'T>>)
      (compute: IReadOnlyList<'T> -> IReadOnlyList<'T> -> Change<'T> list)
      : IDisposable =
      let collection = ObservableCollection<'T>()
      control.ItemsSource <- collection

      let mutable current: IReadOnlyList<'T> =
        Array.empty<'T> :> IReadOnlyList<'T>

      // The observer must not hold the control strongly: sources are
      // usually long-lived stores, so a strong edge would root the control
      // for as long as the source emits. Holding it weakly lets an
      // abandoned binding end itself once the control is collected, while
      // a detached but alive control keeps updating, like any other
      // Avalonia binding.
      let weak = WeakReference<ItemsControl>(control)

      // No-op until the real subscription exists, in case a hot source
      // emits synchronously during subscribe.
      let mutable subscription: IDisposable = {
        new IDisposable with
          member _.Dispose() = ()
      }

      let handle items =
        match weak.TryGetTarget() with
        | true, _ ->
          compute current items |> Diff.apply collection
          current <- items
        | false, _ -> subscription.Dispose()

      subscription <- Observable.subscribe handle source
      subscription

    let internal keyedCompute
      (keyOf: 'T -> 'K)
      : IReadOnlyList<'T> -> IReadOnlyList<'T> -> Change<'T> list =
      fun oldItems newItems ->
        match Diff.keyed keyOf oldItems newItems with
        | Ok changes -> changes
        | Error duplicates ->
          let describe (keys: 'K list) =
            if List.isEmpty keys then
              "none"
            else
              keys |> List.map string |> String.concat ", "

          invalidArg
            "keyOf"
            $"Item keys must be unique within a snapshot. Repeated old keys: {describe duplicates.Old}. Repeated new keys: {describe duplicates.New}."

    /// Bind an observable of item snapshots to the control's ItemsSource.
    /// ItemsSource is assigned once; each snapshot applies as minimal
    /// insert, remove, replace, and move deltas on an owned collection, so
    /// item containers and selection survive edits. Items are matched by
    /// keyOf; keys must be unique within a snapshot. Emissions must arrive
    /// on the UI thread; Program dispatch guarantees this for model
    /// streams.
    ///
    /// Returns the control, so the call chains in NXUI-style builders.
    /// Like BindStream, the binding needs no disposal: it keeps applying
    /// snapshots while the control is alive — including while detached
    /// from the visual tree — and ends itself on the first emission after
    /// the control is collected.
    ///
    /// A snapshot that repeats a key throws ArgumentException. Bound
    /// inside a Program view, the throw surfaces through the program's
    /// OnError handler.
    ///
    /// F# model lists qualify as snapshots; upcast the observable once:
    /// Observable.map (fun m -> m.Items :> IReadOnlyList<Item>) model.
    [<Extension>]
    let BindItemsSource<'T, 'K when 'T: equality and 'K: equality>
      (
        this: ItemsControl,
        source: IObservable<IReadOnlyList<'T>>,
        keyOf: 'T -> 'K
      ) : ItemsControl =
      bindItems this source (keyedCompute keyOf) |> ignore
      this

    /// Bind an observable of item snapshots to the control's ItemsSource
    /// using structural equality, without keys. Prefer the keyed overload:
    /// it also matches moved items. Emissions must arrive on the UI
    /// thread. Returns the control, for chaining; like BindStream, the
    /// binding needs no disposal and ends once the control is collected.
    [<Extension>]
    let BindItemsSourceStructural<'T when 'T: equality>
      (this: ItemsControl, source: IObservable<IReadOnlyList<'T>>)
      : ItemsControl =
      bindItems this source Diff.structural |> ignore
      this

  /// C#-friendly wrappers around the collection bindings. The overloads
  /// that take F# lists upcast each snapshot internally, so model lists
  /// bind without a manual conversion. Like the F# extensions, they
  /// return the control for chaining and need no disposal.
  [<Extension>]
  type CollectionExtensions =

    /// Bind an observable of item snapshots, matching items by key.
    [<Extension>]
    static member BindItemsSource<'T, 'K when 'T: equality and 'K: equality>
      (
        this: ItemsControl,
        source: IObservable<IReadOnlyList<'T>>,
        keyOf: Func<'T, 'K>
      ) : ItemsControl =
      Extensions.bindItems this source (Extensions.keyedCompute keyOf.Invoke)
      |> ignore

      this

    /// Bind an observable of F# list snapshots, matching items by key.
    [<Extension>]
    static member BindItemsSource<'T, 'K when 'T: equality and 'K: equality>
      (this: ItemsControl, source: IObservable<'T list>, keyOf: Func<'T, 'K>)
      : ItemsControl =
      let snapshots =
        Observable.map (fun items -> items :> IReadOnlyList<'T>) source

      Extensions.bindItems this snapshots (Extensions.keyedCompute keyOf.Invoke)
      |> ignore

      this

    /// Bind an observable of item snapshots using structural equality.
    [<Extension>]
    static member BindItemsSource<'T when 'T: equality>
      (this: ItemsControl, source: IObservable<IReadOnlyList<'T>>)
      : ItemsControl =
      Extensions.bindItems this source Diff.structural |> ignore
      this

    /// Bind an observable of F# list snapshots using structural equality.
    [<Extension>]
    static member BindItemsSource<'T when 'T: equality>
      (this: ItemsControl, source: IObservable<'T list>)
      : ItemsControl =
      let snapshots =
        Observable.map (fun items -> items :> IReadOnlyList<'T>) source

      Extensions.bindItems this snapshots Diff.structural |> ignore
      this
