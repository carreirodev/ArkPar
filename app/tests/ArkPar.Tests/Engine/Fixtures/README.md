# Captured engine output

Real output of `par2j64.exe` 1.3.3.7, saved byte for byte (CRLF line ends, bare CR between progress updates), so the parsers are tested against what the engine really prints. `.gitattributes` keeps git from normalizing them.

Captured on 2026-11-07 on a virtual drive `P:` (so no personal path is stored), with three 2 MB random "photos": `praia 01.jpg`, `aniversário.jpg`, `viagem/ônibus.jpg`.

| File | Command | Exit code |
|---|---|---|
| `create.txt` | `c /uo /lc256 /rr50 /d"P:\Fotos de família" /fu P:\...\fotos.par2 P:\list.txt` | 0 |
| `list.txt` | `l /uo fotos.par2` | 0 |
| `verify-complete.txt`, `.json` | `v /uo /lc256 /vdP:\json /w fotos.par2` | 0 |
| `verify-damaged.txt`, `.json` | same, after changing 4 bytes of `praia 01.jpg`, renaming `aniversário.jpg` to `aniversario renomeado.jpg` and deleting `viagem/ônibus.jpg` | 164 |
| `repair.txt`, `.json` | `r` with the same options, on that damage | 16 |
| `verify-impossible.txt`, `.json` | `v`, after deleting `praia 01.jpg` and `aniversário.jpg` (more than the 50% redundancy) | 12 |
| `verify-found.json` | `v /uo /vl2 ...`, on a new set, after replacing `praia 01.jpg` with a 1 MB partial copy `praia 01 - cópia.jpg` | 132 |
| `create-canceled.txt` | `c /uo /lc1 /rr100` on a 100 MB file, keys `p` then `c` sent through the hidden console | 2 |
