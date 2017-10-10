module IntelliFactory.Build
#load "ifcore.fsx"
#I "packages/build/Paket.Core/lib/net45"
#r "Paket.Core"
#r "System.Xml"
#r "System.Xml.Linq"
open System
open System.IO
open System.Diagnostics
open System.Collections.Generic


[<AutoOpen>]
module private Cmd =
    let chgExt (ext: string) (filename: string) =
        Path.ChangeExtension(filename, ext)

    let log tag format =
        Printf.kprintf (printfn "[%s] %s" tag) format

    let fail tag format =
        Printf.kprintf (failwithf "[%s] %s" tag) format

    let cp (src: string) (dst: string) =
        log "CP" "%s -> %s" src dst
        File.Copy(src, dst)

    let mv (src: string) (dst: string) =
        log "MV" "%s -> %s" src dst
        File.Move(src, dst)

    let rm (f: string) =
        log "RM" "%s" f
        File.Delete(f)

    let rmDir (d: string) =
        log "RMDIR" "%s" d
        if Directory.Exists(d) then
            Directory.Delete(d, true)

    let mkdir (d: string) =
        log "MKDIR" "%s" d
        Directory.CreateDirectory(d) |> ignore

    let exec (cmd: string) (args: string) =
        log "EXEC" "%s %s" cmd args
        let psi =
            ProcessStartInfo(
                FileName = cmd,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            )
        let p = Process.Start(psi)
        p.StandardOutput.BaseStream.CopyTo(System.Console.OpenStandardOutput())
        // let output = p.StandardOutput.ReadToEnd()
        let error = p.StandardError.ReadToEnd()
        // if not (String.IsNullOrEmpty output) then stdout.Write output
        if not (String.IsNullOrEmpty error) then stderr.Write error
        p.WaitForExit()
        if p.ExitCode <> 0 then
            fail "EXEC" "exited with code %i" p.ExitCode

    let appendLinesTo (file: string) (content: seq<string>) =
        log "Append" "%s" file
        File.AppendAllLines(file, Array.ofSeq content)

    let writeTo (file: string) (content: string) =
        log "WRITE" "%s" file
        File.WriteAllText(file, content)

    let writeLinesTo (file: string) (content: seq<string>) =
        log "WRITE" "%s" file
        File.WriteAllLines(file, Array.ofSeq content)

let parseProjectSources (filename: string) =
    let isNamed (n: string) (e: System.Xml.Linq.XElement) =
        e.Name.LocalName = n
    let xn (n: string) =
        System.Xml.Linq.XName.Get(n)
    let getAttribute n (e: System.Xml.Linq.XElement) =
        e.Attribute(xn n).Value
    System.Xml.Linq.XDocument.Load(filename).Root.Elements()
    |> Seq.filter (isNamed "ItemGroup")
    |> Seq.collect (fun e ->
        e.Elements()
        |> Seq.filter (isNamed "Compile")
        |> Seq.map (getAttribute "Include")
    )
    |> List.ofSeq

type FSharpVersion =
    | FSharp30
    | FSharp31
    | FSharp40
    | FSharp41

type Frameworks() =
    member this.Net40 = "net40"
    member this.Net45 = "net45"

type Reference =
    | Assembly of name: string
    | File of path: string
    | NuGet of NuGetReference
    | Project of WebSharper4Project
    | ProjectFile of string

    override this.ToString() =
        match this with
        | Assembly n -> "Assembly: " + n
        | File n -> "File: " + n
        | NuGet n -> "NuGet: " + n.name
        | Project p -> "Project: " + p.name
        | ProjectFile n -> "MSBuild Project: " + n

    member this.CopyLocal(?x: bool) =
        this // TODO?

and IProject =
    abstract BuildTimeDependencies : list<PaketDependency>
    abstract PackageDependencies : list<PaketDependency>
    abstract Write : hasNet4Version: bool -> unit

