#i "nuget: https://nuget.cloudsmith.io/shayanhabibi/shayanhabibi-partas-build/v3/index.json"
#r "nuget: Partas.Build, 0.8.2"
#r "nuget: Partas.Build.Baked, 0.3.0"
#r "nuget: Partas.Build.EasyBuild.ShipIt, 0.1.0"
#r "nuget: Fake.IO.FileSystem"

open System
open System.IO
open System.Runtime.InteropServices
open Partas.Build
open Fake.IO.Globbing.Operators

module Spec =
    let root = __SOURCE_DIRECTORY__
    let solution = "Ranvier.Persistence.slnx"
    let library = "src/Ranvier.Persistence/Ranvier.Persistence.fsproj"
    let adapters = [
        "adapters/Ranvier.Persistence.Marten/Ranvier.Persistence.Marten.fsproj"
        "adapters/Ranvier.Persistence.KurrentDB/Ranvier.Persistence.KurrentDB.fsproj"
    ]
    /// The packed projects: the library first, then the adapters that reference it.
    let packages = library :: adapters
    let sample = "samples/Ranvier.Persistence.Equinox/Ranvier.Persistence.Equinox.fsproj"
    let unitTests = "tests/Ranvier.Persistence.Tests/Ranvier.Persistence.Tests.fsproj"
    let aotSmoke = "tests/Ranvier.Persistence.AotSmoke/Ranvier.Persistence.AotSmoke.fsproj"
    let fableTests = "fable/Ranvier.Persistence.Tests.Fable"
    /// Each service-backed suite: its name, its test project, and the environment variable holding its connection.
    let services = [
        "marten", "tests/Ranvier.Persistence.Marten.Tests", "RANVIER_MARTEN_CONNECTION"
        "kurrentdb", "tests/Ranvier.Persistence.KurrentDB.Tests", "RANVIER_KURRENTDB_CONNECTION"
        "equinox", "tests/Ranvier.Persistence.Equinox.Tests", "RANVIER_EQUINOX_CONNECTION"
    ]
    /// The core packages a persistence package may depend on, checked for matching traced and untraced ids.
    let corePackages = [ "Ranvier"; "Ranvier.Wire"; "Ranvier.Elmish" ]
    let localTools =
        use manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".config/dotnet-tools.json")))
        manifest.RootElement.GetProperty("tools").EnumerateObject()
        |> Seq.map (fun tool -> tool.Name, tool.Value.GetRawText())
        |> Seq.toList
    let formatting =
        !! "**/*.fs"
        -- "**/obj/**"
        -- "**/bin/**"
        -- "**/out/**"
        -- "**/output/**"
        -- "**/dist/**"
        -- "**/node_modules/**"
        -- "**/fable_modules/**"
        |> Seq.toList
    let rid = RuntimeInformation.RuntimeIdentifier
    let executable name =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then name + ".exe" else name

module Options =
    let group =
        Input.option "--group"
        |> Input.mapFromAmong [
            "all", "all"
            "dotnet", "dotnet"
            "fable", "fable"
            "aot", "aot"
            "services", "services"
            "marten", "marten"
            "kurrentdb", "kurrentdb"
            "equinox", "equinox"
        ]
        |> Input.def "all"
        |> Input.description "Checks to run: all (dotnet, fable, aot), a single group, or a database service (services runs all three; default: all)"
    let dryFormat = Input.option<bool> "--dry-format" |> Input.description "Check formatting without rewriting files"
    let dryPublish = Input.option<bool> "--dry-publish" |> Input.description "Build and pack without uploading packages"
    let ranvierRoot =
        Input.option<string> "--ranvier-root"
        |> Input.def (Path.GetFullPath(Path.Combine(Spec.root, "../Ranvier")))
        |> Input.description "Path to the sibling Ranvier repository"

