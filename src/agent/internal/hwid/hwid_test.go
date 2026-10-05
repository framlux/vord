// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package hwid

import "testing"

// Intent: empty and whitespace-only values carry no identity.
func TestIsPlaceholder_EmptyOrWhitespace(t *testing.T) {
	for _, s := range []string{"", " ", "   \t  ", "\n"} {
		if IsPlaceholder(s) == false {
			t.Errorf("IsPlaceholder(%q) = false, want true", s)
		}
	}
}

// Intent: every entry of the placeholder set, spelled the way firmware reports it, is filler.
func TestIsPlaceholder_KnownFirmwareFiller(t *testing.T) {
	values := []string{
		"To Be Filled By O.E.M.",
		"To Be Filled By O.E.M",
		"To be filled by O.E.M.",
		"Not Specified",
		"Not Applicable",
		"Not Available",
		"N/A",
		"NA",
		"None",
		"NULL",
		"Unknown",
		"Invalid",
		"Undefined",
		"Empty",
		"Default string",
		"Default String",
		"Default",
		"OEM",
		"O.E.M.",
		"System Serial Number",
		"Chassis Serial Number",
		"Base Board Serial Number",
		"Baseboard Serial Number",
		"Serial Number",
		"No Asset Tag",
		"No Asset Information",
		"Asset Tag",
		"Asset-1234567890",
		"0123456789",
		"123456789",
		"1234567890",
		"12345678",
	}
	for _, s := range values {
		if IsPlaceholder(s) == false {
			t.Errorf("IsPlaceholder(%q) = false, want true", s)
		}
	}
}

// Intent: case, spacing and punctuation differences must not let filler through.
func TestIsPlaceholder_CaseSpacingAndPunctuationVariants(t *testing.T) {
	values := []string{
		"system serial number",
		"SYSTEM SERIAL NUMBER",
		"System   Serial   Number",
		"  System Serial Number  ",
		"System-Serial-Number",
		"System_Serial_Number",
		"SystemSerialNumber",
		"System.Serial.Number",
		"System/Serial/Number",
		"n/a",
		"N.A.",
		"N-A",
		"TO BE FILLED BY OEM",
		"to-be-filled-by-o.e.m.",
		"DEFAULT STRING",
		"default_string",
		"asset-1234567890",
		"ASSET 1234567890",
		"0123-456-789",
	}
	for _, s := range values {
		if IsPlaceholder(s) == false {
			t.Errorf("IsPlaceholder(%q) = false, want true", s)
		}
	}
}

// Intent: a value that is one character repeated, or only separators, cannot identify a machine.
func TestIsPlaceholder_DegeneratePatterns(t *testing.T) {
	values := []string{
		"0",
		"00",
		"00000000",
		"0000000000000000",
		"FFFFFFFF",
		"ffffffff",
		"xxxxxxxx",
		"XXXXXXXXXXXX",
		"11111111",
		"0000-0000-0000",
		"00 00 00 00",
		"a",
		"...",
		"---",
		"_ _ _",
		"//",
	}
	for _, s := range values {
		if IsPlaceholder(s) == false {
			t.Errorf("IsPlaceholder(%q) = false, want true", s)
		}
	}
}

// Intent: genuine serials and the agent's own fallback serials must survive classification.
func TestIsPlaceholder_RealIdentifiers(t *testing.T) {
	values := []string{
		"C02XL0GTJGH5",
		"PF2ABCDE",
		"mid-0123abcd4567",
		"gen-6f1c2a90-3b7d-4e5f-8a1b-9c0d2e3f4a5b",
		"VMware-56 4d 12 34 56 78 9a bc-de f0 12 34 56 78 9a bc",
		"Serial123",
		"MXL1234ABC",
		"SN-TEST",
		"ASSET-00417",
		"20240115-0001",
	}
	for _, s := range values {
		if IsPlaceholder(s) {
			t.Errorf("IsPlaceholder(%q) = true, want false", s)
		}
	}
}

// Intent: matching is exact on the normalised value, so a real identifier that merely contains a
// filler word is kept.
func TestIsPlaceholder_ContainsFillerWordButIsMore(t *testing.T) {
	values := []string{
		"My Serial Number Is X1",
		"Serial Number 12345",
		"Defaulted-7731",
		"None-4471",
		"Unknown Board 9",
		"1234567891",
		"123456",
		"0123456780",
	}
	for _, s := range values {
		if IsPlaceholder(s) {
			t.Errorf("IsPlaceholder(%q) = true, want false", s)
		}
	}
}
