// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"testing"
)

// --- parseIPMISDR tests ---

// Intent: Fan, power supply, and temperature readings are parsed from IPMI SDR output.
func TestParseIPMISDR_MixedSensors(t *testing.T) {
	output := `Fan1             | 3400 RPM          | ok
Fan2             | 3200 RPM          | ok
PSU1 Power       | 120 Watts         | ok
CPU Temp         | 45 degrees C      | ok
Inlet Temp       | 25 degrees C      | ok
`
	payload := &hwHealthPayload{}
	parseIPMISDR(output, payload)

	if len(payload.Fans) != 2 {
		t.Errorf("expected 2 fans, got %d", len(payload.Fans))
	}
	if len(payload.Fans) > 0 && payload.Fans[0].RPM != 3400 {
		t.Errorf("Fan1 RPM = %d, want 3400", payload.Fans[0].RPM)
	}
	if len(payload.Fans) > 0 && payload.Fans[0].Status != "ok" {
		t.Errorf("Fan1 Status = %q, want %q", payload.Fans[0].Status, "ok")
	}

	if len(payload.PowerSupplies) != 1 {
		t.Errorf("expected 1 power supply, got %d", len(payload.PowerSupplies))
	}
	if len(payload.PowerSupplies) > 0 && payload.PowerSupplies[0].Watts != 120 {
		t.Errorf("PSU1 Watts = %d, want 120", payload.PowerSupplies[0].Watts)
	}

	if len(payload.Temperatures) != 2 {
		t.Errorf("expected 2 temperatures, got %d", len(payload.Temperatures))
	}
	if len(payload.Temperatures) > 0 && payload.Temperatures[0].Celsius != 45.0 {
		t.Errorf("CPU Temp = %f, want 45.0", payload.Temperatures[0].Celsius)
	}
}

// Intent: Empty output produces no readings.
func TestParseIPMISDR_EmptyOutput(t *testing.T) {
	payload := &hwHealthPayload{}
	parseIPMISDR("", payload)

	if len(payload.Fans) != 0 {
		t.Errorf("expected 0 fans, got %d", len(payload.Fans))
	}
	if len(payload.PowerSupplies) != 0 {
		t.Errorf("expected 0 power supplies, got %d", len(payload.PowerSupplies))
	}
	if len(payload.Temperatures) != 0 {
		t.Errorf("expected 0 temperatures, got %d", len(payload.Temperatures))
	}
}

// Intent: Lines with fewer than 3 pipe-separated fields are skipped.
func TestParseIPMISDR_MalformedLines(t *testing.T) {
	output := `Fan1 | 3400 RPM
incomplete line
Fan2             | 3200 RPM          | ok
`
	payload := &hwHealthPayload{}
	parseIPMISDR(output, payload)

	if len(payload.Fans) != 1 {
		t.Errorf("expected 1 fan (malformed lines skipped), got %d", len(payload.Fans))
	}
}

// Intent: Non-matching sensor values (no RPM/Watts/degrees C) are ignored.
func TestParseIPMISDR_UnknownSensorType(t *testing.T) {
	output := "Voltage          | 12.1 Volts        | ok\n"
	payload := &hwHealthPayload{}
	parseIPMISDR(output, payload)

	if len(payload.Fans) != 0 || len(payload.PowerSupplies) != 0 || len(payload.Temperatures) != 0 {
		t.Error("expected no readings for unknown sensor type")
	}
}

// --- parseSMARTJSON tests ---

