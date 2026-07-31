# Garnet.LightEpoch tests

Unit tests for `LightEpoch`: protection state transitions, the reclamation
frontier, drain-list semantics, per-instance isolation, entry-table slot handout,
and the user-word API.

```
dotnet test libs/storage/Tsavorite/cs/test/epoch/Garnet.LightEpoch.test.csproj
```

`LitmusTests` also lives here, but it only wraps the standalone hardware stress
harness in [`playground/LightEpochLitmus`](../../../../../../playground/LightEpochLitmus),
which is where that story is documented. It is `[Explicit]`, so it is excluded from
the command above and has to be asked for:

```
dotnet test ... --filter "TestCategory=Litmus"
```
