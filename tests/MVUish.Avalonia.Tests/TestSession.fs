module MVUish.Avalonia.Tests.TestSession

// One headless session serves the WHOLE assembly: Dispatcher.UIThread is
// process-global, so two sessions make program-loop posts land on the
// wrong dispatcher. Parallelization stays off for these session tests:
// they share the one rendering thread plus process-global focus, input,
// and clipboard. Logic-only tests bound to a fake IDispatcher share
// nothing and need no such guard.

open System
open Avalonia
open Avalonia.Headless

[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()

type TestApp() =
  inherit Application()

let Session = HeadlessUnitTestSession.StartNew(typeof<TestApp>)
