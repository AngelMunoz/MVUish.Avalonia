module MVUish.Avalonia.Tests.AdapterTests

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Collections.Specialized
open System.Reactive.Subjects
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open MVUish.Avalonia.Collections
open MVUish.Avalonia.Tests.DiffTests
open Xunit

// NotifyCollectionChangedAction cases are not plain F# identifiers.
let private added = NotifyCollectionChangedAction.Add
let private replaced = NotifyCollectionChangedAction.Replace
let private reset = NotifyCollectionChangedAction.Reset

type TestApp() =
  inherit Application()

// One headless session serves every test. Each Dispatch call runs its body
// on the session's UI thread with Avalonia services installed.
let private session = HeadlessUnitTestSession.StartNew(typeof<TestApp>)

let private onUi (test: unit -> unit) =
  session.Dispatch(Action test, CancellationToken.None).GetAwaiter().GetResult()

/// Record every collection-changed action the ItemsSource emits.
let private recorder (collection: ObservableCollection<'T>) =
  let actions = ResizeArray<NotifyCollectionChangedAction>()
  collection.CollectionChanged.Add(fun args -> actions.Add args.Action)
  actions

let private boundNotes (list: ListBox) =
  let source = new Subject<IReadOnlyList<Note>>()
  list.BindItemsSource(source, (fun n -> n.Id)) |> ignore
  source

let private itemsOf (list: ListBox) : ObservableCollection<Note> =
  Assert.IsAssignableFrom<ObservableCollection<Note>>(list.ItemsSource)

[<Fact>]
let ``first snapshot fills the items with Add events`` () =
  onUi(fun () ->
    let list = ListBox()
    let source = boundNotes list
    let collection = itemsOf list
    let actions = recorder collection

    source.OnNext(notes [ 1; 2; 3 ])

    Assert.Equal(3, list.ItemCount)
    Assert.Equal(3, collection.Count)

    Assert.Equal<NotifyCollectionChangedAction>(
      [ added; added; added ],
      Seq.toList actions
    )
  )

[<Fact>]
let ``ItemsSource is assigned once and edits never Reset`` () =
  onUi(fun () ->
    let list = ListBox()
    let source = boundNotes list
    let collection = itemsOf list
    let actions = recorder collection

    source.OnNext(notes [ 1; 2; 3 ])
    Assert.Equal(3, list.ItemCount)

    source.OnNext(notes [ 1; 2; 3; 4 ])
    Assert.Equal(4, list.ItemCount)

    source.OnNext(notes [ 2; 4 ])
    Assert.Equal(2, list.ItemCount)

    source.OnNext(notes [])
    Assert.Equal(0, list.ItemCount)

    Assert.Same(collection, list.ItemsSource)

    Assert.All(
      Seq.toList actions,
      Action<NotifyCollectionChangedAction>(fun action ->
        Assert.NotEqual(reset, action)
      )
    )
  )

[<Fact>]
let ``changed item with the same key raises Replace`` () =
  onUi(fun () ->
    let list = ListBox()
    let source = boundNotes list
    let collection = itemsOf list
    let actions = recorder collection

    source.OnNext(notes [ 1; 2 ])

    let changed = { Id = 1; Text = "changed" }
    source.OnNext(ResizeArray [ changed; note 2 ] :> IReadOnlyList<Note>)

    Assert.Equal(changed.Text, collection[0].Text)

    Assert.Equal<NotifyCollectionChangedAction>(
      [ replaced ],
      Seq.toList(actions |> Seq.skip 2)
    )
  )

[<Fact>]
let ``selection survives a mid-list removal`` () =
  onUi(fun () ->
    let list = ListBox()
    let source = boundNotes list

    source.OnNext(notes [ 1; 2; 3 ])
    list.SelectedIndex <- 2

    // Remove the head item; the selected item must keep its identity.
    source.OnNext(notes [ 2; 3 ])

    Assert.Equal<Note>(note 3, list.SelectedItem :?> Note)
    Assert.Equal(1, list.SelectedIndex)
  )

[<Fact>]
let ``repeated keys throw ArgumentException naming the parameter`` () =
  onUi(fun () ->
    let list = ListBox()
    let source = boundNotes list

    let thrown =
      Assert.Throws<ArgumentException>(
        Action(fun () -> source.OnNext(notes [ 1; 1 ]))
      )

    Assert.Equal("keyOf", thrown.ParamName)
  )

[<Fact>]
let ``structural binding replaces a middle item`` () =
  onUi(fun () ->
    let list = ListBox()
    let source = new Subject<IReadOnlyList<string>>()

    Extensions.BindItemsSourceStructural(list, source) |> ignore

    let collection =
      Assert.IsAssignableFrom<ObservableCollection<string>>(list.ItemsSource)

    let actions = recorder collection
    source.OnNext(ResizeArray [ "a"; "b"; "c" ] :> IReadOnlyList<string>)
    source.OnNext(ResizeArray [ "a"; "X"; "c" ] :> IReadOnlyList<string>)

    Assert.Equal<string>([ "a"; "X"; "c" ], Seq.toList collection)

    Assert.Equal<NotifyCollectionChangedAction>(
      [ replaced ],
      Seq.toList(actions |> Seq.skip 3)
    )
  )

[<Fact>]
let ``F# list snapshots bind without a manual cast`` () =
  onUi(fun () ->
    let list = ListBox()
    let source = new Subject<Note list>()
    list.BindItemsSource(source, (fun n -> n.Id)) |> ignore

    source.OnNext([ note 1; note 2 ])
    Assert.Equal(2, list.ItemCount)

    source.OnNext([ note 2 ])
    Assert.Equal(1, list.ItemCount)
  )

[<Fact>]
let ``detaching from the visual tree stops applying emissions`` () =
  onUi(fun () ->
    let list = ListBox()
    let source = boundNotes list
    source.OnNext(notes [ 1 ])

    let window = Window(Content = list)
    window.Show()
    window.Close()

    source.OnNext(notes [ 1; 2 ])

    let collection = itemsOf list
    Assert.Equal(1, collection.Count)
  )
