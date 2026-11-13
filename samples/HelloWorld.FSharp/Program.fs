open System
open System.Reflection

[<EntryPoint>]
let main args =
    let asm = Assembly.GetExecutingAssembly()
    let version =
        match asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> string (asm.GetName().Version)
        | a -> a.InformationalVersion
    printfn "Hello, World! (F#, version %s, %d-bit)" version (IntPtr.Size * 8)
    printfn "Running on %s from %s" Environment.MachineName AppDomain.CurrentDomain.BaseDirectory
    if args.Length > 0 then printfn "Arguments: %s" (String.Join(" ", args))
    0
