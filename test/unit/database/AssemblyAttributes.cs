// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using TUnit.Core;

// Prevent any single test from hanging the entire suite.
// Individual tests can override with a longer [Timeout] if needed.
[assembly: Timeout(120_000)]
