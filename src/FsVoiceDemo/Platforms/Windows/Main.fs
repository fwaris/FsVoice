namespace FsVoiceDemo.WinUI

open System

module Program =
    [<EntryPoint; STAThread>]
    let main args =
        do FSharp.Maui.WinUICompat.Program.Main(args, typeof<FsVoiceDemo.WinUI.App>)
        0
