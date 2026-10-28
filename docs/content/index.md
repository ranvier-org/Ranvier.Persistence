---
title: Ranvier.Persistence
description: Persisted Ranvier inputs restored from in-memory, file, localStorage, PostgreSQL and KurrentDB stores
layout: bare
---

# Ranvier.Persistence

Keyed persisted inputs for [Ranvier](https://ranvier-org.github.io/Ranvier/) graphs on .NET and Fable:
signals and keyed collections restored from a state store across a restart or a page reload.

Start with [installation](guide/introduction.md), then explore:

- [Persisted inputs](guide/persistence.md): keys, codecs, loading, saving and migrations.
- [Marten / PostgreSQL](guide/persistence-marten.md) and [KurrentDB snapshots](guide/persistence-kurrentdb.md).
- [The Equinox / FsCodec sample](guide/persistence-equinox.md).
- [Replaying a production shop bug](guide/shop-replay.md) and [restoring from a journal](guide/journal-restore.md).

The [repository](https://github.com/ranvier-org/Ranvier.Persistence) contains the libraries, the adapters,
the Equinox sample and their test suites.
