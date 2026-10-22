module MVUish.Avalonia.Tests.TestSession

// One headless session serves the WHOLE assembly: Dispatcher.UIThread is
// process-global, so two sessions make program-loop InvokeAsync posts land
// on the wrong dispatcher. Test parallelization is disabled to keep
// programs from interleaving on the shared UI thread.

open System
open Avalonia
open Avalonia.Headless

[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()

type TestApp() =
  inherit Application()

let Session = HeadlessUnitTestSession.StartNew(typeof<TestApp>)
