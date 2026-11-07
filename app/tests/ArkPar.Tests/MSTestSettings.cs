// SPDX-License-Identifier: GPL-2.0-or-later

// Each test works in its own temporary folder, so tests can run in parallel.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