and NuGetDependencyVersion =
    | LockVersion
    | CurrentVersion
    | NoVersion

    member this.Pretty =
        match this with
        | LockVersion -> "(force found version)"
        | CurrentVersion -> "(current version)"
        | NoVersion -> "(no version spec)"

    member this.Paket =
        match this with
        | LockVersion -> "LOCKEDVERSION"
        | CurrentVersion -> "CURRENTVERSION"
        | NoVersion -> "~> LOCKEDVERSION"

and PaketDependency =
    | NuGet of name: string * version: NuGetDependencyVersion
    | FrameworkAssembly of string

    static member Check (deps: list<PaketDependency>) =
        deps
        |> List.groupBy (function
            | NuGet (n, _) -> NuGet(n, NoVersion)
            | FrameworkAssembly n as x -> x
        )
        |> List.map (function
            | FrameworkAssembly _ as x, _ -> x
            | NuGet _, [x] -> x
            | NuGet (n, _), xs ->
                let v =
                    xs |> List.tryPick (function
                        | NuGet (_, NoVersion) -> None
                        | NuGet (_, f) -> Some f
                        | _ -> None)
                    |> Option.defaultValue NoVersion
                NuGet (n, v)
        )
        // for d1 in deps do
        //     for d2 in deps do
        //         match d1, d2 with
        //         | NuGet (n1, v1), NuGet (n2, v2) when n1 = n2 && v1 <> v2 ->
        //             fail "DEPS" "Mismatched dependency versions for %s: %s vs %s" n1
        //                 (if v1 then "LOCKEDVERSION" else "(no version spec)") 
        //                 (if v2 then "LOCKEDVERSION" else "(no version spec)")
        //         | _ -> ()

