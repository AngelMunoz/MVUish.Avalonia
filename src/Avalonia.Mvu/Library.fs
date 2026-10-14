module Avalonia.Mvu

open System
open System.Runtime.CompilerServices
open System.Threading.Tasks
open System.Reactive.Subjects
open System.Reactive.Linq
open Avalonia
open Avalonia.Threading

// -----------------------------------------------------------
// 1. TYPES
// -----------------------------------------------------------

/// Dispatch is a function in F# for natural usage.
type Dispatch<'Msg> = 'Msg -> unit

/// Cmd accepts a Dispatch function and an Error handler.
type Cmd<'Msg> = Dispatch<'Msg> -> (string * exn -> unit) -> unit

type Sub<'Msg> = Dispatch<'Msg> -> IDisposable

/// The Program type that holds the configuration of the MVU app.
type Program<'Arg, 'Model, 'Msg, 'View> = {
  Init: 'Arg -> 'Model * Cmd<'Msg>
  Update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>
  View: IObservable<'Model> -> Dispatch<'Msg> -> 'View
  Subscribe: 'Model -> Sub<'Msg>
  OnError: (string * exn) -> unit
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

  let inline map
    ([<InlineIfLambda>] mapping: 'A -> 'Msg)
    (cmd: Cmd<'A>)
    : Cmd<'Msg> =
    fun dispatch onError ->
      let dispatchA a = dispatch(mapping a)
      cmd dispatchA onError

  module OfFunc =
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

  module OfAsync =
    let inline perform
      ([<InlineIfLambda>] f: 'Arg -> Async<'Result>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        let computation = async {
          try
            let! result = f arg
            dispatch(ofSuccess result)
          with ex ->
            onError("Async command failed", ex)
        }

        Async.StartImmediate computation

    let inline attempt
      ([<InlineIfLambda>] f: 'Arg -> Async<unit>)
      (arg: 'Arg)
      ([<InlineIfLambda>] ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch onError ->
        let computation = async {
          try
            do! f arg
          with ex ->
            try
              dispatch(ofError ex)
            with ex2 ->
              onError("Error handler failed", ex2)
        }

        Async.StartImmediate computation

  module OfTask =
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

// -----------------------------------------------------------
// 2.1 SUB HELPERS
// -----------------------------------------------------------

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Sub =
  let none: Sub<'Msg> =
    fun _ -> {
      new IDisposable with
        member _.Dispose() = ()
    }

  let inline batch (subs: seq<Sub<'Msg>>) : Sub<'Msg> =
    fun dispatch ->
      let disposables = subs |> Seq.map(fun s -> s dispatch) |> Seq.toArray

      {
        new IDisposable with
          member _.Dispose() =
            for d in disposables do
              d.Dispose()
      }

  let inline map
    ([<InlineIfLambda>] mapping: 'A -> 'Msg)
    (sub: Sub<'A>)
    : Sub<'Msg> =
    fun dispatch ->
      let dispatchA a = dispatch(mapping a)
      sub dispatchA

  /// Subscribe to a .NET-style event (add/remove handler) and map its args into MVU messages.
  /// Note: dispatch is thread-safe; messages are still processed on the UI thread by Program.runWith.
  let inline fromEvent<'Msg, 'EventArgs>
    (addHandler: EventHandler<'EventArgs> -> unit)
    (removeHandler: EventHandler<'EventArgs> -> unit)
    ([<InlineIfLambda>] map: 'EventArgs -> 'Msg)
    : Sub<'Msg> =
    fun dispatch ->
      let handler = EventHandler<'EventArgs>(fun _ args -> dispatch(map args))
      addHandler handler

      {
        new IDisposable with
          member _.Dispose() = removeHandler handler
      }

  /// Dispatch a tick message periodically.
  /// Uses System.Timers.Timer (ticks on a ThreadPool thread). This is OK because dispatch is thread-safe.
  let inline interval<'Msg>
    (interval: TimeSpan)
    ([<InlineIfLambda>] tick: unit -> 'Msg)
    : Sub<'Msg> =
    fun dispatch ->
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
    OnError = fun (msg, ex) -> printfn "Avalonia.Mvu Error: %s\n%O" msg ex
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

  let withSubscription
    (subscribe: 'Model -> Sub<'Msg>)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    = { program with Subscribe = subscribe }

  let withConsoleTrace (program: Program<'Arg, 'Model, 'Msg, 'View>) =
    let tracedUpdate msg model =
      let newModel, cmd = program.Update msg model
      System.Console.WriteLine($"[Msg] {msg}")
      System.Console.WriteLine($"[Model] {newModel}")
      newModel, cmd

    { program with Update = tracedUpdate }

  let withErrorHandler
    (onError: string * exn -> unit)
    (program: Program<'Arg, 'Model, 'Msg, 'View>)
    = { program with OnError = onError }

  let runWith (arg: 'Arg) (program: Program<'Arg, 'Model, 'Msg, 'View>) =
    let initModel, initCmd = program.Init arg
    let state = new BehaviorSubject<'Model>(initModel)

    let mailbox =
      MailboxProcessor.Start(fun inbox ->
        let rec loop () = async {
          let! msg = inbox.Receive()

          let op =
            Dispatcher.UIThread.InvokeAsync(fun () ->
              try
                let currentModel = state.Value
                let newModel, cmd = program.Update msg currentModel

                if not(obj.ReferenceEquals(currentModel, newModel)) then
                  state.OnNext(newModel)

                // Pass a contextual error handler that includes the message
                cmd
                  inbox.Post
                  (fun (ctx, ex) ->
                    program.OnError($"{ctx} [Context: {msg}]", ex)
                  )
              with exn ->
                program.OnError($"Update loop error [Context: {msg}]", exn)
            )

          do! op.GetTask() |> Async.AwaitTask
          return! loop()
        }

        loop()
      )

    let dispatch = mailbox.Post

    // Init Context
    initCmd
      dispatch
      (fun (ctx, ex) -> program.OnError($"{ctx} [Context: Init]", ex))

    try
      let sub = program.Subscribe initModel
      sub dispatch |> ignore
    with ex ->
      program.OnError("Subscription error", ex)

    let view = program.View (state :> IObservable<'Model>) dispatch
    view

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

  type Subscription =
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Batch<'Msg>
      (subs: seq<Func<Action<'Msg>, IDisposable>>)
      : Func<Action<'Msg>, IDisposable> =
      let fsSubs = subs |> Seq.map(fun s -> (fun d -> s.Invoke(toAction d)))
      let batch = Sub.batch fsSubs
      Func<_, _>(fun d -> batch(toFs d))

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Map<'A, 'Msg>
      (mapping: Func<'A, 'Msg>, sub: Func<Action<'A>, IDisposable>)
      : Func<Action<'Msg>, IDisposable> =
      let fsSub = fun d -> sub.Invoke(toAction d)
      let mapped = Sub.map mapping.Invoke fsSub
      Func<_, _>(fun d -> mapped(toFs d))

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member FromEvent<'Msg, 'EventArgs>
      (
        addHandler: Action<EventHandler<'EventArgs>>,
        removeHandler: Action<EventHandler<'EventArgs>>,
        map: Func<'EventArgs, 'Msg>
      ) : Func<Action<'Msg>, IDisposable> =

      Func<_, _>(fun (dispatch: Action<'Msg>) ->
        let fsSub =
          fun d ->
            let handler =
              EventHandler<'EventArgs>(fun _ args -> d(map.Invoke args))

            addHandler.Invoke(handler)

            {
              new IDisposable with
                member _.Dispose() = removeHandler.Invoke(handler)
            }

        fsSub(toFs dispatch)
      )

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Interval<'Msg>
      (interval: TimeSpan, tick: Func<'Msg>)
      : Func<Action<'Msg>, IDisposable> =
      Func<_, _>(fun (dispatch: Action<'Msg>) ->
        let fsSub =
          fun d ->
            let timer = new Timers.Timer(interval.TotalMilliseconds)
            timer.Elapsed.Add(fun _ -> d(tick.Invoke()))
            timer.AutoReset <- true
            timer.Start()

            {
              new IDisposable with
                member _.Dispose() =
                  timer.Stop()
                  timer.Dispose()
            }

        fsSub(toFs dispatch)
      )

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

    let mutable _subscribe: Func<'Model, Func<Action<'Msg>, IDisposable>> =
      Func<_, _>(fun _ ->
        Func<_, _>(fun _ -> {
          new IDisposable with
            member _.Dispose() = ()
        })
      )

    let mutable _trace = false

    let mutable _onError: Action<string, exn> =
      Action<_, _>(fun msg ex -> printfn "Avalonia.Mvu Error: %s\n%O" msg ex)

    member this.WithSubscription
      (sub: Func<'Model, Func<Action<'Msg>, IDisposable>>)
      =
      _subscribe <- sub
      this

    member this.WithConsoleTrace() =
      _trace <- true
      this

    member this.WithErrorHandler(handler: Action<string, exn>) =
      _onError <- handler
      this

    member this.RunWith(arg: 'Arg) =
      let fsInit a =
        let struct (m, c) = init.Invoke(a)

        m,
        (fun d e ->
          c.Invoke(toAction d, Action<string, exn>(fun s ex -> e(s, ex)))
        )

      let fsUpdate m s =
        let struct (m, c) = update.Invoke(m, s)

        m,
        (fun d e ->
          c.Invoke(toAction d, Action<string, exn>(fun s ex -> e(s, ex)))
        )

      let inline fsView obs d = view.Invoke(obs, toAction d)

      let inline fsSub m =
        let cSub = _subscribe.Invoke(m)
        fun d -> cSub.Invoke(toAction d)

      let mutable prog = FsProgram.mkProgram fsInit fsUpdate fsView
      prog <- FsProgram.withSubscription fsSub prog

      if _trace then
        prog <- FsProgram.withConsoleTrace prog

      // Apply the error handler
      prog <-
        FsProgram.withErrorHandler
          (fun (msg, ex) -> _onError.Invoke(msg, ex))
          prog

      FsProgram.runWith arg prog

    member this.Run() = this.RunWith(Unchecked.defaultof<'Arg>)

  type ProgramBuilderSimple<'Arg, 'Model, 'Msg, 'View>
    (
      init: Func<'Arg, 'Model>,
      update: Func<'Msg, 'Model, 'Model>,
      view: Func<IObservable<'Model>, Action<'Msg>, 'View>
    ) =

    member this.ToFull() =
      let fullInit arg =
        struct (init.Invoke(arg), Command.None<'Msg>())

      let fullUpdate msg model =
        struct (update.Invoke(msg, model), Command.None<'Msg>())

      new ProgramBuilder<_, _, _, _>(
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
      new ProgramBuilder<_, _, _, _>(init, update, view)

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
      new ProgramBuilder<unit, _, _, _>(Func<_, _>(wrappedInit), update, view)

    static member MkSimple<'Arg, 'Model, 'Msg, 'View>
      (
        init: Func<'Arg, 'Model>,
        update: Func<'Msg, 'Model, 'Model>,
        view: Func<IObservable<'Model>, Action<'Msg>, 'View>
      ) =
      new ProgramBuilderSimple<_, _, _, _>(init, update, view)

    static member MkSimple<'Model, 'Msg, 'View>
      (
        init: Func<'Model>,
        update: Func<'Msg, 'Model, 'Model>,
        view: Func<IObservable<'Model>, Action<'Msg>, 'View>
      ) =
      let wrappedInit (_: unit) = init.Invoke()

      new ProgramBuilderSimple<unit, _, _, _>(
        Func<_, _>(wrappedInit),
        update,
        view
      )
