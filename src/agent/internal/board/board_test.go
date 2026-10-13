// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package board

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

const (
	pathCompatible  = "sys/firmware/devicetree/base/compatible"
	pathModel       = "sys/firmware/devicetree/base/model"
	pathCPUInfo     = "proc/cpuinfo"
	pathSysVendor   = "sys/class/dmi/id/sys_vendor"
	pathProductName = "sys/class/dmi/id/product_name"
)

// A 32-bit Raspberry Pi kernel prints the machine descriptor and the board revision beside the
// device-tree model, and the arm64 Raspberry Pi kernel prints the revision and the model only.
const (
	cpuInfoPi4Armhf = "processor\t: 0\nmodel name\t: ARMv7 Processor rev 3 (v7l)\nBogoMIPS\t: 108.00\n" +
		"Features\t: half thumb fastmult vfp edsp neon vfpv3 tls vfpv4 idiva idivt vfpd32 lpae evtstrm crc32\n" +
		"CPU implementer\t: 0x41\nCPU architecture: 7\nCPU variant\t: 0x0\nCPU part\t: 0xd08\nCPU revision\t: 3\n\n" +
		"Hardware\t: BCM2835\nRevision\t: d03114\nSerial\t\t: 10000000a1b2c3d4\nModel\t\t: Raspberry Pi 4 Model B Rev 1.4\n"

	cpuInfoPi5Arm64 = "processor\t: 0\nBogoMIPS\t: 108.00\nFeatures\t: fp asimd evtstrm aes pmull sha1 sha2 crc32 atomics fphp asimdhp cpuid asimdrdm lrcpc dcpop asimddp\n" +
		"CPU implementer\t: 0x41\nCPU architecture: 8\nCPU variant\t: 0x4\nCPU part\t: 0xd0b\nCPU revision\t: 1\n\n" +
		"Revision\t: c04170\nSerial\t\t: 10000000a1b2c3d4\nModel\t\t: Raspberry Pi 5 Model B Rev 1.0\n"

	cpuInfoMainlineArm64 = "processor\t: 0\nBogoMIPS\t: 50.00\nFeatures\t: fp asimd evtstrm aes pmull sha1 sha2 crc32 cpuid\n" +
		"CPU implementer\t: 0x41\nCPU architecture: 8\nCPU variant\t: 0x3\nCPU part\t: 0xd0c\nCPU revision\t: 1\n"

	cpuInfoX86 = "processor\t: 0\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 85\n" +
		"model name\t: Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz\nstepping\t: 7\n"
)

// writeFixture lays out files, keyed by their path under the root, in a fresh temporary root. A
// path that is absent from the map is absent from the filesystem, which is how a host without a
// device tree or without DMI looks.
func writeFixture(t *testing.T, files map[string]string) string {
	t.Helper()

	root := t.TempDir()
	for path, content := range files {
		full := filepath.Join(root, path)
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(full, []byte(content), 0o644); err != nil {
			t.Fatal(err)
		}
	}

	return root
}