and WebSharper4Project =
    {
        tool : BuildTool
        name : string
        typ : ProjectType
        isExe : bool
        references : Reference list
        embed : string list
        sources : string list
        withSourceMap : bool
        needsSystemWeb : bool
    }

    interface IProject with
        member this.Write(hasNet4Version) =
            let filename = this.ProjectFileName
            rm filename
            let files =
                [
                    for s in this.sources ->
                        sprintf """<Compile Include="%s" />""" s
                    for e in this.embed ->
                        sprintf """<EmbeddedResource Include="%s" />""" e
                ]
                |> List.map (fun s -> "\r\n    " + s)
                |> String.concat ""
            let conditionalRefs = HashSet()
            let refs =
                this.references
                |> List.collect (function
                    | Reference.NuGet _ -> []
                    | Reference.Assembly asm ->
                        let r = sprintf """<Reference Include="%s"/>""" asm
                        if hasNet4Version then
                            conditionalRefs.Add(r) |> ignore
                            []
                        else
                            [r]
                    | Reference.File p ->
                        let name = Path.GetFileNameWithoutExtension(p)
                        [
                            sprintf """<Reference Include="%s">""" name
                            sprintf """  <HintPath>%s</HintPath>""" p
                            "</Reference>"
                        ]
                    | Reference.Project p ->
                        [sprintf """<ProjectReference Include="../%s" />""" p.ProjectFileName]
                    | Reference.ProjectFile n ->
                        [sprintf """<ProjectReference Include="../%s" />""" n]
                )
                |> List.map (fun s -> "\r\n    " + s)
                |> String.concat ""
            let conditionalRefs =
                if conditionalRefs.Count = 0 then "" else
                sprintf """
  <ItemGroup Condition="$(TargetFramework.StartsWith('net4'))">
    %s
  </ItemGroup>""" (String.concat "\r\n    " conditionalRefs)
            let wsContent =
                if this.typ = ProjectType.NotWebSharper then "" else
                sprintf """
    <WebSharperProject>%s</WebSharperProject>
    <WebSharperSourceMap>%b</WebSharperSourceMap>"""
                <| match this.typ with
                    | ProjectType.Extension -> "Extension"
                    | ProjectType.Library -> "Library"
                    | ProjectType.HtmlWebsite -> "Html"
                    | ProjectType.SiteletWebsite -> "Website"
                    | ProjectType.BundleWebsite -> "Bundle"
                    | ProjectType.NotWebSharper -> failwith "Can't happen"
                <| this.withSourceMap
            sprintf """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>%s</TargetFrameworks>%s
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>%s
  </ItemGroup>
  <ItemGroup>%s
  </ItemGroup>%s
  <Import Project="..\.paket\Paket.Restore.targets" />
</Project>""" (String.concat ";" this.Frameworks) wsContent files refs conditionalRefs
            |> writeTo filename

            this.references
            |> List.choose (function
                | Reference.NuGet { name = n } -> Some n
                | _ -> None
            )
            |> List.distinct
            |> writeLinesTo this.PaketReferencesFileName

        member this.BuildTimeDependencies =
            this.references
            |> List.choose (function
                | Reference.NuGet n ->
                    // let v = if n.pre then Some "pre" else None
                    let v = if n.forceFound then LockVersion else NoVersion
                    Some <| PaketDependency.NuGet(n.name, v)
                | Reference.Assembly asm ->
                    Some <| PaketDependency.FrameworkAssembly asm
                | _ -> None
            )

        member this.PackageDependencies =
            this.references
            |> List.choose (function
                | Reference.NuGet n when not n.buildTimeOnly ->
                    // let v = if n.pre then Some "pre" else None
                    let v = if n.forceFound then LockVersion else NoVersion
                    PaketDependency.NuGet(n.name, v)
                    |> Some
                | Reference.Assembly asm ->
                    Some <| PaketDependency.FrameworkAssembly asm
                | _ -> None
            )

    member this.Log(format) = log ("PROJECT " + this.name) format
    member this.Fail(format) = fail ("PROJECT " + this.name) format

    member this.ProjectFileName =
        Path.Combine(this.name, this.name + ".fsproj")

    member this.PaketReferencesFileName =
        Path.Combine(this.name, "paket.references")

    member this.GetFrameworks(hasNet4Version) =
        [
            if hasNet4Version then yield "net461"
            yield "netstandard2.0"
        ]

    member this.Frameworks =
        this.GetFrameworks(this.needsSystemWeb)

    member this.OutputFiles(hasNet4Version) =
        this.GetFrameworks(hasNet4Version)
        |> List.collect (fun fw ->
            let inFw = if this.needsSystemWeb then fw else "netstandard2.0"
            [
                fw, sprintf "%s/bin/Release/%s/%s.dll" this.name inFw this.name
                fw, sprintf "%s/bin/Release/%s/%s.xml" this.name inFw this.name
            ]
        )

    member this.SourcesFromProject() =
        let sources = parseProjectSources this.ProjectFileName
        if List.isEmpty sources then
            this.Log "SourcesFromProject (NONE)"
        else
            sources |> List.iter (this.Log "SourcesFromProject %s")
        let needsSystemWeb =
            this.needsSystemWeb ||
            sources |> List.exists (fun f ->
                File.ReadAllText(Path.Combine(this.name, f)).Contains("System.Web")
            )
        let refs =
            if needsSystemWeb &&
                this.references
                |> List.exists (function Reference.Assembly "System.Web" -> true | _ -> false)
                |> not
            then Reference.Assembly "System.Web" :: this.references
            else this.references
        { this with
            sources = this.sources @ sources
            needsSystemWeb = needsSystemWeb
            references = refs
        }

    member this.References(f) =
        let refs = f (ReferenceBuilder())
        refs |> Seq.iter (this.Log "Reference %O")
        { this with references = this.references @ List.ofSeq refs }

    member this.Embed(f) =
        f |> Seq.iter (this.Log "Embed %s")
        { this with embed = this.embed @ List.ofSeq f }

    member this.WithSourceMap() =
        { this with withSourceMap = true }

