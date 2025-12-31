open System

open Avalonia
open Avalonia.Data
open Avalonia.Controls

open NXUI.Extensions

open Avalonia.Mvu

open FSharp.Control.Reactive

type State = {
  count: int
  name: string
  nameFound: bool
}

type Message =
  | Increment
  | Decrement
  | Reset
  | NameFound of bool
  | SetName of string

let update msg state =
  match msg with
  | Increment ->
    { state with count = state.count + 1 }, Cmd.ofMsg(SetName "Increment")
  | Decrement ->
    { state with count = state.count - 1 }, Cmd.ofMsg(SetName "Decrement")
  | Reset -> { state with count = 0 }, Cmd.ofMsg(SetName "Reset")
  | NameFound found -> { state with nameFound = found }, Cmd.none
  | SetName name ->
    let wasNameFound (name: string) =
      name.Equals("peter", StringComparison.InvariantCultureIgnoreCase)

    let ofSuccess (found: bool) = NameFound found

    { state with name = name }, Cmd.OfFunc.perform wasNameFound name ofSuccess

// The view function now matches the Program.View signature: Observable -> Dispatch -> Control
let rootStackPanel
  (window: Window)
  (state: IObservable<State>)
  (dispatch: Dispatch<Message>)
  : StackPanel =

  let counterText =
    state
    |> Observable.map(fun ({ count = count }) -> count)
    |> Observable.distinctUntilChanged
    |> Observable.map(fun count -> $"You clicked %i{count} times")

  let nameFound =
    state
    |> Observable.map(fun ({ nameFound = nameFound }) -> nameFound)
    |> Observable.distinctUntilChanged

  let actionPerformed =
    state
    |> Observable.map(fun ({ name = name }) -> name)
    |> Observable.distinctUntilChanged
    |> Observable.map(fun name -> $"Action Performed: {name}")

  let incrementOnClick _ observable =
    observable |> Observable.add(fun _ -> dispatch Increment)

  let checkText _ observable =
    observable
    |> Observable.throttle(TimeSpan.FromMilliseconds(250.))
    |> Observable.add(fun name -> dispatch(SetName name))

  StackPanel()
    .Children(
      Button().Content("Click me!!").OnClick(incrementOnClick),
      TextBox().Text(window.BindTitle()),
      TextBlock().Text(counterText, BindingMode.OneWay),
      TextBlock().Text(actionPerformed, BindingMode.OneWay),
      StackPanel()
        .Spacing(4.0)
        .Children(
          Label().Content("Find the name!"),
          TextBox().Watermark("Starts with P ends with R").OnText(checkText)
        ),
      CheckBox()
        .IsChecked(nameFound.ToBinding(), BindingMode.OneWay)
        .IsEnabled(false)
        .Content("Name Found")
    )

let view () : Window =
  let window = Window().Title("NXUI and F#").Width(300).Height(300)

  let init () =
    {
      count = 0
      name = ""
      nameFound = false
    },
    Cmd.none

  let program =
    Program.mkProgram init update (rootStackPanel window)
    |> Program.withConsoleTrace

  let content = Program.run program
  window.Content(content)

[<EntryPoint>]
let main argv =
  AppBuilder
    .Configure<Application>()
    .UsePlatformDetect()
    .UseFluentTheme(Styling.ThemeVariant.Dark)
    .WithApplicationName("NXUI and F#")
    .StartWithClassicDesktopLifetime(view, argv)
