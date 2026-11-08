# ArkPar

A modernized fork of MultiPar: a Windows desktop app that creates PAR2 recovery files for any files, and later verifies and repairs them. Photos are the main use.

## Language

**Engine**:
The upstream console program (`par2j64.exe`) that does the actual PAR2 work; ArkPar ships it unmodified and drives it.
_Avoid_: client, backend, par2j (in user-facing text)

**Recovery set**:
The group of PAR2 files that protects one fixed list of source files; it only knows the files that existed when it was created.
_Avoid_: PAR set, parity set, backup

**Recovery file**:
One `.par2` file belonging to a recovery set.
_Avoid_: parity file, volume

**Recovery block**:
One unit of repair data inside the recovery files. A repair uses one recovery block for each block of the protected files that was lost, so it is possible while the blocks lost do not outnumber the recovery blocks available.
_Avoid_: slice, parity block

**Creation mode**:
How a selection is split into recovery sets; the user picks one each time, with an explanation on screen. The modes are single set, per subfolder, per folder and per file.
_Avoid_: recursion, batch mode

**Single set**:
The creation mode that makes one recovery set covering everything selected, including all files in subfolders.
_Avoid_: normal mode

**Per subfolder**:
The creation mode that makes one recovery set for each direct subfolder of the selected folder, each covering everything inside it. Files loose in the selected folder itself get one more set there, covering only them.
_Avoid_: per-folder (ambiguous)

**Per folder**:
The creation mode that makes one recovery set in every folder that directly contains files, at any depth, each covering only its own files.
_Avoid_: recursive mode

**Per file**:
The creation mode that makes one recovery set for each selected file.
_Avoid_: individual mode

**Folder verification**:
Verifying every recovery set found inside a chosen folder, at any depth, one after another, then summing them up by folder: intact, with a problem the sets can repair, with a problem they cannot, or not verified (unreadable, or canceled first). A folder is as bad as its worst set.
_Avoid_: batch mode, recursive verify

**Stale set**:
A recovery set that no longer matches the files it should protect: files were added, changed or removed after it was created, or now belong to other sets. New and changed files are unprotected until the set is recreated; a removed file may still be recoverable from it, so recreating it then waits for the user.
_Avoid_: outdated, old set

**Orphaned set**:
A recovery set inside the folders chosen to create again that no set the create will make has the name of, and whose files are partly or all gone from the disk (a photo deleted in per file, a folder left empty in per folder). Creating never makes nor remakes it; the list before creating shows it as a warning to use verify and repair, since it may be the only way back to what was lost. A set of another mode whose files are all still there is not one. One that cannot be read, or whose folder cannot be read, is shown among them too, with the reason, since it may be one.
_Avoid_: leftover set, lost set

**Redundancy**:
The size of a recovery set relative to the files it protects, which is roughly how much damage it can repair (10% redundancy repairs up to about 10% of lost data).
_Avoid_: parity, recovery rate