and ProjectType =
    | Library
    | Extension
    | HtmlWebsite
    | SiteletWebsite
    | BundleWebsite
    | NotWebSharper

and Solution =
    {
        projects : list<IProject>
    }

    member this.BuildTimeDependencies =
        this.projects
        |> Seq.collect (fun p -> List.ofSeq p.BuildTimeDependencies)
        |> Seq.distinct
        |> List.ofSeq

    member this.PackageDependencies =
        this.projects
        |> Seq.collect (fun p -> List.ofSeq p.PackageDependencies)
        |> Seq.distinct
        |> List.ofSeq

    member this.Write() =
        let hasNet4Version =
            this.projects
            |> Seq.exists (function
                | :? WebSharper4Project as p -> p.needsSystemWeb
                | _ -> false)
        this.projects |> Seq.iter (fun p -> p.Write(hasNet4Version))

and ReferenceBuilder() =
    member this.Assembly x = Reference.Assembly x
    member this.File x = Reference.File x
    member this.NuGet x : NuGetReference =
        {
            name = x
            pre = false
            forceFound = false
            buildTimeOnly = false
            version = None
        }
    member this.Project(x: WebSharper4Project) = Reference.Project x
    member this.Project(x: MSBuildProject) = Reference.ProjectFile x.file

and NuGetReference =
    {
        name: string
        pre: bool
        forceFound: bool
        buildTimeOnly: bool
        version: option<string>
    }

    static member WebSharper =
        {
            name = "WebSharper"
            pre = true
            forceFound = true
            buildTimeOnly = false
            version = None
        }

    static member WebSharperFSharp =
        {
            name = "WebSharper.FSharp"
            pre = true
            forceFound = true
            buildTimeOnly = true
            version = None
        }

    member this.Latest(includePre) =
        { this with pre = includePre }

    member this.ForceFoundVersion() =
        { this with forceFound = true }

    member this.Reference() =
        Reference.NuGet this

    member this.Version(v) =
        { this with version = Some v }

    member this.Version(v, pre) =
        { this with version = Some v; pre = pre }

    member this.BuildTimeOnly() =
        { this with buildTimeOnly = true }