// Intent: Full SMART JSON with all fields is parsed correctly.
func TestParseSMARTJSON_FullData(t *testing.T) {
	data := []byte(`{
		"model_name": "Samsung SSD 870",
		"smart_status": {"passed": true},
		"temperature": {"current": 35},
		"power_on_time": {"hours": 12345},
		"ata_smart_attributes": {
			"table": [
				{"name": "Wear_Leveling_Count", "value": 95}
			]
		}
	}`)

	reading, err := parseSMARTJSON("/dev/sda", data)
	if err != nil {
		t.Fatalf("parseSMARTJSON: %v", err)
	}

	if reading.Device != "/dev/sda" {
		t.Errorf("Device = %q, want %q", reading.Device, "/dev/sda")
	}
	if reading.Model != "Samsung SSD 870" {
		t.Errorf("Model = %q, want %q", reading.Model, "Samsung SSD 870")
	}
	if reading.HealthStatus != "PASSED" {
		t.Errorf("HealthStatus = %q, want %q", reading.HealthStatus, "PASSED")
	}
	if reading.TemperatureCelsius != 35 {
		t.Errorf("TemperatureCelsius = %d, want 35", reading.TemperatureCelsius)
	}
	if reading.PowerOnHours != 12345 {
		t.Errorf("PowerOnHours = %d, want 12345", reading.PowerOnHours)
	}
	if reading.WearoutPercent != 5 {
		t.Errorf("WearoutPercent = %d, want 5 (100 - 95)", reading.WearoutPercent)
	}
}

// Intent: SMART health status "FAILED" is correctly reported.
func TestParseSMARTJSON_FailedHealth(t *testing.T) {
	data := []byte(`{"smart_status": {"passed": false}}`)

	reading, err := parseSMARTJSON("/dev/sdb", data)
	if err != nil {
		t.Fatalf("parseSMARTJSON: %v", err)
	}

	if reading.HealthStatus != "FAILED" {
		t.Errorf("HealthStatus = %q, want %q", reading.HealthStatus, "FAILED")
	}
}

// Intent: Minimal JSON (empty object) parses without error and returns zero-value fields.
func TestParseSMARTJSON_MinimalData(t *testing.T) {
	data := []byte(`{}`)

	reading, err := parseSMARTJSON("/dev/sda", data)
	if err != nil {
		t.Fatalf("parseSMARTJSON: %v", err)
	}

	if reading.Model != "" {
		t.Errorf("Model = %q, want empty", reading.Model)
	}
	if reading.HealthStatus != "" {
		t.Errorf("HealthStatus = %q, want empty", reading.HealthStatus)
	}
}

// Intent: Invalid JSON returns an error.
func TestParseSMARTJSON_InvalidJSON(t *testing.T) {
	_, err := parseSMARTJSON("/dev/sda", []byte("not json"))
	if err == nil {
		t.Error("expected error for invalid JSON, got nil")
	}
}

// Intent: Media_Wearout_Indicator attribute is also recognized for wearout.
func TestParseSMARTJSON_MediaWearoutIndicator(t *testing.T) {
	data := []byte(`{
		"ata_smart_attributes": {
			"table": [
				{"name": "Media_Wearout_Indicator", "value": 80}
			]
		}
	}`)

	reading, err := parseSMARTJSON("/dev/sda", data)
	if err != nil {
		t.Fatalf("parseSMARTJSON: %v", err)
	}

	if reading.WearoutPercent != 20 {
		t.Errorf("WearoutPercent = %d, want 20 (100 - 80)", reading.WearoutPercent)
	}
}

// Intent: Non-map attributes in SMART table are gracefully skipped.
func TestParseSMARTJSON_NonMapAttribute(t *testing.T) {
	data := []byte(`{
		"ata_smart_attributes": {
			"table": ["not-a-map", {"name": "Other_Attr", "value": 99}]
		}
	}`)

	reading, err := parseSMARTJSON("/dev/sda", data)
	if err != nil {
		t.Fatalf("parseSMARTJSON: %v", err)
	}

	if reading.WearoutPercent != 0 {
		t.Errorf("WearoutPercent = %d, want 0", reading.WearoutPercent)
	}
}

// --- parseLmSensorsJSON tests ---

