# Garnet.LightEpoch tests

Unit tests for `LightEpoch`: protection state transitions, the reclamation
frontier, drain-list semantics, per-instance isolation, entry-table slot handout,
and the user-word API.

```
dotnet test libs/storage/Tsavorite/cs/test/test.epoch/Garnet.LightEpoch.test.csproj
```

The hardware stress harness that reproduces the race these tests were written
around is a standalone tool in
[`playground/LightEpochLitmus`](../../../../../../playground/LightEpochLitmus).
