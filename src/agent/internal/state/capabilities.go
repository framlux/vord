// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package state

// Agent capability flags, sent to the server during configuration requests.
// Each capability is a single bit in the capabilities bitmask.
const (
	// CapabilityRemoteCommands indicates the agent accepts remote command execution.
	CapabilityRemoteCommands uint64 = 1 << iota
)
