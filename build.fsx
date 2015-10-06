#load "tools/includes.fsx"
open IntelliFactory.Build

let bt =
    BuildTool().PackageId("IntelliFactory.Reactive")
        .VersionFrom("WebSharper")
        .WithFSharpVersion(FSharpVersion.FSharp30)
        .WithFramework(fun fw -> fw.Net40)

let main =
    bt.WebSharper.Library("IntelliFactory.Reactive")
        .SourcesFromProject()

bt.Solution [
    main

    bt.NuGet.CreatePackage()
        .Configure(fun c ->
            { c with
                Title = Some "IntelliFactory.Reactive"
                LicenseUrl = Some "http://websharper.com/licensing"
                ProjectUrl = Some "https://github.com/intellifactory/reactive"
                Description = "Reactive Library for WebSharper"
                RequiresLicenseAcceptance = true })
        .Add(main)
]
|> bt.Dispatch
