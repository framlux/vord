// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package hwid classifies hardware identifiers reported by firmware.
package hwid

import "strings"

// knownPlaceholders holds values that firmware writes into DMI fields nobody filled in, in
// normalised form: lowercase, with spaces, '.', '-', '_' and '/' removed.
//
// The list leans aggressive on purpose and should not be trimmed. A false positive only makes the
// agent fall back to the next identifier source, and the server only drops the serial or asset-tag
// clause from its duplicate check (the machine-id still catches a true re-registration), whereas
// a false negative lets one shared filler value block every later machine from registering.
//
// The server carries the same rules in HardwareIdentifierPlaceholders.cs; keep the two in step.
var knownPlaceholders = map[string]struct{}{
	"tobefilledbyoem":       {},
	"notspecified":          {},
	"notapplicable":         {},
	"notavailable":          {},
	"na":                    {},
	"none":                  {},
	"null":                  {},
	"unknown":               {},
	"invalid":               {},
	"undefined":             {},
	"empty":                 {},
	"defaultstring":         {},
	"default":               {},
	"oem":                   {},
	"systemserialnumber":    {},
	"chassisserialnumber":   {},
	"baseboardserialnumber": {},
	"serialnumber":          {},
	"noassettag":            {},
	"noassetinformation":    {},
	"assettag":              {},
	"asset1234567890":       {},
	"0123456789":            {},
	"123456789":             {},
	"1234567890":            {},
	"12345678":              {},
}

// IsPlaceholder reports whether s is firmware filler rather than something that identifies a
// machine: blank, a known filler string, or a degenerate pattern such as all zeros. Matching is
// exact on the normalised value, never by substring, because a real serial may contain a word
// such as "serial".
func IsPlaceholder(s string) bool {
	if strings.TrimSpace(s) == "" {
		return true
	}

	normalized := normalize(s)
	if normalized == "" {
		return true
	}
	if isSingleRepeatedCharacter(normalized) {
		return true
	}

	_, found := knownPlaceholders[normalized]

	return found
}

// normalize lowercases s and removes the separators firmware vendors scatter through filler text.
func normalize(s string) string {
	return strings.Map(func(r rune) rune {
		switch r {
		case ' ', '.', '-', '_', '/':
			return -1
		}

		return r
	}, strings.ToLower(s))
}

// isSingleRepeatedCharacter reports whether every character of s is the same, such as "00000000".
func isSingleRepeatedCharacter(s string) bool {
	var first rune
	for i, r := range s {
		if i == 0 {
			first = r

			continue
		}
		if r != first {
			return false
		}
	}

	return true
}