// Intent: Every Raspberry Pi board is recognised from its device tree, whatever the model, SoC or
// word size, and a board that names itself reports that name with the revision the firmware adds.
// Hosts that are not Raspberry Pis stay unrecognised, and a board that is not recognised yet still
// reports its model.
func TestDetect(t *testing.T) {
	tests := []struct {
		name       string
		files      map[string]string
		wantFamily string
		wantModel  string
	}{
		{
			name: "Pi 4 B on Raspberry Pi OS 64-bit",
			files: map[string]string{
				pathCompatible: "raspberrypi,4-model-b\x00brcm,bcm2711\x00",
				pathModel:      "Raspberry Pi 4 Model B Rev 1.4\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 4 Model B Rev 1.4",
		},
		{
			name: "Pi 5",
			files: map[string]string{
				pathCompatible: "raspberrypi,5-model-b\x00brcm,bcm2712\x00",
				pathModel:      "Raspberry Pi 5 Model B Rev 1.0\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 5 Model B Rev 1.0",
		},
		{
			name: "Compute Module 4",
			files: map[string]string{
				pathCompatible: "raspberrypi,4-compute-module\x00brcm,bcm2711\x00",
				pathModel:      "Raspberry Pi Compute Module 4 Rev 1.1\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi Compute Module 4 Rev 1.1",
		},
		{
			name: "Compute Module 5",
			files: map[string]string{
				pathCompatible: "raspberrypi,5-compute-module\x00brcm,bcm2712\x00",
				pathModel:      "Raspberry Pi Compute Module 5 Rev 1.0\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi Compute Module 5 Rev 1.0",
		},
		{
			name: "Pi 400 keyboard computer is a Raspberry Pi like any other",
			files: map[string]string{
				pathCompatible: "raspberrypi,400\x00brcm,bcm2711\x00",
				pathModel:      "Raspberry Pi 400 Rev 1.0\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 400 Rev 1.0",
		},
		{
			name: "Pi 500 keyboard computer is a Raspberry Pi like any other",
			files: map[string]string{
				pathCompatible: "raspberrypi,500\x00brcm,bcm2712\x00",
				pathModel:      "Raspberry Pi 500 Rev 1.0\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 500 Rev 1.0",
		},
		{
			name: "Pi 3 B on a 32-bit kernel",
			files: map[string]string{
				pathCompatible: "raspberrypi,3-model-b\x00brcm,bcm2837\x00",
				pathModel:      "Raspberry Pi 3 Model B Rev 1.2\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 3 Model B Rev 1.2",
		},
		{
			name: "Zero 2 W",
			files: map[string]string{
				pathCompatible: "raspberrypi,model-zero-2-w\x00brcm,bcm2837\x00",
				pathModel:      "Raspberry Pi Zero 2 W Rev 1.0\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi Zero 2 W Rev 1.0",
		},
		{
			name: "carrier board that lists its own vendor before the Raspberry Pi board",
			files: map[string]string{
				pathCompatible: "acme,carrier-board\x00raspberrypi,4-compute-module\x00brcm,bcm2711\x00",
				pathModel:      "Acme Carrier Board with Compute Module 4\x00",
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Acme Carrier Board with Compute Module 4",
		},
		{
			name: "compatible without a trailing NUL and in upper case",
			files: map[string]string{
				pathCompatible: "RaspberryPi,4-Model-B",
			},
			wantFamily: FamilyRaspberryPi,
		},
		{
			name: "device tree present without a model file falls back to the cpuinfo model",
			files: map[string]string{
				pathCompatible: "raspberrypi,4-model-b\x00brcm,bcm2711\x00",
				pathCPUInfo:    cpuInfoPi4Armhf,
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 4 Model B Rev 1.4",
		},
		{
			name: "device tree model wins over a different cpuinfo model",
			files: map[string]string{
				pathCompatible: "raspberrypi,5-model-b\x00brcm,bcm2712\x00",
				pathModel:      "Raspberry Pi 5 Model B Rev 1.0\x00",
				pathCPUInfo:    cpuInfoPi4Armhf,
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 5 Model B Rev 1.0",
		},
		{
			name: "Docker on Raspberry Pi OS 32-bit, device tree masked",
			files: map[string]string{
				pathCPUInfo: cpuInfoPi4Armhf,
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 4 Model B Rev 1.4",
		},
		{
			name: "Docker on Raspberry Pi OS arm64, device tree masked",
			files: map[string]string{
				pathCPUInfo: cpuInfoPi5Arm64,
			},
			wantFamily: FamilyRaspberryPi,
			wantModel:  "Raspberry Pi 5 Model B Rev 1.0",
		},
		{
			name: "mainline arm64 kernel in Docker identifies nothing",
			files: map[string]string{
				pathCPUInfo: cpuInfoMainlineArm64,
			},
		},
		{
			name: "cpuinfo model of a board that is not recognised is still the model",
			files: map[string]string{
				pathCPUInfo: "processor\t: 0\n\nModel\t\t: Some Other Board\n",
			},
			wantModel: "Some Other Board",
		},
		{
			name: "x86 host: the lower-case model line is not the board model",
			files: map[string]string{
				pathCPUInfo: cpuInfoX86,
			},
		},
		{
			name: "Orange Pi 5 is not a recognised family yet but reports its model",
			files: map[string]string{
				pathCompatible: "xunlong,orangepi-5\x00rockchip,rk3588s\x00",
				pathModel:      "Orange Pi 5\x00",
			},
			wantModel: "Orange Pi 5",
		},
		{
			name: "Apple Silicon laptop under Asahi is not a single-board computer",
			files: map[string]string{
				pathCompatible: "apple,j293\x00apple,t8103\x00apple,arm-platform\x00",
				pathModel:      "Apple MacBook Pro (13-inch, M1, 2020)\x00",
			},
			wantModel: "Apple MacBook Pro (13-inch, M1, 2020)",
		},
		{
			name: "QEMU virt guest",
			files: map[string]string{
				pathCompatible: "linux,dummy-virt\x00",
			},
		},
		{
			name: "a recognised device tree is not second-guessed by cpuinfo",
			files: map[string]string{
				pathCompatible: "apple,j293\x00apple,t8103\x00",
				pathModel:      "Apple MacBook Pro (13-inch, M1, 2020)\x00",
				pathCPUInfo:    cpuInfoPi5Arm64,
			},
			wantModel: "Apple MacBook Pro (13-inch, M1, 2020)",
		},
		{
			name: "UEFI-booted Pi 4 (pftf RPi4 v1.53, edk2-platforms c8295e07)",
			files: map[string]string{
				pathSysVendor:   "Raspberry Pi Foundation\n",
				pathProductName: "Raspberry Pi 4 Model B\n",
			},
			wantFamily: FamilyRaspberryPi,
		},
		{
			name: "UEFI-booted Pi 3 (pftf RPi3 v1.53.1, edk2-platforms c8295e07)",
			files: map[string]string{
				pathSysVendor:   "Raspberry Pi Foundation\n",
				pathProductName: "Raspberry Pi 3 Model B\n",
			},
			wantFamily: FamilyRaspberryPi,
		},
		{
			name: "UEFI-booted Pi 5 (worproject rpi5-uefi, edk2-platforms 8e1779b5)",
			files: map[string]string{
				pathSysVendor:   "Raspberry Pi Foundation\n",
				pathProductName: "Raspberry Pi 5 Model B\n",
			},
			wantFamily: FamilyRaspberryPi,
		},
		{
			name: "UEFI Pi whose model the firmware table does not know",
			files: map[string]string{
				pathSysVendor:   "Raspberry Pi Foundation\n",
				pathProductName: "Unknown Raspberry Pi Model\n",
			},
			wantFamily: FamilyRaspberryPi,
		},
		{
			name: "DMI vendor alone is enough, in any case",
			files: map[string]string{
				pathSysVendor: "RASPBERRY PI Foundation\n",
			},
			wantFamily: FamilyRaspberryPi,
		},
		{
			name: "DMI product name alone is enough",
			files: map[string]string{
				pathProductName: "raspberry pi 4 model b\n",
			},
			wantFamily: FamilyRaspberryPi,
		},
		{
			name: "ordinary x86 server DMI",
			files: map[string]string{
				pathSysVendor:   "Dell Inc.\n",
				pathProductName: "PowerEdge R640\n",
				pathCPUInfo:     cpuInfoX86,
			},
		},
		{
			name:  "nothing readable at all",
			files: map[string]string{},
		},
		{
			name: "empty and blank files",
			files: map[string]string{
				pathCompatible:  "",
				pathModel:       "\x00",
				pathCPUInfo:     "",
				pathSysVendor:   "\n",
				pathProductName: "  \n",
			},
		},
		{
			name: "the /proc/device-tree symlink path is never read",
			files: map[string]string{
				"proc/device-tree/compatible": "raspberrypi,4-model-b\x00brcm,bcm2711\x00",
				"proc/device-tree/model":      "Raspberry Pi 4 Model B Rev 1.4\x00",
			},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := Detect(writeFixture(t, tt.files))

			if got.Family != tt.wantFamily {
				t.Errorf("Family = %q, want %q", got.Family, tt.wantFamily)
			}
			if got.Model != tt.wantModel {
				t.Errorf("Model = %q, want %q", got.Model, tt.wantModel)
			}
		})
	}
}

// Intent: The compatible list comes back split on NUL, lower-cased and without empty entries, so a
// caller can read the SoC after the board.
func TestDetect_CompatibleList(t *testing.T) {
	root := writeFixture(t, map[string]string{
		pathCompatible: "RaspberryPi,4-Model-B\x00\x00brcm,bcm2711\x00",
	})

	want := []string{"raspberrypi,4-model-b", "brcm,bcm2711"}
	if got := Detect(root).Compatible; !reflect.DeepEqual(got, want) {
		t.Errorf("Compatible = %q, want %q", got, want)
	}
}

// Intent: A directory where a file should be is unreadable, not a crash, and gives no signal.
func TestDetect_DirectoriesInPlaceOfFiles_GiveNothing(t *testing.T) {
	root := t.TempDir()
	for _, path := range []string{pathCompatible, pathModel, pathCPUInfo, pathSysVendor, pathProductName} {
		if err := os.MkdirAll(filepath.Join(root, path), 0o755); err != nil {
			t.Fatal(err)
		}
	}

	if got := Detect(root); got.Family != "" || got.Model != "" || len(got.Compatible) != 0 {
		t.Errorf("Detect = %+v, want the zero value", got)
	}
}

// Intent: An empty root means the real filesystem root, as it does for the machine id, so the host
// is read when no fixture is given.
func TestResolveRoot(t *testing.T) {
	if got := resolveRoot(""); got != "/" {
		t.Errorf("resolveRoot(\"\") = %q, want /", got)
	}
	if got := resolveRoot("/tmp/fixture"); got != "/tmp/fixture" {
		t.Errorf("resolveRoot(a directory) = %q, want it unchanged", got)
	}
}

// Intent: A model that carries control characters or is too long is cleaned before it is reported,
// because the server stores at most 255 characters and a control character is never part of a name.
func TestDetect_ModelIsSanitisedAndBounded(t *testing.T) {
	tests := []struct {
		name  string
		model string
		want  string
	}{
		{"control characters are removed", "Raspberry\x01 Pi\x7f 4\r\n\x00", "Raspberry Pi 4"},
		{"surrounding whitespace is trimmed", "  Raspberry Pi 4  \x00", "Raspberry Pi 4"},
		{"invalid UTF-8 is dropped", "Raspberry Pi \xff\xfe4", "Raspberry Pi 4"},
		{"255 bytes are kept as they are", strings.Repeat("a", 255), strings.Repeat("a", 255)},
		{"longer than 255 bytes is cut at 255", strings.Repeat("a", 300), strings.Repeat("a", 255)},
		{
			// 127 two-byte characters are 254 bytes, and a 128th would end at 256.
			"a two-byte character that would straddle the limit is dropped whole",
			strings.Repeat("é", 200),
			strings.Repeat("é", 127),
		},
		{
			// One byte plus 127 two-byte characters ends exactly on 255.
			"a character that ends exactly on the limit is kept",
			"a" + strings.Repeat("é", 200),
			"a" + strings.Repeat("é", 127),
		},
		{
			"a cut that ends in a space leaves no trailing space",
			strings.Repeat("a", 254) + " b",
			strings.Repeat("a", 254),
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			root := writeFixture(t, map[string]string{pathModel: tt.model})

			if got := Detect(root).Model; got != tt.want {
				t.Errorf("Model = %q (%d bytes), want %q (%d bytes)", got, len(got), tt.want, len(tt.want))
			}
		})
	}
}

// Intent: The same cleaning applies to a model that arrives through cpuinfo.
func TestDetect_CPUInfoModelIsBounded(t *testing.T) {
	root := writeFixture(t, map[string]string{
		pathCPUInfo: "Model\t\t: " + strings.Repeat("b", 400) + "\n",
	})

	if got := Detect(root).Model; got != strings.Repeat("b", 255) {
		t.Errorf("Model has %d bytes, want 255", len(got))
	}
}

// Intent: A caller that already holds the cpuinfo text gets the same answer without the file being
// read again; the root's own /proc/cpuinfo is not consulted when the text is given.
func TestDetectFromCPUInfo_UsesTheTextItIsGiven(t *testing.T) {
	root := writeFixture(t, map[string]string{
		pathCPUInfo: cpuInfoPi4Armhf,
	})

	got := DetectFromCPUInfo(root, cpuInfoPi5Arm64)
	if got.Family != FamilyRaspberryPi || got.Model != "Raspberry Pi 5 Model B Rev 1.0" {
		t.Errorf("DetectFromCPUInfo = %+v, want the Pi 5 from the given text", got)
	}
}

// Intent: With the device tree readable the given cpuinfo text is not needed, and nothing in it
// overrides the board the device tree names.
func TestDetectFromCPUInfo_DeviceTreeStillWins(t *testing.T) {
	root := writeFixture(t, map[string]string{
		pathCompatible: "raspberrypi,4-model-b\x00brcm,bcm2711\x00",
		pathModel:      "Raspberry Pi 4 Model B Rev 1.4\x00",
	})

	got := DetectFromCPUInfo(root, cpuInfoMainlineArm64)
	if got.Family != FamilyRaspberryPi || got.Model != "Raspberry Pi 4 Model B Rev 1.4" {
		t.Errorf("DetectFromCPUInfo = %+v", got)
	}
}

// Intent: Text with no usable board in it, including text that is empty, gives nothing.
func TestDetectFromCPUInfo_NoBoardInTheText(t *testing.T) {
	root := t.TempDir()

	for name, text := range map[string]string{"empty": "", "x86": cpuInfoX86, "mainline arm64": cpuInfoMainlineArm64} {
		t.Run(name, func(t *testing.T) {
			if got := DetectFromCPUInfo(root, text); got.Family != "" || got.Model != "" {
				t.Errorf("DetectFromCPUInfo = %+v, want nothing", got)
			}
		})
	}
}

// Intent: LimitModel keeps a model the server can store: unchanged when it fits, and otherwise cut
// at 255 bytes without splitting a character.
func TestLimitModel(t *testing.T) {
	tests := []struct {
		name  string
		model string
		want  string
	}{
		{"empty", "", ""},
		{"short", "PowerEdge R640", "PowerEdge R640"},
		{"exactly 255 bytes", strings.Repeat("a", 255), strings.Repeat("a", 255)},
		{"256 bytes", strings.Repeat("a", 256), strings.Repeat("a", 255)},
		{"multi-byte straddling the limit", strings.Repeat("é", 200), strings.Repeat("é", 127)},
		{"three-byte characters", strings.Repeat("€", 100), strings.Repeat("€", 85)},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := LimitModel(tt.model); got != tt.want {
				t.Errorf("LimitModel = %d bytes, want %d bytes", len(got), len(tt.want))
			}
		})
	}
}
