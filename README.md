# Ranvier.Persistence

Keyed persisted inputs for [Ranvier](https://github.com/ranvier-org/Ranvier) graphs: signals and keyed collections
restored from an `IStateStore` across a restart or a page reload, with in-memory, file, localStorage, PostgreSQL
(Marten) and KurrentDB stores.

[Repository](https://github.com/ranvier-org/Ranvier.Persistence) · [Documentation](https://ranvier-org.github.io/Ranvier.Persistence/)

The persistence projects, tests and checks were extracted from the Ranvier monorepo. The core packages are pinned,
so this repository builds independently; a sibling checkout is supported for coordinated development.

## The Ranvier family

- [Ranvier](https://github.com/ranvier-org/Ranvier): the reactive core, `Ranvier.Wire` and `Ranvier.Elmish`
  ([documentation](https://ranvier-org.github.io/Ranvier/)).
- [Ranvier.Views](https://github.com/ranvier-org/Ranvier.Views): the view layer.
- [Ranvier.Css](https://github.com/ranvier-org/Ranvier.Css): typed CSS and generated bindings.

## Packages

| Package | Targets | Contents |
| --- | --- | --- |
| `Ranvier.Persistence` | net10.0, netstandard2.1, Fable | Keys, codecs, persisted signals and collections, memory, file and localStorage stores |
| `Ranvier.Persistence.Marten` | net10.0 | Marten / PostgreSQL store with scoped records and optional optimistic concurrency |
| `Ranvier.Persistence.KurrentDB` | net10.0 | KurrentDB snapshot store with atomic batches and optional optimistic concurrency |

Each package is also published as `<id>.Traced`, built with the trace log and depending on the traced core
packages. `samples/Ranvier.Persistence.Equinox` implements `IStateStore` with Equinox and FsCodec.

See the [library guide](src/Ranvier.Persistence/README.md) and the [contributor guide](CONTRIBUTOR.md).

## Setup and checks

Install .NET 10 and Node.js 22 or later. All workflows run through Partas.Build:

```powershell
rtk dotnet fsi build.fsx -- dev setup
rtk dotnet fsi build.fsx -- ci
```

`ci` restores dependencies, checks formatting, builds, runs the checks that need no database and packs the
untraced and traced packages. It uploads nothing.

Individual CI shards:

```powershell
rtk dotnet fsi build.fsx -- test --group dotnet     # the persistence suite, untraced and traced
rtk dotnet fsi build.fsx -- test --group fable      # the suite compiled with fickl, run under Mocha
rtk dotnet fsi build.fsx -- test --group aot        # NativeAOT publish and smoke run, untraced and traced
rtk dotnet fsi build.fsx -- test --group services   # Marten, KurrentDB and Equinox suites (need databases)
```

Bare `test` runs `dotnet`, `fable` and `aot`. The database groups (`marten`, `kurrentdb`, `equinox`, or all three
as `services`) need a connection variable each; [CONTRIBUTOR.md](CONTRIBUTOR.md#database-suites) lists the
containers. Other commands are `build`, `format`, `format --dry-format`, `pack`, `docs`, `bump` and `shipit`.
`publish --dry-publish` validates and packs without uploading. Run `--help` for details.

## Working across repositories

```powershell
rtk dotnet fsi build.fsx -- dev local
rtk dotnet fsi build.fsx -- test
```

`dev local` uses `../Ranvier`, cloning it only if the directory is missing, and writes the ignored
`Local.Build.props`. Supply `--ranvier-root <path>` for another location. `dev packages` switches back to the
pinned NuGet dependencies. For a one-off build, pass `-p:UseLocalRanvier=true -p:RanvierRoot=<path>`.

## Releases

Each package has a ShipIt changelog with a project version updater. The adapters depend on the
`Ranvier.Persistence` version built beside them. Configure trusted NuGet publishing and GitHub permissions as
described in [CONTRIBUTOR.md](CONTRIBUTOR.md#maintainer-setup).
