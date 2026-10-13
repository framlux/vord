// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package board recognises single-board computers from what the firmware and the kernel expose.
//
// A board is recognised from its device tree first, because the device tree is set by the board's
// own firmware and reads the same on every distribution, kernel and word size. A Raspberry Pi booted
// through UEFI has an SMBIOS table instead of a device tree, so the DMI vendor and product name come
// next. Last, where neither is visible, as inside a default Docker container, the Raspberry Pi
// kernels still print the board's model in /proc/cpuinfo.
//
// Only an allowlist of board families is recognised. A device tree is also how ARM laptops, phones
// and some NAS boxes boot, so "has a device tree" says nothing about being a single-board computer.
package board

import (
	"io"
	"os"
	"path/filepath"
	"strings"
	"unicode"
	"unicode/utf8"
)

// FamilyRaspberryPi is the family of every Raspberry Pi board, Compute Modules and the keyboard
// models included.
const FamilyRaspberryPi = "raspberrypi"

// maxModelBytes is the longest hardware model the server stores, in bytes. The server column holds
// 255 characters, so a value of at most 255 bytes always fits.
const maxModelBytes = 255

// maxFileBytes bounds how much of any one file is read, so a pathological file cannot cost memory.
const maxFileBytes = 1 << 20

// Paths are relative to the filesystem root. The device tree is read from sysfs and never from
// /proc/device-tree, which is an absolute symlink to the sysfs location and would escape a fixture
// root.
const (
	deviceTreeCompatible = "sys/firmware/devicetree/base/compatible"
	deviceTreeModel      = "sys/firmware/devicetree/base/model"
	cpuInfoPath          = "proc/cpuinfo"
	dmiSysVendor         = "sys/class/dmi/id/sys_vendor"
	dmiProductName       = "sys/class/dmi/id/product_name"
)

// family is one recognised board family and what identifies it in each signal. Adding a family
// whose vendor makes only single-board computers is one entry here, with a fixture for each signal
// it fills in. A field left empty means the family has no match for that signal. Every value is
// lower case, because the text it is compared with is lower-cased first.
type family struct {
	// name is the family reported in Info.Family.
	name string
	// deviceTreeVendor is the vendor prefix of a device-tree compatible entry, "raspberrypi" in
	// "raspberrypi,4-model-b".
	deviceTreeVendor string
	// modelPrefix is the start of the device-tree model as /proc/cpuinfo prints it, for the case where
	// the compatible list cannot be read.
	modelPrefix string
	// dmiText is a substring of the SMBIOS system vendor or product name. The Raspberry Pi UEFI
	// firmware publishes the vendor "Raspberry Pi Foundation" and a product name that starts with
	// "Raspberry Pi".
	dmiText string
}

var families = []family{
	{
		name:             FamilyRaspberryPi,
		deviceTreeVendor: "raspberrypi",
		modelPrefix:      "raspberry pi ",
		dmiText:          "raspberry pi",
	},
}

// Info describes the board the agent runs on.
type Info struct {
	// Family is the recognised board family, or "" when the host is not a recognised board.
	Family string
	// Model is the board's display name, for example "Raspberry Pi 4 Model B Rev 1.4". It is
	// reported for any board that names itself, recognised or not.
	Model string
	// Compatible is the device-tree compatible list, most specific first, lower-cased.
	Compatible []string
}

// Detect reads the board from the filesystem under root, which is "/" for the real host and a
// directory in tests. An empty root means "/".
func Detect(root string) Info {
	root = resolveRoot(root)

	return detect(root, func() string { return readText(filepath.Join(root, cpuInfoPath)) })
}

// DetectFromCPUInfo is Detect for a caller that has already read /proc/cpuinfo and hands the text
// over, so the file is not read twice.
func DetectFromCPUInfo(root string, cpuinfo string) Info {
	return detect(resolveRoot(root), func() string { return cpuinfo })
}

// LimitModel cuts a hardware model string to the length the server stores, on a character boundary,
// so a long value is shortened rather than rejected.
func LimitModel(model string) string {
	if len(model) <= maxModelBytes {
		return model
	}

	cut := maxModelBytes
	for (cut > 0) && (utf8.RuneStart(model[cut]) == false) {
		cut--
	}

	return strings.TrimSpace(model[:cut])
}

