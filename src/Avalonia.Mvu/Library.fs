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

/// Cmd accepts a Dispatch function.
type Cmd<'Msg> = Dispatch<'Msg> -> unit

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
  let none: Cmd<'Msg> = fun _ -> ()

  let batch (cmds: seq<Cmd<'Msg>>) : Cmd<'Msg> =
    fun dispatch ->
      for cmd in cmds do
        cmd dispatch

  let ofMsg (msg: 'Msg) : Cmd<'Msg> = fun dispatch -> dispatch msg

  let map (mapping: 'A -> 'Msg) (cmd: Cmd<'A>) : Cmd<'Msg> =
    fun dispatch ->
      let dispatchA a = dispatch(mapping a)
      cmd dispatchA

  module OfFunc =
    let perform
      (f: 'Arg -> 'Result)
      (arg: 'Arg)
      (ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch ->
        try
          let result = f arg
          dispatch(ofSuccess result)
        with _ ->
          ()

    let attempt
      (f: 'Arg -> unit)
      (arg: 'Arg)
      (ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch ->
        try
          f arg
        with exn ->
          dispatch(ofError exn)

  module OfAsync =
    let perform
      (f: 'Arg -> Async<'Result>)
      (arg: 'Arg)
      (ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch ->
        let computation = async {
          let! result = f arg
          dispatch(ofSuccess result)
        }

        Async.StartImmediate computation

    let attempt
      (f: 'Arg -> Async<unit>)
      (arg: 'Arg)
      (ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch ->
        let computation = async {
          try
            do! f arg
          with exn ->
            dispatch(ofError exn)
        }

        Async.StartImmediate computation

  module OfTask =
    let perform
      (f: 'Arg -> Task<'Result>)
      (arg: 'Arg)
      (ofSuccess: 'Result -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch ->
        let run () = task {
          let! result = f arg
          dispatch(ofSuccess result)
        }

        run() |> ignore

    let attempt
      (f: 'Arg -> Task)
      (arg: 'Arg)
      (ofError: exn -> 'Msg)
      : Cmd<'Msg> =
      fun dispatch ->
        let run () = task {
          try
            do! f arg
          with exn ->
            dispatch(ofError exn)
        }

        run() |> ignore

// -----------------------------------------------------------
// 3. PROGRAM MODULE
// -----------------------------------------------------------

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Program =

  let mkProgram
    (init: 'Arg -> 'Model * Cmd<'Msg>)
    (update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>)
    (view: IObservable<'Model> -> Dispatch<'Msg> -> 'View)
    =
    {
      Init = init
      Update = update
      View = view
      Subscribe =
        fun _ ->
          (fun _ ->
            { new IDisposable with
                member _.Dispose() = ()
            }
          )
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
    =
    { program with Subscribe = subscribe }

  let withConsoleTrace (program: Program<'Arg, 'Model, 'Msg, 'View>) =
    let tracedUpdate msg model =
      let newModel, cmd = program.Update msg model
      System.Console.WriteLine($"[Msg] {msg}")
      System.Console.WriteLine($"[Model] {newModel}")
      newModel, cmd

    { program with Update = tracedUpdate }

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

                cmd(inbox.Post)
              with exn ->
                program.OnError("Update loop error", exn)
            )

          do! op.GetTask() |> Async.AwaitTask
          return! loop()
        }

        loop()
      )

    let dispatch = mailbox.Post

    initCmd dispatch

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
    let binding = source.ToBinding()
    this[property] <- binding
    this

// -----------------------------------------------------------
// 5. C# INTEROP
// -----------------------------------------------------------

module CSharp =

  // Alias for the F# Program module to avoid shadowing issues
  module FsProgram = Program

  // Convert Action to F# Func
  let internal toFs (action: Action<'T>) : ('T -> unit) = action.Invoke
  // Convert F# Func to Action
  let internal toAction (f: 'T -> unit) : Action<'T> = Action<'T>(f)

  type Command =
    static member None<'Msg>() : Action<Action<'Msg>> =
      Action<Action<'Msg>>(fun d -> Cmd.none(toFs d))

    static member OfMsg<'Msg>(msg: 'Msg) : Action<Action<'Msg>> =
      Action<Action<'Msg>>(fun d -> Cmd.ofMsg msg (toFs d))

    static member Batch<'Msg>
      (cmds: seq<Action<Action<'Msg>>>)
      : Action<Action<'Msg>> =
      let fsCmds = cmds |> Seq.map(fun c -> (fun d -> c.Invoke(toAction d)))
      let batch = Cmd.batch fsCmds
      Action<Action<'Msg>>(fun d -> batch(toFs d))

    static member Perform<'Arg, 'Result, 'Msg>
      (func: Func<'Arg, 'Result>, arg: 'Arg, ofSuccess: Func<'Result, 'Msg>)
      =
      let cmd = Cmd.OfFunc.perform func.Invoke arg ofSuccess.Invoke
      Action<Action<'Msg>>(fun d -> cmd(toFs d))

    static member PerformTask<'Arg, 'Result, 'Msg>
      (
        func: Func<'Arg, Task<'Result>>,
        arg: 'Arg,
        ofSuccess: Func<'Result, 'Msg>
      ) =
      let cmd = Cmd.OfTask.perform func.Invoke arg ofSuccess.Invoke
      Action<Action<'Msg>>(fun d -> cmd(toFs d))

  type Subscription =
    static member FromEvent<'Msg, 'EventArgs>
      (
        addHandler: Action<EventHandler<'EventArgs>>,
        removeHandler: Action<EventHandler<'EventArgs>>,
        map: Func<'EventArgs, 'Msg>
      ) : Func<Action<'Msg>, IDisposable> =

      Func<_, _>(fun (dispatch: Action<'Msg>) ->
        let fsSub =
          (fun d ->
            let handler =
              EventHandler<'EventArgs>(fun _ args -> d(map.Invoke(args)))

            addHandler.Invoke(handler)

            { new IDisposable with
                member _.Dispose() = removeHandler.Invoke(handler)
            }
          )

        fsSub(toFs dispatch)
      )

    static member Interval<'Msg>
      (interval: TimeSpan, tick: Func<'Msg>)
      : Func<Action<'Msg>, IDisposable> =
      Func<_, _>(fun (dispatch: Action<'Msg>) ->
        let fsSub =
          (fun d ->
            let timer = new System.Timers.Timer(interval.TotalMilliseconds)
            timer.Elapsed.Add(fun _ -> d(tick.Invoke()))
            timer.AutoReset <- true
            timer.Start()

            { new IDisposable with
                member _.Dispose() =
                  timer.Stop()
                  timer.Dispose()
            }
          )

        fsSub(toFs dispatch)
      )

  type ProgramBuilder<'Arg, 'Model, 'Msg, 'View>
    (
      init: Func<'Arg, ValueTuple<'Model, Action<Action<'Msg>>>>,
      update: Func<'Msg, 'Model, ValueTuple<'Model, Action<Action<'Msg>>>>,
      view: Func<IObservable<'Model>, Action<'Msg>, 'View>
    ) =

    let mutable _subscribe: Func<'Model, Func<Action<'Msg>, IDisposable>> =
      Func<_, _>(fun _ ->
        Func<_, _>(fun _ ->
          { new IDisposable with
              member _.Dispose() = ()
          }
        )
      )

    let mutable _trace = false

    member this.WithSubscription
      (sub: Func<'Model, Func<Action<'Msg>, IDisposable>>)
      =
      _subscribe <- sub
      this

    member this.WithConsoleTrace() =
      _trace <- true
      this

    member this.RunWith(arg: 'Arg) =
      let fsInit a =
        let struct (m, c) = init.Invoke(a)
        m, (fun d -> c.Invoke(toAction d))

      let fsUpdate m s =
        let struct (m, c) = update.Invoke(m, s)
        m, (fun d -> c.Invoke(toAction d))

      let fsView obs d = view.Invoke(obs, toAction d)

      let fsSub m =
        let cSub = _subscribe.Invoke(m)
        fun d -> cSub.Invoke(toAction d)

      let mutable prog = FsProgram.mkProgram fsInit fsUpdate fsView
      prog <- FsProgram.withSubscription fsSub prog

      if _trace then
        prog <- FsProgram.withConsoleTrace prog

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
        init: Func<'Arg, ValueTuple<'Model, Action<Action<'Msg>>>>,
        update: Func<'Msg, 'Model, ValueTuple<'Model, Action<Action<'Msg>>>>,
        view: Func<IObservable<'Model>, Action<'Msg>, 'View>
      ) =
      new ProgramBuilder<_, _, _, _>(init, update, view)

    static member MkProgram<'Model, 'Msg, 'View>
      (
        init: Func<ValueTuple<'Model, Action<Action<'Msg>>>>,
        update: Func<'Msg, 'Model, ValueTuple<'Model, Action<Action<'Msg>>>>,
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
