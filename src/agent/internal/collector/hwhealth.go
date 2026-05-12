// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"os/exec"
	"strconv"
	"strings"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
)

// HwHealthCollector collects hardware health data from IPMI, SMART, and lm-sensors.
type HwHealthCollector struct {
	hasIpmitool bool
	hasSmartctl bool
	hasSensors  bool
}

// NewHwHealthCollector creates a new HwHealthCollector.
func NewHwHealthCollector() *HwHealthCollector {
	_, ipmitoolErr := exec.LookPath("ipmitool")
	_, smartctlErr := exec.LookPath("smartctl")
	_, sensorsErr := exec.LookPath("sensors")

	return &HwHealthCollector{
		hasIpmitool: ipmitoolErr == nil,
		hasSmartctl: smartctlErr == nil,
		hasSensors:  sensorsErr == nil,
	}
}

func (c *HwHealthCollector) Name() string              { return "hardware_health" }
func (c *HwHealthCollector) DefaultInterval() time.Duration { return 5 * time.Minute }

type fanReading struct {
	Name   string `json:"name"`
	RPM    int    `json:"rpm"`
	Status string `json:"status"`
}

type powerSupplyReading struct {
	Name   string `json:"name"`
	Watts  int    `json:"watts"`
	Status string `json:"status"`
}

type temperatureReading struct {
	Name    string  `json:"name"`
	Celsius float64 `json:"celsius"`
	Status  string  `json:"status"`
}

type diskSmartReading struct {
	Device             string `json:"device"`
	Model              string `json:"model"`
	HealthStatus       string `json:"health_status"`
	TemperatureCelsius int    `json:"temperature_celsius"`
	WearoutPercent     int    `json:"wearout_percent"`
	PowerOnHours       int64  `json:"power_on_hours"`
}

type hwHealthPayload struct {
	Fans               []fanReading         `json:"fans"`
	PowerSupplies      []powerSupplyReading `json:"power_supplies"`
	Temperatures       []temperatureReading `json:"temperatures"`
	DiskSmart          []diskSmartReading   `json:"disk_smart"`
	BmcFirmwareVersion string               `json:"bmc_firmware_version"`
}

func (c *HwHealthCollector) Collect(ctx context.Context, store *db.Store) error {
	payload := hwHealthPayload{}

	if c.hasIpmitool {
		collectIPMIStructured(ctx, &payload)
		collectBMCInfo(ctx, &payload)
	}
	if c.hasSmartctl {
		collectSMARTStructured(ctx, &payload)
	}
	if c.hasSensors {
		collectLmSensorsStructured(ctx, &payload)
	}

	if len(payload.Fans) == 0 && len(payload.PowerSupplies) == 0 &&
		len(payload.Temperatures) == 0 && len(payload.DiskSmart) == 0 &&
		payload.BmcFirmwareVersion == "" {
		slog.Debug("no hardware health sensors available")

		return store.SaveCollectorState(c.Name(), nil)
	}

	data, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("marshaling hardware health: %w", err)
	}

	if err := store.EnqueueTelemetry(id.NewV7(), db.TelemetryHardwareHealth, string(data)); err != nil {
		return fmt.Errorf("enqueuing hardware health telemetry: %w", err)
	}

	return store.SaveCollectorState(c.Name(), nil)
}

func collectIPMIStructured(ctx context.Context, payload *hwHealthPayload) {
	out, err := runCmd(ctx, "ipmitool", "sdr")
	if err != nil {
		slog.Debug("ipmitool sdr failed", "error", err)

		return
	}

	parseIPMISDR(string(out), payload)
}

// parseIPMISDR parses the output of "ipmitool sdr" and appends fan, power supply,
// and temperature readings to the payload.
func parseIPMISDR(output string, payload *hwHealthPayload) {
	for _, line := range strings.Split(output, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}

		parts := strings.SplitN(line, "|", 3)
		if len(parts) < 3 {
			continue
		}

		name := strings.TrimSpace(parts[0])
		value := strings.TrimSpace(parts[1])
		status := strings.TrimSpace(parts[2])

		switch {
		case strings.Contains(value, "RPM"):
			rpm, _ := strconv.Atoi(strings.TrimSpace(strings.Replace(value, "RPM", "", 1)))
			payload.Fans = append(payload.Fans, fanReading{
				Name:   name,
				RPM:    rpm,
				Status: status,
			})
		case strings.Contains(value, "Watts"):
			watts, _ := strconv.Atoi(strings.TrimSpace(strings.Replace(value, "Watts", "", 1)))
			payload.PowerSupplies = append(payload.PowerSupplies, powerSupplyReading{
				Name:   name,
				Watts:  watts,
				Status: status,
			})
		case strings.Contains(value, "degrees C"):
			tempStr := strings.TrimSpace(strings.Replace(value, "degrees C", "", 1))
			temp, _ := strconv.ParseFloat(tempStr, 64)
			payload.Temperatures = append(payload.Temperatures, temperatureReading{
				Name:    name,
				Celsius: temp,
				Status:  status,
			})
		}
	}
}