module Stage =
    let restoreTools = stage "restore tools in isolated SDK processes" {
        for name, registration in Spec.localTools do
            stage $"restore {name}" {
                run (fun _ ->
                    let directory = Path.Combine(Spec.root, ".config/.tool-restore")
                    Directory.CreateDirectory directory |> ignore
                    let json = "{\"version\":1,\"isRoot\":true,\"tools\":{" + System.Text.Json.JsonSerializer.Serialize(name) + ":" + registration + "}}"
                    File.WriteAllText(Path.Combine(directory, name + ".json"), json))
                run (Cmd.ofList "dotnet" [ "tool"; "restore"; "--tool-manifest"; Path.Combine(Spec.root, ".config/.tool-restore", name + ".json"); "--verbosity"; "q" ])
            }
    }
    let restore = input {
        let! quick = Baked.Common.quick
        and! config = Baked.Dotnet.configOrRelease
        return stage "restore" {
            when' (not quick)
            restoreTools
            stage "restore solution" {
                run (cmd $"dotnet restore {Spec.solution} -p:Configuration={config}")
            }
        }
    }

    let npm = input {
        let! quick = Baked.Common.quick
        return stage "install pinned npm dependencies" {
            when' (not quick)
            run "npm ci --ignore-scripts"
        }
    }

    let build = input {
        let! config = Baked.Dotnet.configOrRelease
        return stage "build" {
            for project in Spec.packages @ [ Spec.sample; Spec.unitTests ] @ (Spec.services |> List.map (fun (_, path, _) -> path)) do
                stage $"build {project}" {
                    run (cmd $"dotnet build {project} -c {config} -m:1 --no-restore")
                }
        }
    }

    let formatWith (check: bool) = stage "format" {
        run (cmd $"dotnet fantomas" |> Cmd.argIf check [ "--check" ] |> Cmd.args Spec.formatting)
    }
    let format = input {
        let! check = Options.dryFormat
        return formatWith check
    }

    let scripts = stage "test workflow scripts" {
        run "node --test .github/scripts/contrib.test.cjs .github/scripts/packages.test.cjs"
    }
    let dotnet = stage "test the persistence suite, untraced and traced" {
        run (cmd $"dotnet test {Spec.unitTests} -c Release -m:1")
        run (cmd $"dotnet test {Spec.unitTests} -c Release -m:1 -p:RanvierTrace=true")
    }
    let fable = input {
        let! clean = Baked.Stages.cleanWith (InputSpec.ret false) [] [ "src/**/obj/**/*.nuspec" ]
        return stage "compile the suite with fickl and run it under Mocha" {
            clean
            stage "compile" { run (cmd $"dotnet fickl {Spec.fableTests} -e .fs.js -o dist/tests -c Release") }
            // RanvierTrace compiles the trace log into the Release build and selects the traced core packages.
            stage "compile traced" {
                envVars [ ("RanvierTrace", "true") ]
                run (cmd $"dotnet fickl {Spec.fableTests} -e .fs.js -o dist/tests-traced -c Release")
            }
            // Each build runs with a TaskCompletionSource that settles inline, as on .NET, and with promise delivery.
            for build in [ "tests"; "tests-traced" ] do
                for delivery in [ "inline"; "promise" ] do
                    stage $"mocha {build} ({delivery} delivery)" {
                        envVars [ ("RANVIER_FABLE_DELIVERY", delivery) ]
                        run (Cmd.ofList "node" [ "node_modules/mocha/bin/mocha.js"; $"dist/{build}/Main.fs.js"; "--timeout"; "10000" ])
                    }
        }
    }
    let aot = stage "publish and run the NativeAOT smoke test, untraced and traced" {
        for traced in [ "false"; "true" ] do
            stage $"trace={traced}" {
                run (cmd $"dotnet publish {Spec.aotSmoke} -c Release -r {Spec.rid} -p:RanvierTrace={traced} -o artifacts/aot-{traced}")
                run (Path.Combine(Spec.root, $"artifacts/aot-{traced}", Spec.executable "Ranvier.Persistence.AotSmoke"))
            }
    }
    let service (name: string) (project: string) (variable: string) = stage $"{name} integration suite" {
        stage $"require {variable}" {
            run (fun _ ->
                if String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable variable) then
                    failwith $"Set {variable} to run the {name} suite; CONTRIBUTOR.md lists the containers.")
        }
        run (cmd $"dotnet test {project} -c Release -m:1")
        run (cmd $"dotnet test {project} -c Release -m:1 -p:RanvierTrace=true")
        stage "persisted signal round trips" {
            when' (name = "equinox")
            run (cmd $"dotnet run --project {Spec.sample} -c Release -- ci-round-trip")
            run (cmd $"dotnet run --project {Spec.sample} -c Release -p:RanvierTrace=true -- ci-traced-round-trip")
        }
    }

    let testsWith (group: InputSpec<string>) = input {
        let! group = group
        and! skip = Baked.Common.skipTests
        and! npm = npm
        and! fable = fable
        return stage "persistence checks" {
            when' (not skip)
            scripts
            stage "dotnet" {
                when' (group = "all" || group = "dotnet")
                dotnet
            }
            stage "fable" {
                when' (group = "all" || group = "fable")
                npm
                fable
            }
            stage "aot" {
                when' (group = "all" || group = "aot")
                aot
            }
            for name, project, variable in Spec.services do
                stage name {
                    when' (group = "services" || group = name)
                    service name project variable
                }
        }
    }
    let tests = testsWith (InputSpec.ofInput Options.group)
    let allTests = testsWith (InputSpec.ret "all")

    let pack = stage "pack untraced and traced packages" {
        run (fun _ ->
            let bin = Path.Combine(Spec.root, "bin")
            if Directory.Exists bin then
                for file in Directory.GetFiles(bin, "*.nupkg") do
                    File.Delete file)
        for project in Spec.packages do
            for traced in [ "false"; "true" ] do
                stage $"pack {project} (trace={traced})" {
                    run (cmd $"dotnet pack {project} -c Release -p:RanvierTrace={traced} -o bin")
                }
        stage "check package ids, versions, frameworks, trace builds and dependencies" {
            run (
                Cmd.ofList "node" ([ ".github/scripts/packages.cjs"; "bin"; "src"; "--libraries" ] @ Spec.adapters @ [ "--external" ] @ Spec.corePackages)
            )
        }
    }

    let publish = input {
        let! key = Baked.NuGet.apiKey.option
        and! dry = Options.dryPublish
        return stage "publish" {
            when' (not dry)
            stage "push (requires NUGET_API_KEY or --nuget-key)" {
                failIfIgnored
                when' key.IsSome
                run (cmd $"dotnet nuget push bin/*.nupkg --source https://api.nuget.org/v3/index.json --skip-duplicate"
                     |> Cmd.secretOption "--api-key" (Option.defaultValue "" key))
            }
        }
    }

    let local = input {
        let! root = Options.ranvierRoot
        let root = Path.GetFullPath(root, Spec.root)
        return stage "configure sibling Ranvier checkout" {
            stage "clone if missing" {
                when' (not (Directory.Exists root))
                run (cmd $"git clone https://github.com/ranvier-org/Ranvier.git {root}")
            }
            stage "write local configuration" {
                run (fun _ ->
                    if not (File.Exists(Path.Combine(root, "src/Ranvier/Ranvier.fsproj"))) then
                        invalidArg "ranvier-root" "The path is not a Ranvier checkout."
                    let escaped = System.Security.SecurityElement.Escape root
                    File.WriteAllText(Path.Combine(Spec.root, "Local.Build.props"), $"<Project><PropertyGroup><UseLocalRanvier>true</UseLocalRanvier><RanvierRoot>{escaped}</RanvierRoot></PropertyGroup></Project>"))
            }
        }
    }
    let packages = stage "configure published package dependencies" {
        run (fun _ -> File.WriteAllText(Path.Combine(Spec.root, "Local.Build.props"), "<Project><PropertyGroup><UseLocalRanvier>false</UseLocalRanvier></PropertyGroup></Project>"))
    }
    let docs = input {
        let! watch = Baked.Common.watch
        return stage "documentation" {
            run (cmd $"dotnet run --project docs/docs.fsproj --" |> Cmd.arg (if watch then "watch" else "build"))
        }
    }

