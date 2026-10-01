# ForkSave (experimental)

Factorio-style non-blocking autosaves for RimWorld's native Linux build. On autosave the game
`fork()`s; the child writes the save from its copy-on-write snapshot and `_exit`s, while the
parent keeps playing and reaps the child from `Root.Update`.

Mono/Unity don't support running managed code after `fork()` (only the forking thread survives),
so this can hang or crash the child. The parent guards against that:

- A child that fails, or runs past the timeout (setting, default 120 s), is killed and a normal
  blocking autosave runs instead. `SafeSaver` writes `*.new` and renames, so a killed child
  leaves the previous save intact.
- The fork happens in a small native helper (`Source/Native/forksave.c` → `libforksave.so`)
  that holds the Boehm GC's allocation lock across `fork()` via the GC's `GC_atfork_*` hooks.
  Without it, another thread can hold that lock at fork time and the child deadlocks on its
  first allocation. This can't be done from C#: no managed code may run while the lock is held.
- An autosave that comes due while an incremental GC is running is postponed 250 ticks (up to 5
  times) through the vanilla autosave timer: the lock hook would otherwise finish that collection
  synchronously, stalling the game for up to seconds. Retrying through the timer keeps every fork
  in the "Autosaving" long event; forking from `Root.Update` right after a GC hung the child.
- The GC is disabled in the child right after the fork, so it never runs a stop-the-world
  collection. (Not before: the lock hook finishes an in-progress incremental collection first.)
- `Verse.Log` calls in the child are captured to `ForkSave-child.log` in the save-data folder
  and replayed into the game log with a `[ForkSave child]` prefix.
- The first autosave of each session runs blocking, as a baseline and to JIT the save path
  and the child-only code.
- Commitment mode and non-Linux platforms keep the vanilla autosave.
- With RimWorld's "Run in background" off, Unity stops updating while the window is unfocused,
  so a hung child is only killed once you return to the game.

## Build

```bash
dotnet build -c Release Source/ForkSave
```

Needs the .NET SDK (verified with 8.0: `sudo pacman -S dotnet-sdk`, or the upstream
[`dotnet-install`](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script) script) and
`gcc`. Output (`ForkSave.dll`, `libforksave.so`) goes to `1.6/Assemblies/`.

The project probes the usual Steam library locations (`~/.local/share/Steam`, `~/.steam/steam`, the
Flatpak install, and `/mnt/games/SteamLibrary` as a second drive) and picks the first one holding the
native Linux build. For any other location pass
`-p:RimWorldManaged=<RimWorld install>/RimWorldLinux_Data/Managed`; if the folder is missing or
wrong, the build fails with that hint instead of a wall of missing-type errors.

The build is deterministic: with the same SDK, rebuilding the current sources reproduces the
binaries in `1.6/Assemblies/` byte-for-byte.

## Log lines

| Line | Meaning |
|---|---|
| `Blocking autosave '…' (first autosave this session) took N ms` | Baseline |
| `Forked child <pid> to autosave '…'; fork() blocked the game for N ms` | The freeze you still get |
| `Background autosave '…' finished in N s` | Success |
| `Postponed autosave 250 ticks for a running GC (attempt k).` | Autosave retried later to avoid a GC stall |
| `GC still running after 5 postpones; forking anyway.` | Fork may stall while it finishes the GC |
| `Background autosave '…' failed` + `Blocking autosave … (fallback)` | Child failed, vanilla save ran |
| `Child … exceeded the … timeout and was killed` | Child hung (lock/GPU/mod), vanilla save ran |
| `fork() failed (errno N)` or `Could not fork: …` | Vanilla autosave ran |
| `Native fork helper loaded.` | At startup; background autosaves active |
| `Native fork helper unavailable (…)` | At startup; all autosaves stay vanilla |
