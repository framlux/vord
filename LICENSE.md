# License

Copyright (c) 2026 Framlux LLC

This repository uses a dual-license structure. Different components are licensed
under different terms as described below.

## Open Source (MIT License)

The following components are licensed under the [MIT License](https://opensource.org/licenses/MIT):

- **`src/agent/`** - Fleet agent (Go)
- **`src/grpc/protos/`** - Protobuf service definitions

See the `LICENSE` file in each directory for the full license text.

## Functional Source License (FSL-1.1-ALv2)

All other source code is licensed under the
[Functional Source License, Version 1.1, ALv2 Future License](https://fsl.software/FSL-1.1-ALv2.template.md).
This license converts to Apache License, Version 2.0 two years after each
version is made available.

- **`src/server/`** - API server and gRPC control plane
- **`src/database/`** - Database models and migrations
- **`src/migrationRunner/`** - Migration runner
- **`src/web/`** - Web UI
- **`test/`** - Tests

See the `LICENSE` file in each directory for the full license text.
