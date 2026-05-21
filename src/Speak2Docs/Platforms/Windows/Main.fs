namespace Speak2Docs.WinUI

open System

module Program =
    [<EntryPoint; STAThread>]
    let main args =
        do FSharp.Maui.WinUICompat.Program.Main(args, typeof<Speak2Docs.WinUI.App>)
        0