and BuildTool =
    {
        packageId: string
        versionFrom: string * string option
        fsharpVersion: FSharpVersion
        framework: string
        references : Reference list
    }

    member this.Log(format) = log ("PKG " + this.packageId) format
    member this.Fail(format) = fail ("PKG " + this.packageId) format

    member this.PackageId(pkgId) =
        { this with packageId = pkgId }

    member this.VersionFrom(pkgId, ?versionSpec: string) =
        if pkgId <> "WebSharper" then
            this.Fail "VersionFrom %s %s" pkgId (defaultArg versionSpec "")
        this.Log "VersionFrom %s %s" pkgId (defaultArg versionSpec "")
        { this with versionFrom = pkgId, versionSpec }

    member this.WithFSharpVersion(v: FSharpVersion) =
        this.Log "FSharpVersion %A (ignoring)" v
        { this with fsharpVersion = v }

    member this.WithFramework (f) =
        let fw = f (Frameworks())
        this.Log "Framework %s (ignoring)" fw
        { this with framework = fw }

    member this.References(f) =
        let refs = f (ReferenceBuilder())
        refs |> Seq.iter (this.Log "Reference %A")
        { this with references = this.references @ List.ofSeq refs }

    member this.WebSharper4 =
        WebSharper4Projects(this)

    member this.FSharp =
        FSharpProjects(this)

    member this.MSBuild(file) =
        log "PROJECT" "%s (IGNORING -- MIGHT HAVE TO CONVERT BY HAND)" file
        {
            tool = this
            file = file
        } : MSBuildProject

    member this.Solution (projects: seq<IProject>) : Solution =
        let s : Solution = { projects = List.ofSeq projects }
        log "DEPS" "Computed build-time dependencies:"
        for dep in PaketDependency.Check s.BuildTimeDependencies do
            match dep with
            | PaketDependency.NuGet(name, ver) ->
                log "DEPS" "  nuget %s %s" name ver.Pretty
            | PaketDependency.FrameworkAssembly asm ->
                log "DEPS" "  framework %s" asm
        for p in projects do
            match p with
            | :? NuGetPackage as p ->
                log "DEPS" "Computed NuGet dependencies for output NuGet package %s:" p.config.Id
                for dep in PaketDependency.Check p.PackageDependencies do
                    match dep with
                    | PaketDependency.NuGet(name, ver) ->
                        log "DEPS" "  nuget %s%s" name ver.Pretty
                    | PaketDependency.FrameworkAssembly asm ->
                        log "DEPS" "  framework %s" asm
            | _ -> ()
        s

    member this.NuGet = this

    member this.CreatePackage() : NuGetPackage =
        {
            tool = this
            config =
                {
                    Title = None
                    LicenseUrl = None
                    ProjectUrl = None
                    Description = ""
                    RequiresLicenseAcceptance = false
                    Authors = ["IntelliFactory"]
                    ExtraFiles = []
                    SystemReferences = []
                    Id = this.packageId
                }
            projects = []
            extraFiles = []
            packageRefs = []
        }

    member this.Dispatch (s: Solution) =
        printfn "------- Check the above information -------"
        printfn "--- ENTER to continue, CTRL+C to cancel ---"
        System.Console.ReadLine() |> ignore
        // Strap yourselves in, we're doing this!
        s.BuildTimeDependencies
        |> PaketDependency.Check
        |> List.choose (function
            | PaketDependency.NuGet (name, CurrentVersion) -> None
            | PaketDependency.NuGet (name, forceFound) ->
                let ver = "" // TODO
                sprintf "nuget %s %s" name ver |> Some
            | PaketDependency.FrameworkAssembly asm -> None
        )
        |> String.concat "\n"
        |> sprintf """version 5.108.0
source https://api.nuget.org/v3/index.json
source https://nuget.intellifactory.com/nuget username: "%%IF_USER%%" password: "%%IF_PASS%%"

%s

group build
    framework: net45
    source https://api.nuget.org/v3/index.json

    nuget FAKE
    nuget Paket.Core 5.108.0
    github intellifactory/websharper tools/WebSharper.Fake.fsx
"""
        |> writeTo "paket.dependencies"
        // |> appendLinesTo "paket.dependencies"
        s.Write()
        //exec @"tools\.paket\paket.exe" "install"

and FSharpProjects(tool: BuildTool) =
    let project isExe name : WebSharper4Project =
        tool.Log "F# Project %s" name
        {
            tool = tool
            typ = ProjectType.NotWebSharper
            name = name
            isExe = isExe
            references =
                tool.references @ [
                    Reference.NuGet NuGetReference.WebSharper
                    Reference.NuGet NuGetReference.WebSharperFSharp
                ]
            embed = []
            sources = []
            withSourceMap = false
            needsSystemWeb = false
        }
    member this.Library name = project false name
    member this.Executable name = project true name

and WebSharper4Projects(tool: BuildTool) =
    let project t isExe name : WebSharper4Project =
        tool.Log "WS Project (%A) %s" t name
        {
            tool = tool
            typ = t
            name = name
            isExe = isExe
            references =
                tool.references @ [
                    Reference.NuGet NuGetReference.WebSharper
                    Reference.NuGet NuGetReference.WebSharperFSharp
                ]
            embed = []
            sources = []
            withSourceMap = false
            needsSystemWeb = false
        }
    member this.Library name = project ProjectType.Library false name
    member this.Extension name = project ProjectType.Extension false name
    member this.HtmlWebsite name = project ProjectType.HtmlWebsite false name
    member this.SiteletWebsite name = project ProjectType.SiteletWebsite false name
    member this.BundleWebsite name = project ProjectType.BundleWebsite false name
    member this.Executable name = project ProjectType.SiteletWebsite true name

