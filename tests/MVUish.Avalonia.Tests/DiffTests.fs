module MVUish.Avalonia.Tests.DiffTests

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open MVUish.Avalonia.Collections
open Xunit

type Note = { Id: int; Text: string }

let note (id: int) : Note = { Id = id; Text = string id }

let notes (ids: int list) : IReadOnlyList<Note> =
  ResizeArray(ids |> List.map note) :> IReadOnlyList<Note>

/// Diff the ids, apply the changes to a live collection seeded with the old
/// items, and assert the collection ends equal to the new items.
let private roundTrip
  (oldItems: IReadOnlyList<Note>)
  (newItems: IReadOnlyList<Note>)
  : Change<Note> list =
  match Diff.keyed (fun (n: Note) -> n.Id) oldItems newItems with
  | Ok changes ->
    let collection = ObservableCollection<Note>(oldItems)
    Diff.apply collection changes
    Assert.Equal<Note>(Seq.toList newItems, Seq.toList collection)
    changes
  | Error duplicates -> failwithf "keyed diff failed: %A" duplicates

[<Fact>]
let ``keyed: first snapshot inserts in order`` () =
  let changes = roundTrip (notes []) (notes [ 1; 2; 3 ])

  Assert.Equal<Change<Note>>(
    [ Insert(0, note 1); Insert(1, note 2); Insert(2, note 3) ],
    changes
  )

[<Fact>]
let ``keyed: append at tail inserts one item`` () =
  let changes = roundTrip (notes [ 1; 2 ]) (notes [ 1; 2; 3 ])
  Assert.Equal<Change<Note>>([ Insert(2, note 3) ], changes)

[<Fact>]
let ``keyed: prepend inserts at head`` () =
  let changes = roundTrip (notes [ 2; 3 ]) (notes [ 1; 2; 3 ])
  Assert.Equal<Change<Note>>([ Insert(0, note 1) ], changes)

[<Fact>]
let ``keyed: insert in the middle`` () =
  let changes = roundTrip (notes [ 1; 3 ]) (notes [ 1; 2; 3 ])
  Assert.Equal<Change<Note>>([ Insert(1, note 2) ], changes)

[<Fact>]
let ``keyed: remove head`` () =
  let changes = roundTrip (notes [ 1; 2; 3 ]) (notes [ 2; 3 ])
  Assert.Equal<Change<Note>>([ Remove 0 ], changes)

[<Fact>]
let ``keyed: remove middle`` () =
  let changes = roundTrip (notes [ 1; 2; 3 ]) (notes [ 1; 3 ])
  Assert.Equal<Change<Note>>([ Remove 1 ], changes)

[<Fact>]
let ``keyed: remove tail`` () =
  let changes = roundTrip (notes [ 1; 2; 3 ]) (notes [ 1; 2 ])
  Assert.Equal<Change<Note>>([ Remove 2 ], changes)

[<Fact>]
let ``keyed: clear removes from the tail down`` () =
  let changes = roundTrip (notes [ 1; 2; 3 ]) (notes [])
  Assert.Equal<Change<Note>>([ Remove 2; Remove 1; Remove 0 ], changes)

[<Fact>]
let ``keyed: no-op emits no changes`` () =
  let changes = roundTrip (notes [ 1; 2 ]) (notes [ 1; 2 ])
  Assert.Equal<Change<Note>>([], changes)

[<Fact>]
let ``keyed: changed item with the same key sets in place`` () =
  let changed = { Id = 1; Text = "changed" }
  let newItems = ResizeArray [ changed; note 2 ] :> IReadOnlyList<Note>
  let changes = roundTrip (notes [ 1; 2 ]) newItems
  Assert.Equal<Change<Note>>([ Set(0, changed) ], changes)

[<Fact>]
let ``keyed: move to front emits one move`` () =
  let changes = roundTrip (notes [ 1; 2; 3 ]) (notes [ 3; 1; 2 ])
  Assert.Equal<Change<Note>>([ Move(2, 0) ], changes)

[<Fact>]
let ``keyed: reversal moves without insert or remove`` () =
  let changes = roundTrip (notes [ 1; 2; 3; 4 ]) (notes [ 4; 3; 2; 1 ])

  Assert.Equal<Change<Note>>([ Move(3, 0); Move(3, 1); Move(3, 2) ], changes)

