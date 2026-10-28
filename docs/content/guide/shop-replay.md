---
title: Replay a production shop bug
description: Capture an async shipping race in an untraced production build, then reproduce it offline in a traced development build.
order: 30
---

An older shipping response arrives after the shopper changes their cart. It replaces the newer quote,
raising the total from **$67.50 to $76.50**. Capture the incident in an untraced Release build, then
reproduce it in a traced Debug build with **zero live service calls**.

The runnable example lives in
[Ranvier.Examples](https://github.com/shayanhabibi/Ranvier.Examples/tree/1e26a146650c0b9b05805b5e6a9d2d564cc01e61/src/Ranvier.Examples.ShopReplay).
Its [walkthrough](https://github.com/shayanhabibi/Ranvier.Examples/blob/1e26a146650c0b9b05805b5e6a9d2d564cc01e61/docs/workflows/shop-replay.md)
covers the saved files, async gateway and verification checks.

![An untraced shop saves a checkpoint and input journal; traced replay reproduces the receipt and explains its cause.](/Ranvier.Persistence/diagrams/shop-replay.svg)

## Run the incident

Install the .NET 10 SDK. Check out the example revision and its pinned Ranvier source:

```shell
git clone https://github.com/shayanhabibi/Ranvier.Examples.git
cd Ranvier.Examples
git checkout 1e26a146650c0b9b05805b5e6a9d2d564cc01e61
git submodule update --init --recursive
```

Choose a capture directory that does not exist, then run the two builds:

```shell
dotnet run --project src/Ranvier.Examples.ShopReplay -c Release -p:RanvierTrace=false -- capture tmp/shop-incident
dotnet run --project src/Ranvier.Examples.ShopReplay -c Debug -p:RanvierTrace=true -- replay tmp/shop-incident
```

The shop prices tea at $25, applies `SAVE10`, checks stock and offers free shipping above $50.
Two requests overlap:

1. One tea starts a shipping request. The source is pending.
2. The shopper changes to three teas while that request is still pending.
3. The newer quote returns free shipping: $75 minus $7.50 is **$67.50**.
4. The older quote returns $9 shipping. The buggy gateway accepts it, producing **$76.50**.

The gateway reads local shipping rules asynchronously and uses completion gates to arrange this order
reliably. This is an application race demonstrated in a production build; both builds run the same shop code.

## What makes replay deterministic

Three APIs cooperate:

- [Persistence](persistence.md) saves the cart and the checkpoint's journal position.
- The [input journal](https://ranvier-org.github.io/Ranvier/guide/journal/) records cart edits, async results or failures, and flush boundaries.
- [Tracing](https://ranvier-org.github.io/Ranvier/guide/tracing/) explains the computations in the development replay.

The checkpoint precedes both requests. Restoring it creates the same pending source. Replaying the cart
edits reconstructs the request sequence, and the journal supplies the recorded outcomes in their original
order. The gateway captures `Journal.work` before checking `Journal.replaying`, then skips external calls
in diagnostic mode. `Persist.replay ReplayMode.Diagnostic` applies the recorded tail after the checkpoint.

Pending and returned values are enough for this incident when the triggering edits and completion order
also match. Replay preserves the observable sequence, rather than waiting for the original network delays.
Applications that depend on a clock or randomness must expose and record those inputs too.

## Read the cause

Both processes print the same five checkout observations, from the restored pending cart through the
two completions. Replay checks each observation against the production capture and writes:

- `replay-why.txt`: the receipt's last run traced back to the stale shipping result.
- `replay-snapshot.txt`: the labelled pricing and checkout graph.
- `replay-trace.jsonl`: the development trace.

The explanation ends at the older quote's settlement:

```text
why /receipt run 5
  ... Mark /receipt <- /checkout
  ... Mark /checkout <- /shipping
  ... Settle /shipping = { Revision = 1; Shipping = 900; Stock = 8 }
```

The defect is the unconditional acceptance of a response for an earlier cart revision. Keep that deliberate
defect while following the example; correcting it should make the recorded incident assertion fail.

## Try a failure and verify repeatability

```shell
dotnet run --project src/Ranvier.Examples.ShopReplay -c Release -p:RanvierTrace=false -- capture-failure tmp/shop-failure
dotnet run --project src/Ranvier.Examples.ShopReplay -c Debug -p:RanvierTrace=true -- replay tmp/shop-failure
python tools/verify-shop-replay.py
```

The failure scenario reproduces an older request failing after the newer quote succeeds. The verifier
launches three fresh replay processes and checks their traces byte for byte, as well as failure replay,
checkpoint immutability and rejection of damaged recordings or incompatible codecs.

The sample copies an acknowledged, quiescent checkpoint before async work starts. It keeps stable input
keys, compatible codecs and matching graph construction. Read the [restore limits](journal-restore.md)
before adapting it to checkpoints containing in-flight work or distributed storage.