func collectSMARTStructured(ctx context.Context, payload *hwHealthPayload) {
	out, err := runCmd(ctx, "smartctl", "--scan")
	if err != nil {
		slog.Debug("smartctl --scan failed", "error", err)

		return
	}

	for _, line := range strings.Split(string(out), "\n") {
		fields := strings.Fields(line)
		if len(fields) == 0 {
			continue
		}
		device := fields[0]

		// Only pass validated /dev/ paths to smartctl to prevent reading arbitrary files.
		if strings.HasPrefix(device, "/dev/") == false {
			slog.Debug("skipping non-device path from smartctl --scan", "path", device)
			continue
		}

		smartOut, err := runCmd(ctx, "smartctl", "--json=c", "--all", device)
		if err != nil {
			slog.Debug("smartctl failed for device", "device", device, "error", err)
			continue
		}

		reading, err := parseSMARTJSON(device, smartOut)
		if err != nil {
			slog.Debug("failed to parse smartctl JSON", "device", device, "error", err)
			continue
		}

		payload.DiskSmart = append(payload.DiskSmart, reading)
	}
}

// parseSMARTJSON parses the JSON output of "smartctl --json=c --all" for a single device.
func parseSMARTJSON(device string, data []byte) (diskSmartReading, error) {
	var smartData map[string]any
	if err := json.Unmarshal(data, &smartData); err != nil {
		return diskSmartReading{}, fmt.Errorf("unmarshal smartctl JSON: %w", err)
	}

	reading := diskSmartReading{Device: device}

	// Extract model.
	if modelName, ok := smartData["model_name"].(string); ok {
		reading.Model = modelName
	}

	// Extract health status.
	if health, ok := smartData["smart_status"].(map[string]any); ok {
		if passed, ok := health["passed"].(bool); ok {
			if passed {
				reading.HealthStatus = "PASSED"
			} else {
				reading.HealthStatus = "FAILED"
			}
		}
	}

	// Extract temperature.
	if temp, ok := smartData["temperature"].(map[string]any); ok {
		if current, ok := temp["current"].(float64); ok {
			reading.TemperatureCelsius = int(current)
		}
	}

	// Extract power-on hours.
	if hours, ok := smartData["power_on_time"].(map[string]any); ok {
		if h, ok := hours["hours"].(float64); ok {
			reading.PowerOnHours = int64(h)
		}
	}

	// Extract wearout (SSD percentage used).
	if attrs, ok := smartData["ata_smart_attributes"].(map[string]any); ok {
		if table, ok := attrs["table"].([]any); ok {
			for _, attr := range table {
				attrMap, ok := attr.(map[string]any)
				if ok == false {
					continue
				}
				attrName, _ := attrMap["name"].(string)
				if attrName == "Wear_Leveling_Count" || attrName == "Media_Wearout_Indicator" {
					if val, ok := attrMap["value"].(float64); ok {
						reading.WearoutPercent = 100 - int(val)
					}
				}
			}
		}
	}

	return reading, nil
}

func collectLmSensorsStructured(ctx context.Context, payload *hwHealthPayload) {
	out, err := runCmd(ctx, "sensors", "-j")
	if err != nil {
		slog.Debug("sensors -j failed", "error", err)

		return
	}

	if err := parseLmSensorsJSON(out, payload); err != nil {
		slog.Debug("failed to parse lm-sensors JSON", "error", err)
	}
}

// parseLmSensorsJSON parses the JSON output of "sensors -j" and appends temperature
// and fan readings to the payload.
func parseLmSensorsJSON(data []byte, payload *hwHealthPayload) error {
	var sensorsData map[string]any
	if err := json.Unmarshal(data, &sensorsData); err != nil {
		return fmt.Errorf("unmarshal lm-sensors JSON: %w", err)
	}

	for chipName, chipData := range sensorsData {
		chipMap, ok := chipData.(map[string]any)
		if ok == false {
			continue
		}
		for sensorName, sensorData := range chipMap {
			sensorMap, ok := sensorData.(map[string]any)
			if ok == false {
				continue
			}

			isTemp := strings.Contains(sensorName, "temp") || strings.Contains(sensorName, "Temp")
			isFan := strings.Contains(sensorName, "fan") || strings.Contains(sensorName, "Fan")

			for key, val := range sensorMap {
				num, ok := val.(float64)
				if ok == false {
					continue
				}
				if strings.Contains(key, "input") == false {
					continue
				}

				fullName := fmt.Sprintf("%s/%s", chipName, sensorName)
				if isTemp {
					payload.Temperatures = append(payload.Temperatures, temperatureReading{
						Name:    fullName,
						Celsius: num,
						Status:  "ok",
					})
				} else if isFan {
					payload.Fans = append(payload.Fans, fanReading{
						Name:   fullName,
						RPM:    int(num),
						Status: "ok",
					})
				}
			}
		}
	}

	return nil
}

func collectBMCInfo(ctx context.Context, payload *hwHealthPayload) {
	out, err := runCmd(ctx, "ipmitool", "mc", "info")
	if err != nil {
		slog.Debug("ipmitool mc info failed", "error", err)

		return
	}

	payload.BmcFirmwareVersion = parseBMCFirmwareVersion(string(out))
}

// parseBMCFirmwareVersion extracts the firmware revision from "ipmitool mc info" output.
func parseBMCFirmwareVersion(output string) string {
	for _, line := range strings.Split(output, "\n") {
		if strings.HasPrefix(line, "Firmware Revision") {
			parts := strings.SplitN(line, ":", 2)
			if len(parts) == 2 {
				return strings.TrimSpace(parts[1])
			}
		}
	}

	return ""
}