[<Fact>]
let ``keyed: seeded shuffle round-trips`` () =
  let random = Random 987
  let ids = [ 0..99 ]
  let shuffled = ResizeArray ids

  // Fisher-Yates with a fixed seed keeps this test reproducible.
  for i = shuffled.Count - 1 downto 1 do
    let j = random.Next(i + 1)
    let swapped = shuffled[i]
    shuffled[i] <- shuffled[j]
    shuffled[j] <- swapped

  roundTrip (notes ids) (notes(Seq.toList shuffled)) |> ignore

[<Fact>]
let ``keyed: repeated new keys report every repeated key`` () =
  let result =
    Diff.keyed (fun (n: Note) -> n.Id) (notes [ 1; 2 ]) (notes [ 2; 2; 1; 1 ])

  match result with
  | Error duplicates ->
    Assert.Equal<int list>([ 2; 1 ], duplicates.New)
    Assert.Equal<int list>([], duplicates.Old)
  | Ok changes -> failwithf "expected an error, got %A" changes

[<Fact>]
let ``keyed: repeated old keys report the error`` () =
  let result = Diff.keyed (fun (n: Note) -> n.Id) (notes [ 1; 1 ]) (notes [ 1 ])

  match result with
  | Error duplicates ->
    Assert.Equal<int list>([ 1 ], duplicates.Old)
    Assert.Equal<int list>([], duplicates.New)
  | Ok changes -> failwithf "expected an error, got %A" changes

// -----------------------------------------------------------
// Structural diff
// -----------------------------------------------------------

let private words (values: string list) : IReadOnlyList<string> =
  ResizeArray values :> IReadOnlyList<string>

let private roundTripStructural
  (oldItems: IReadOnlyList<string>)
  (newItems: IReadOnlyList<string>)
  : Change<string> list =
  let changes = Diff.structural oldItems newItems
  let collection = ObservableCollection<string>(oldItems)
  Diff.apply collection changes
  Assert.Equal<string>(Seq.toList newItems, Seq.toList collection)
  changes

[<Fact>]
let ``structural: append and remove at the tail`` () =
  let appended =
    roundTripStructural (words [ "a"; "b" ]) (words [ "a"; "b"; "c" ])

  Assert.Equal<Change<string>>([ Insert(2, "c") ], appended)

  let removed =
    roundTripStructural (words [ "a"; "b"; "c" ]) (words [ "a"; "b" ])

  Assert.Equal<Change<string>>([ Remove 2 ], removed)

[<Fact>]
let ``structural: remove in the middle`` () =
  let changes =
    roundTripStructural (words [ "a"; "b"; "c" ]) (words [ "a"; "c" ])

  Assert.Equal<Change<string>>([ Remove 1 ], changes)

[<Fact>]
let ``structural: changed item in the middle collapses to a set`` () =
  let changes =
    roundTripStructural (words [ "a"; "b"; "c" ]) (words [ "a"; "X"; "c" ])

  Assert.Equal<Change<string>>([ Set(1, "X") ], changes)

[<Fact>]
let ``structural: insert in the middle keeps equal items matched`` () =
  let changes =
    roundTripStructural (words [ "a"; "c" ]) (words [ "a"; "b"; "c" ])

  Assert.Equal<Change<string>>([ Insert(1, "b") ], changes)

[<Fact>]
let ``structural: clear removes from the head`` () =
  let changes = roundTripStructural (words [ "a"; "b"; "c" ]) (words [])

  Assert.Equal<Change<string>>([ Remove 0; Remove 0; Remove 0 ], changes)

[<Fact>]
let ``structural: no-op emits no changes`` () =
  let changes = roundTripStructural (words [ "a"; "b" ]) (words [ "a"; "b" ])
  Assert.Equal<Change<string>>([], changes)

[<Fact>]
let ``structural: repeated values are allowed`` () =
  let appended =
    roundTripStructural (words [ "a"; "b"; "a" ]) (words [ "a"; "b"; "a"; "a" ])

  Assert.Equal<Change<string>>([ Insert(3, "a") ], appended)

  let removed =
    roundTripStructural (words [ "a"; "b"; "a"; "a" ]) (words [ "a"; "b"; "a" ])

  Assert.Equal<Change<string>>([ Remove 3 ], removed)

[<Fact>]
let ``structural: seeded shuffle round-trips`` () =
  let random = Random 4242
  let values = [ for i in 0..49 -> string i ]
  let shuffled = ResizeArray values

  for i = shuffled.Count - 1 downto 1 do
    let j = random.Next(i + 1)
    let swapped = shuffled[i]
    shuffled[i] <- shuffled[j]
    shuffled[j] <- swapped

  roundTripStructural (words values) (words(Seq.toList shuffled)) |> ignore
