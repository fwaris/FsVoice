#load "packages.fsx"
open System
open System.Diagnostics

let procs = Process.GetProcesses() 
let unique = procs |> Array.map _.ProcessName |> set
unique |> Set.filter (fun x -> x.Contains("olk"))
unique.Count

procs
|> Array.map (fun p -> p.ProcessName)
|> Seq.filter (fun x -> x.Contains("olk"))
|> Seq.iter (fun x -> printfn $"{x}")