and MSBuildProject =
    {
        tool : BuildTool
        file : string
    }

    member this.Configuration(c) = this
    member this.GeneratedAssemblyFiles(a) = this

    interface IProject with
        member  this.BuildTimeDependencies = []
        member  this.PackageDependencies = []
        member  this.Write(_) = ()


and NuGetPackageConfig =
    {
        Authors : list<string>
        Description : string
        ExtraFiles : list<string * string>
        SystemReferences : list<string>
        Id : string
        LicenseUrl : option<string>
        ProjectUrl : option<string>
        Title : string option
        RequiresLicenseAcceptance : bool
    }

and NuGetPackage =
    {
        tool: BuildTool
        config: NuGetPackageConfig
        projects: list<WebSharper4Project>
        packageRefs: list<NuGetPackage>
        extraFiles: list<string * string>
    }
    member this.Configure f =
        { this with config = f this.config }

    member this.Add p =
        { this with projects = this.projects @ [p] }

    member this.AddFile(inp, out) =
        { this with extraFiles = (inp, out) :: this.extraFiles }

    member this.AddPackage(pkg: NuGetPackage) =
        { this with packageRefs = pkg :: this.packageRefs }

    member this.FileName =
        sprintf "nuget/%s.paket.template" this.tool.packageId

    member this.PackageDependencies =
        this.projects
        |> Seq.collect (fun p -> (p :> IProject).PackageDependencies)
        |> Seq.distinct
        |> List.ofSeq
        |> List.append (
            this.packageRefs
            |> List.map (fun p -> PaketDependency.NuGet(p.config.Id, CurrentVersion))
        )

    interface IProject with
        member this.Write(hasNet4Version) =
            mkdir "nuget"
            let prefix p = function
                | None -> ""
                | Some n -> p + " " + n
            let section title isMandatory = function
                | [] when isMandatory ->
                    fail "paket.template" "Missing mandatory section %s" title
                | [] -> ""
                | items ->
                    String.concat "\n    " (title :: items)
            let authors = section "authors" true this.config.Authors
            let owners = section "owners" false this.config.Authors
            let files =
                this.projects
                |> List.collect (fun p -> p.OutputFiles hasNet4Version)
                |> List.map (fun (fw, file) -> sprintf "../%s ==> lib/%s" file fw)
                |> List.append (
                    this.extraFiles
                    |> List.map (fun (inp, out) -> sprintf "../%s ==> %s" inp out)
                )
                |> section "files" false
            let deps =
                this.PackageDependencies
                |> List.choose (function
                    | PaketDependency.NuGet (name, ver) ->
                        Some <| sprintf "    %s %s" name ver.Paket
                    | PaketDependency.FrameworkAssembly _ -> None
                )
                |> section "dependencies" false
            let fwdeps =
                this.PackageDependencies
                |> List.choose (function
                    | PaketDependency.NuGet _ -> None
                    | PaketDependency.FrameworkAssembly asm ->
                        Some <| sprintf "    %s" asm
                )
                |> section "frameworkAssemblies" false
            [
                "type file"
                prefix "id" (Some this.tool.packageId)
                authors
                owners
                prefix "licenseUrl" this.config.LicenseUrl
                prefix "projectUrl" this.config.ProjectUrl
                section "description" true [this.config.Description]
                section "tags" false ["Web JavaScript F# C#"]
                files
                deps
                fwdeps
            ]
            |> String.concat "\n"
            |> writeTo this.FileName

        member this.BuildTimeDependencies =
            [] // TODO

        member this.PackageDependencies = this.PackageDependencies

let BuildTool() =
    {
        packageId = ""
        versionFrom = "NONE", None
        fsharpVersion = FSharp30
        framework = "NONE"
        references = []
    }