func resolveRoot(root string) string {
	if root == "" {
		return "/"
	}

	return root
}

// detect combines the signals. cpuinfo is a function so the file is read only when the device tree
// leaves something to be answered.
func detect(root string, cpuinfo func() string) Info {
	info := Info{Compatible: readCompatible(root)}
	info.Family = familyFromCompatible(info.Compatible)
	info.Model = sanitiseModel(readText(filepath.Join(root, deviceTreeModel)))

	if info.Family == "" {
		info.Family = familyFromDMI(
			readText(filepath.Join(root, dmiSysVendor)),
			readText(filepath.Join(root, dmiProductName)))
	}

	// The device tree answers both questions when it is readable. Without its compatible list the
	// cpuinfo model can still recognise a board, and without its model it can still name one.
	needsFamily := (info.Family == "") && (len(info.Compatible) == 0)
	if needsFamily || (info.Model == "") {
		cpuModel := cpuInfoModel(cpuinfo())

		if info.Model == "" {
			info.Model = sanitiseModel(cpuModel)
		}
		if needsFamily {
			info.Family = familyFromModel(cpuModel)
		}
	}

	return info
}

// readCompatible returns the entries of the device-tree compatible property: NUL-separated, most
// specific first, lower-cased and without empty entries.
func readCompatible(root string) []string {
	var entries []string

	for _, entry := range strings.Split(readText(filepath.Join(root, deviceTreeCompatible)), "\x00") {
		entry = strings.ToLower(strings.TrimSpace(entry))
		if entry != "" {
			entries = append(entries, entry)
		}
	}

	return entries
}

// familyFromCompatible looks at every entry, not only the first: a carrier board lists its own
// vendor ahead of the Raspberry Pi module it carries.
func familyFromCompatible(compatible []string) string {
	for _, entry := range compatible {
		vendor, _, found := strings.Cut(entry, ",")
		if found == false {
			continue
		}

		for _, candidate := range families {
			if (candidate.deviceTreeVendor != "") && (candidate.deviceTreeVendor == vendor) {
				return candidate.name
			}
		}
	}

	return ""
}

func familyFromModel(model string) string {
	model = strings.ToLower(model)

	for _, candidate := range families {
		if (candidate.modelPrefix != "") && strings.HasPrefix(model, candidate.modelPrefix) {
			return candidate.name
		}
	}

	return ""
}

func familyFromDMI(sysVendor string, productName string) string {
	haystack := strings.ToLower(sysVendor) + "\n" + strings.ToLower(productName)

	for _, candidate := range families {
		if (candidate.dmiText != "") && strings.Contains(haystack, candidate.dmiText) {
			return candidate.name
		}
	}

	return ""
}

// cpuInfoModel returns the value of the "Model" line of /proc/cpuinfo. The key is matched exactly:
// x86 prints a lower-case "model" with a number and "model name" with the CPU, and neither is a
// board.
func cpuInfoModel(cpuinfo string) string {
	for _, line := range strings.Split(cpuinfo, "\n") {
		key, value, found := strings.Cut(line, ":")
		if found && (strings.TrimSpace(key) == "Model") {
			return strings.TrimSpace(value)
		}
	}

	return ""
}

// sanitiseModel makes a display name safe to report: invalid UTF-8 and control characters are
// removed (the device tree ends its strings with a NUL), the ends are trimmed, and the result is cut
// to what the server stores.
func sanitiseModel(model string) string {
	model = strings.ToValidUTF8(model, "")
	model = strings.Map(func(r rune) rune {
		if unicode.IsControl(r) {
			return -1
		}

		return r
	}, model)

	return LimitModel(strings.TrimSpace(model))
}

// readText returns up to maxFileBytes of a file, or "" when it cannot be read, which includes a
// directory where the file should be.
func readText(path string) string {
	file, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer file.Close()

	data, err := io.ReadAll(io.LimitReader(file, maxFileBytes))
	if err != nil {
		return ""
	}

	return string(data)
}