exit <| rootCommand fsi.CommandLineArgs[1..] {
    description "Ranvier.Persistence development, validation and releases"
    workingDir Spec.root
    command "tools" {
        description "Restore pinned tools individually to avoid SDK tool-metadata collisions on fresh runners"
        Command.pipeline { Stage.restoreTools }
    }
    command "dev" {
        description "Set up dependencies and switch between published and sibling Ranvier references"
        command "setup" {
            description "Restore .NET tools, projects and pinned npm inputs"
            Command.pipeline { Stage.restore; Stage.npm }
        }
        command "local" {
            description "Clone Ranvier if missing and configure local project references"
            Command.pipeline { Stage.local; Stage.restore }
        }
        command "packages" {
            description "Switch back to pinned published Ranvier dependencies"
            Command.pipeline { Stage.packages; Stage.restore }
        }
    }
    command "build" {
        description "Build the libraries, adapters, sample and test projects"
        Command.pipeline { Stage.restore; Stage.build }
    }
    command "test" {
        alias "tests"
        description "Run persistence checks; --group selects a CI shard or a database service (default: all)"
        Command.pipeline { Stage.restore; Stage.build; Stage.tests }
    }
    command "ci" {
        description "Run the CI gates that need no database locally and pack the validated libraries"
        Command.pipeline { Stage.restore; Stage.npm; Stage.formatWith true; Stage.build; Stage.allTests; Stage.pack }
    }
    command "format" {
        description "Format source files (--dry-format to check)"
        Command.pipeline { Stage.restore; Stage.format }
    }
    command "pack" {
        description "Pack the library and adapters, untraced and traced, and check the packages"
        Command.pipeline { Stage.restore; Stage.pack }
    }
    command "publish" {
        description "Validate the gates that need no database and publish (--dry-publish to skip upload)"
        Command.pipeline { Stage.restore; Stage.npm; Stage.build; Stage.allTests; Stage.pack; Stage.publish }
    }
    command "docs" {
        description "Build documentation (--watch to serve it)"
        Command.pipeline { Stage.restore; Stage.docs }
    }
    command "bump" {
        description "Calculate release versions with ShipIt in local mode"
        Command.pipeline { Partas.Build.EasyBuild.ShipIt.Stages.bump }
    }
    addCommand Partas.Build.EasyBuild.ShipIt.Commands.shipit
}
