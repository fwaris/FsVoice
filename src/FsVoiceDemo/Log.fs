namespace FsVoiceDemo

open System
open FSharp.DI

type FsVoiceDemoLog() = class end

module Log =
    let mutable debug_logging = false
    let log = DI.loggerLazy<FsVoiceDemoLog> ()
