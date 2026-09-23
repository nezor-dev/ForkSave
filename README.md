# ForkSave (experimental)

Factorio-style non-blocking autosaves for RimWorld's native Linux build. On autosave the game
`fork()`s; the child writes the save from its copy-on-write snapshot and `_exit`s, while the
parent keeps playing and reaps the child from `Root.Update`.

Mono/Unity don't support running managed code after `fork()` (only the forking thread survives),
so this can hang or crash the child. The parent guards against that:

- A child that fails, or runs past the timeout (setting, default 120 s), is killed and a normal
  blocking autosave runs instead. `SafeSaver` writes `*.new` and renames, so a killed child
  leaves the previous save intact.
- GC is disabled across the fork, so the child never runs a stop-the-world collection.
- `Verse.Log` calls in the child are captured to `ForkSave-child.log` in the save-data folder
  and replayed into the game log with a `[ForkSave child]` prefix.
- The first autosave of each session runs blocking, as a baseline and to JIT the save path
  and the child-only code.
- Commitment mode and non-Linux platforms keep the vanilla autosave.

## Build

```bash
dotnet build -c Release Source/ForkSave
```

Output goes to `1.6/Assemblies/`. Override the game path with `-p:RimWorldManaged=<.../RimWorldLinux_Data/Managed>`.

## Log lines

| Line | Meaning |
|---|---|
| `Blocking autosave '…' (first autosave this session) took N ms` | Baseline |
| `Forked child <pid> to autosave '…'; fork() blocked the game for N ms` | The freeze you still get |
| `Background autosave '…' finished in N s` | Success |
| `Background autosave '…' failed` + `Blocking autosave … (fallback)` | Child failed, vanilla save ran |
| `Child … exceeded the … timeout and was killed` | Child hung (lock/GPU/mod), vanilla save ran |
| `fork() failed (errno N)` or `Could not fork: …` | Vanilla autosave ran |
