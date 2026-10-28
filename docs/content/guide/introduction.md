---
title: Introduction
order: 1
---

## Installation

```bash frame="terminal"
dotnet add package Ranvier.Persistence --version 0.1.0-preview.1
```

`Ranvier.Persistence` targets .NET 10 and .NET Standard 2.1, and ships its sources for Fable. It depends on the
core [Ranvier](https://github.com/ranvier-org/Ranvier) and `Ranvier.Wire` packages.

The database adapters target .NET 10:

- `Ranvier.Persistence.Marten` stores records in PostgreSQL through Marten.
- `Ranvier.Persistence.KurrentDB` stores a scope as a KurrentDB snapshot stream.

A traced build of each package is published under a `.Traced` package id, matching the core `Ranvier.Traced`
package. These are preview versions; the API and the stored record format can change before 1.0.

See [Persisted inputs](persistence.md) for the API.