// Intent: Temperature and fan readings are extracted from lm-sensors JSON.
func TestParseLmSensorsJSON_TempAndFan(t *testing.T) {
	data := []byte(`{
		"coretemp-isa-0000": {
			"temp1": {"temp1_input": 55.0},
			"fan1": {"fan1_input": 1200.0}
		}
	}`)

	payload := &hwHealthPayload{}
	err := parseLmSensorsJSON(data, payload)
	if err != nil {
		t.Fatalf("parseLmSensorsJSON: %v", err)
	}

	if len(payload.Temperatures) != 1 {
		t.Fatalf("expected 1 temperature, got %d", len(payload.Temperatures))
	}
	if payload.Temperatures[0].Celsius != 55.0 {
		t.Errorf("Celsius = %f, want 55.0", payload.Temperatures[0].Celsius)
	}
	if payload.Temperatures[0].Name != "coretemp-isa-0000/temp1" {
		t.Errorf("Name = %q, want %q", payload.Temperatures[0].Name, "coretemp-isa-0000/temp1")
	}

	if len(payload.Fans) != 1 {
		t.Fatalf("expected 1 fan, got %d", len(payload.Fans))
	}
	if payload.Fans[0].RPM != 1200 {
		t.Errorf("RPM = %d, want 1200", payload.Fans[0].RPM)
	}
}

// Intent: Non-input keys and non-numeric values are ignored.
func TestParseLmSensorsJSON_SkipsNonInput(t *testing.T) {
	data := []byte(`{
		"coretemp-isa-0000": {
			"temp1": {"temp1_max": 100.0, "temp1_crit": 110.0}
		}
	}`)

	payload := &hwHealthPayload{}
	err := parseLmSensorsJSON(data, payload)
	if err != nil {
		t.Fatalf("parseLmSensorsJSON: %v", err)
	}

	if len(payload.Temperatures) != 0 {
		t.Errorf("expected 0 temperatures (no input keys), got %d", len(payload.Temperatures))
	}
}

// Intent: Invalid JSON returns an error.
func TestParseLmSensorsJSON_InvalidJSON(t *testing.T) {
	err := parseLmSensorsJSON([]byte("not json"), &hwHealthPayload{})
	if err == nil {
		t.Error("expected error for invalid JSON, got nil")
	}
}

// Intent: Non-map chip data and non-map sensor data are gracefully skipped.
func TestParseLmSensorsJSON_NonMapData(t *testing.T) {
	data := []byte(`{
		"not-a-chip": "string-value",
		"chip-isa-0000": {
			"not-a-sensor": 42,
			"temp1": {"temp1_input": 60.0}
		}
	}`)

	payload := &hwHealthPayload{}
	err := parseLmSensorsJSON(data, payload)
	if err != nil {
		t.Fatalf("parseLmSensorsJSON: %v", err)
	}

	if len(payload.Temperatures) != 1 {
		t.Errorf("expected 1 temperature, got %d", len(payload.Temperatures))
	}
}

// Intent: Empty JSON object produces no readings.
func TestParseLmSensorsJSON_EmptyData(t *testing.T) {
	payload := &hwHealthPayload{}
	err := parseLmSensorsJSON([]byte("{}"), payload)
	if err != nil {
		t.Fatalf("parseLmSensorsJSON: %v", err)
	}

	if len(payload.Temperatures) != 0 || len(payload.Fans) != 0 {
		t.Error("expected no readings for empty data")
	}
}

// --- parseBMCFirmwareVersion tests ---

// Intent: Firmware revision is extracted from ipmitool mc info output.
func TestParseBMCFirmwareVersion_Found(t *testing.T) {
	output := `Device ID                 : 32
Device Revision           : 1
Firmware Revision         : 2.85
IPMI Version              : 2.0
`
	version := parseBMCFirmwareVersion(output)

	if version != "2.85" {
		t.Errorf("version = %q, want %q", version, "2.85")
	}
}

// Intent: Missing firmware revision returns empty string.
func TestParseBMCFirmwareVersion_NotFound(t *testing.T) {
	output := "Device ID : 32\nDevice Revision : 1\n"
	version := parseBMCFirmwareVersion(output)

	if version != "" {
		t.Errorf("version = %q, want empty", version)
	}
}

// Intent: Empty output returns empty string.
func TestParseBMCFirmwareVersion_EmptyOutput(t *testing.T) {
	version := parseBMCFirmwareVersion("")

	if version != "" {
		t.Errorf("version = %q, want empty", version)
	}
}
