namespace Speak2Docs

open System
open FSharp.DI

type Speak2DocsLog() = class end

module Log =
    let mutable debug_logging = false
    let log = DI.loggerLazy<Speak2DocsLog> ()
