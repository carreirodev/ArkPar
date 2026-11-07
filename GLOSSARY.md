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
The creation mode that makes one recovery set for each direct subfolder of the selected folder, each covering everything inside it.
_Avoid_: per-folder (ambiguous)

**Per folder**:
The creation mode that makes one recovery set in every folder that directly contains files, at any depth, each covering only its own files.
_Avoid_: recursive mode

**Per file**:
The creation mode that makes one recovery set for each selected file.
_Avoid_: individual mode

**Stale set**:
A recovery set whose folder gained or changed files after the set was created, so those files are unprotected until the set is recreated.
_Avoid_: outdated, old set

**Redundancy**:
The size of a recovery set relative to the files it protects, which is roughly how much damage it can repair (10% redundancy repairs up to about 10% of lost data).
_Avoid_: parity, recovery rate
