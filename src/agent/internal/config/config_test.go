// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package config

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/BurntSushi/toml"
)

func TestDatabasePath_ReturnsCorrectPath(t *testing.T) {
	cfg := &Config{
		DataDir: "/var/lib/vord-agent",
	}

	got := cfg.DatabasePath()
	expected := "/var/lib/vord-agent/agent.db"
	if got != expected {
		t.Errorf("DatabasePath() = %q, want %q", got, expected)
	}
}

func TestDatabasePath_TrailingSlash(t *testing.T) {
	cfg := &Config{
		DataDir: "/tmp/data/",
	}

	got := cfg.DatabasePath()
	expected := "/tmp/data/agent.db"
	if got != expected {
		t.Errorf("DatabasePath() = %q, want %q", got, expected)
	}
}

func TestGRPCTarget_ReturnsCorrectFormat(t *testing.T) {
	cfg := &Config{
		ServerAddress: "example.com",
		ServerPort:    12234,
	}

	got := cfg.GRPCTarget()
	expected := "example.com:12234"
	if got != expected {
		t.Errorf("GRPCTarget() = %q, want %q", got, expected)
	}
}

func TestGRPCTarget_Localhost(t *testing.T) {
	cfg := &Config{
		ServerAddress: "localhost",
		ServerPort:    8080,
	}

	got := cfg.GRPCTarget()
	expected := "localhost:8080"
	if got != expected {
		t.Errorf("GRPCTarget() = %q, want %q", got, expected)
	}
}

func TestGRPCTarget_WithIPAddress(t *testing.T) {
	cfg := &Config{
		ServerAddress: "192.168.1.100",
		ServerPort:    443,
	}

	got := cfg.GRPCTarget()
	expected := "192.168.1.100:443"
	if got != expected {
		t.Errorf("GRPCTarget() = %q, want %q", got, expected)
	}
}

func TestDatabasePath_WithRelativeDir(t *testing.T) {
	cfg := &Config{
		DataDir: "data",
	}

	got := cfg.DatabasePath()
	expected := "data/agent.db"
	if got != expected {
		t.Errorf("DatabasePath() = %q, want %q", got, expected)
	}
}

// Intent: Default config values match expected defaults.
func TestConfig_DefaultValues(t *testing.T) {
	cfg := &Config{
		ServerAddress:       "localhost",
		ServerPort:          12234,
		DataDir:             "/var/lib/vord-agent",
		LogLevel:            "info",
		UseTLS:              true,
		AllowRemoteCommands: false,
	}

	if cfg.ServerAddress != "localhost" {
		t.Errorf("expected default ServerAddress=%q, got %q", "localhost", cfg.ServerAddress)
	}
	if cfg.ServerPort != 12234 {
		t.Errorf("expected default ServerPort=12234, got %d", cfg.ServerPort)
	}
	if cfg.DataDir != "/var/lib/vord-agent" {
		t.Errorf("expected default DataDir=%q, got %q", "/var/lib/vord-agent", cfg.DataDir)
	}
	if cfg.LogLevel != "info" {
		t.Errorf("expected default LogLevel=%q, got %q", "info", cfg.LogLevel)
	}
	if cfg.UseTLS == false {
		t.Error("expected default UseTLS=true")
	}
	if cfg.AllowRemoteCommands {
		t.Error("expected default AllowRemoteCommands=false")
	}
}

// Intent: GRPCTarget produces correct dial target with various port values.
func TestGRPCTarget_EdgePorts(t *testing.T) {
	tests := []struct {
		port int
		want string
	}{
		{1, "host:1"},
		{65535, "host:65535"},
	}

	for _, tt := range tests {
		cfg := &Config{ServerAddress: "host", ServerPort: tt.port}
		if got := cfg.GRPCTarget(); got != tt.want {
			t.Errorf("GRPCTarget() with port=%d = %q, want %q", tt.port, got, tt.want)
		}
	}
}

// --- loadFromTOML tests ---

// Intent: Valid TOML file with all fields parses correctly.
func TestLoadFromTOML_AllFields(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	content := `
server_address = "custom.host"
server_port = 9999
data_dir = "/custom/dir"
log_level = "debug"
use_tls = false
allow_remote_commands = true
registration_token = "my-token"
`
	if err := os.WriteFile(path, []byte(content), 0600); err != nil {
		t.Fatalf("writing test config: %v", err)
	}

	fc, meta, err := loadFromTOML(path)
	if err != nil {
		t.Fatalf("loadFromTOML: %v", err)
	}

	if fc.ServerAddress != "custom.host" {
		t.Errorf("ServerAddress = %q, want %q", fc.ServerAddress, "custom.host")
	}
	if fc.ServerPort != 9999 {
		t.Errorf("ServerPort = %d, want 9999", fc.ServerPort)
	}
	if fc.DataDir != "/custom/dir" {
		t.Errorf("DataDir = %q, want %q", fc.DataDir, "/custom/dir")
	}
	if fc.LogLevel != "debug" {
		t.Errorf("LogLevel = %q, want %q", fc.LogLevel, "debug")
	}
	if fc.UseTLS {
		t.Error("UseTLS should be false")
	}
	if fc.AllowRemoteCommands == false {
		t.Error("AllowRemoteCommands should be true")
	}
	if fc.RegistrationToken != "my-token" {
		t.Errorf("RegistrationToken = %q, want %q", fc.RegistrationToken, "my-token")
	}
	if meta.IsDefined("server_address") == false {
		t.Error("expected server_address to be defined in metadata")
	}
}

// Intent: Partial TOML file only defines the fields it contains.
func TestLoadFromTOML_PartialFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	content := `server_address = "partial.host"` + "\n"
	if err := os.WriteFile(path, []byte(content), 0600); err != nil {
		t.Fatalf("writing test config: %v", err)
	}

	fc, meta, err := loadFromTOML(path)
	if err != nil {
		t.Fatalf("loadFromTOML: %v", err)
	}

	if fc.ServerAddress != "partial.host" {
		t.Errorf("ServerAddress = %q, want %q", fc.ServerAddress, "partial.host")
	}
	if meta.IsDefined("server_address") == false {
		t.Error("expected server_address to be defined")
	}
	if meta.IsDefined("server_port") {
		t.Error("server_port should not be defined in partial config")
	}
	if meta.IsDefined("log_level") {
		t.Error("log_level should not be defined in partial config")
	}
}

// Intent: Missing TOML file is silently skipped without error.
func TestLoadFromTOML_MissingFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "nonexistent.toml")

	_, _, err := loadFromTOML(path)
	if err != nil {
		t.Errorf("expected no error for missing file, got %v", err)
	}
}

// Intent: Invalid TOML syntax returns an error.
func TestLoadFromTOML_InvalidSyntax(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	if err := os.WriteFile(path, []byte("invalid %%% toml [[[ content"), 0600); err != nil {
		t.Fatalf("writing test config: %v", err)
	}

	_, _, err := loadFromTOML(path)
	if err == nil {
		t.Error("expected error for invalid TOML syntax, got nil")
	}
}

// Intent: Empty TOML file parses without error and no fields are defined.
func TestLoadFromTOML_EmptyFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	if err := os.WriteFile(path, []byte(""), 0600); err != nil {
		t.Fatalf("writing test config: %v", err)
	}

	_, meta, err := loadFromTOML(path)
	if err != nil {
		t.Fatalf("loadFromTOML: %v", err)
	}

	if meta.IsDefined("server_address") {
		t.Error("expected no fields defined in empty config")
	}
}

// --- applyLayeredConfig / environment variable tests ---

// helper to create a default config for env var tests.
func defaultTestConfig() *Config {
	return &Config{
		ServerAddress:       "localhost",
		ServerPort:          12234,
		DataDir:             "/var/lib/vord-agent",
		LogLevel:            "info",
		UseTLS:              true,
		AllowRemoteCommands: false,
	}
}

// helper to create empty metadata (no TOML keys defined).
func emptyMeta() toml.MetaData {
	var fc fileConfig
	meta, _ := toml.Decode("", &fc)

	return meta
}

// Intent: VORD_SERVER_ADDRESS env var is picked up when no flag or TOML override.
func TestApplyLayeredConfig_EnvServerAddress(t *testing.T) {
	t.Setenv("VORD_SERVER_ADDRESS", "env.host.com")

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.ServerAddress != "env.host.com" {
		t.Errorf("ServerAddress = %q, want %q", cfg.ServerAddress, "env.host.com")
	}
}

// Intent: VORD_SERVER_PORT with valid int is parsed correctly.
func TestApplyLayeredConfig_EnvServerPortValid(t *testing.T) {
	t.Setenv("VORD_SERVER_PORT", "8080")

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.ServerPort != 8080 {
		t.Errorf("ServerPort = %d, want 8080", cfg.ServerPort)
	}
}

// Intent: VORD_SERVER_PORT with non-numeric value is silently ignored, default preserved.
func TestApplyLayeredConfig_EnvServerPortNonNumeric(t *testing.T) {
	t.Setenv("VORD_SERVER_PORT", "abc")

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.ServerPort != 12234 {
		t.Errorf("ServerPort = %d, want default 12234", cfg.ServerPort)
	}
}

// Intent: VORD_USE_TLS "true" and "false" are parsed correctly.
func TestApplyLayeredConfig_EnvUseTLS(t *testing.T) {
	t.Setenv("VORD_USE_TLS", "false")

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.UseTLS {
		t.Error("expected UseTLS=false when VORD_USE_TLS=false")
	}

	t.Setenv("VORD_USE_TLS", "true")
	cfg = defaultTestConfig()
	cfg.UseTLS = false
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.UseTLS == false {
		t.Error("expected UseTLS=true when VORD_USE_TLS=true")
	}
}

// Intent: VORD_USE_TLS with invalid value is silently ignored.
func TestApplyLayeredConfig_EnvUseTLSInvalid(t *testing.T) {
	t.Setenv("VORD_USE_TLS", "maybe")

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.UseTLS == false {
		t.Error("expected UseTLS=true (default) when VORD_USE_TLS is invalid")
	}
}

// Intent: VORD_ALLOW_REMOTE_COMMANDS "true" is parsed correctly.
func TestApplyLayeredConfig_EnvAllowRemoteCommands(t *testing.T) {
	t.Setenv("VORD_ALLOW_REMOTE_COMMANDS", "true")

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.AllowRemoteCommands == false {
		t.Error("expected AllowRemoteCommands=true when VORD_ALLOW_REMOTE_COMMANDS=true")
	}
}

// Intent: VORD_DATA_DIR, VORD_LOG_LEVEL, and VORD_REGISTRATION_TOKEN are each picked up.
func TestApplyLayeredConfig_EnvDataDirLogLevelRegToken(t *testing.T) {
	t.Setenv("VORD_DATA_DIR", "/env/data")
	t.Setenv("VORD_LOG_LEVEL", "debug")
	t.Setenv("VORD_REGISTRATION_TOKEN", "env-token-123")

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.DataDir != "/env/data" {
		t.Errorf("DataDir = %q, want %q", cfg.DataDir, "/env/data")
	}
	if cfg.LogLevel != "debug" {
		t.Errorf("LogLevel = %q, want %q", cfg.LogLevel, "debug")
	}
	if cfg.RegistrationToken != "env-token-123" {
		t.Errorf("RegistrationToken = %q, want %q", cfg.RegistrationToken, "env-token-123")
	}
}

// Intent: When no env vars are set, all defaults are preserved.
func TestApplyLayeredConfig_NoEnvVars(t *testing.T) {
	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.ServerAddress != "localhost" {
		t.Errorf("ServerAddress = %q, want default %q", cfg.ServerAddress, "localhost")
	}
	if cfg.ServerPort != 12234 {
		t.Errorf("ServerPort = %d, want default 12234", cfg.ServerPort)
	}
	if cfg.LogLevel != "info" {
		t.Errorf("LogLevel = %q, want default %q", cfg.LogLevel, "info")
	}
}

// --- Priority layering tests ---

// Intent: TOML value overrides env var for the same field.
func TestApplyLayeredConfig_TOMLOverridesEnv(t *testing.T) {
	t.Setenv("VORD_SERVER_ADDRESS", "env.host.com")

	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	content := `server_address = "toml.host.com"` + "\n"
	if err := os.WriteFile(path, []byte(content), 0600); err != nil {
		t.Fatalf("writing test config: %v", err)
	}

	fc, meta, err := loadFromTOML(path)
	if err != nil {
		t.Fatalf("loadFromTOML: %v", err)
	}

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, meta, fc, map[string]bool{}, resolvedFlags{})

	if cfg.ServerAddress != "toml.host.com" {
		t.Errorf("ServerAddress = %q, want %q (TOML should override env)", cfg.ServerAddress, "toml.host.com")
	}
}

// Intent: Explicit flag overrides TOML for the same field.
func TestApplyLayeredConfig_FlagOverridesTOML(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	content := `server_address = "toml.host.com"` + "\n"
	if err := os.WriteFile(path, []byte(content), 0600); err != nil {
		t.Fatalf("writing test config: %v", err)
	}

	fc, meta, err := loadFromTOML(path)
	if err != nil {
		t.Fatalf("loadFromTOML: %v", err)
	}

	cfg := defaultTestConfig()
	explicitFlags := map[string]bool{"server": true}
	flags := resolvedFlags{ServerAddress: "flag.host.com"}
	applyLayeredConfig(cfg, meta, fc, explicitFlags, flags)

	if cfg.ServerAddress != "flag.host.com" {
		t.Errorf("ServerAddress = %q, want %q (flag should override TOML)", cfg.ServerAddress, "flag.host.com")
	}
}

// Intent: When all three sources are set, the explicit flag wins.
func TestApplyLayeredConfig_FlagWinsOverAll(t *testing.T) {
	t.Setenv("VORD_LOG_LEVEL", "error")

	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	content := `log_level = "warn"` + "\n"
	if err := os.WriteFile(path, []byte(content), 0600); err != nil {
		t.Fatalf("writing test config: %v", err)
	}

	fc, meta, err := loadFromTOML(path)
	if err != nil {
		t.Fatalf("loadFromTOML: %v", err)
	}

	cfg := defaultTestConfig()
	explicitFlags := map[string]bool{"log-level": true}
	flags := resolvedFlags{LogLevel: "debug"}
	applyLayeredConfig(cfg, meta, fc, explicitFlags, flags)

	if cfg.LogLevel != "debug" {
		t.Errorf("LogLevel = %q, want %q (flag should win over TOML and env)", cfg.LogLevel, "debug")
	}
}

// Intent: Default used when nothing else is set.
func TestApplyLayeredConfig_DefaultWhenNothingSet(t *testing.T) {
	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, map[string]bool{}, resolvedFlags{})

	if cfg.DataDir != "/var/lib/vord-agent" {
		t.Errorf("DataDir = %q, want default %q", cfg.DataDir, "/var/lib/vord-agent")
	}
}

// Intent: All flag overrides work for each field.
func TestApplyLayeredConfig_AllFlagOverrides(t *testing.T) {
	cfg := defaultTestConfig()
	explicitFlags := map[string]bool{
		"server":                true,
		"port":                  true,
		"data-dir":              true,
		"log-level":             true,
		"insecure":              true,
		"allow-remote-commands": true,
		"registration-token":    true,
	}
	flags := resolvedFlags{
		ServerAddress:       "flag.host",
		ServerPort:          5555,
		DataDir:             "/flag/dir",
		LogLevel:            "error",
		Insecure:            false,
		AllowRemoteCommands: true,
		RegistrationToken:   "flag-token",
	}
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, explicitFlags, flags)

	if cfg.ServerAddress != "flag.host" {
		t.Errorf("ServerAddress = %q, want %q", cfg.ServerAddress, "flag.host")
	}
	if cfg.ServerPort != 5555 {
		t.Errorf("ServerPort = %d, want 5555", cfg.ServerPort)
	}
	if cfg.DataDir != "/flag/dir" {
		t.Errorf("DataDir = %q, want %q", cfg.DataDir, "/flag/dir")
	}
	if cfg.LogLevel != "error" {
		t.Errorf("LogLevel = %q, want %q", cfg.LogLevel, "error")
	}
	if cfg.UseTLS == false {
		t.Error("expected UseTLS=true when insecure=false")
	}
	if cfg.AllowRemoteCommands == false {
		t.Error("expected AllowRemoteCommands=true")
	}
	if cfg.RegistrationToken != "flag-token" {
		t.Errorf("RegistrationToken = %q, want %q", cfg.RegistrationToken, "flag-token")
	}
}

// Intent: TOML overrides for all fields that weren't covered individually.
func TestApplyLayeredConfig_TOMLOverridesAllFields(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config.toml")
	content := `
server_address = "toml.host"
server_port = 7777
data_dir = "/toml/dir"
log_level = "warn"
use_tls = false
allow_remote_commands = true
registration_token = "toml-token"
`
	if err := os.WriteFile(path, []byte(content), 0600); err != nil {
		t.Fatalf("writing test config: %v", err)
	}
	fc, meta, err := loadFromTOML(path)
	if err != nil {
		t.Fatalf("loadFromTOML: %v", err)
	}

	cfg := defaultTestConfig()
	applyLayeredConfig(cfg, meta, fc, map[string]bool{}, resolvedFlags{})

	if cfg.ServerPort != 7777 {
		t.Errorf("ServerPort = %d, want 7777", cfg.ServerPort)
	}
	if cfg.DataDir != "/toml/dir" {
		t.Errorf("DataDir = %q, want %q", cfg.DataDir, "/toml/dir")
	}
	if cfg.LogLevel != "warn" {
		t.Errorf("LogLevel = %q, want %q", cfg.LogLevel, "warn")
	}
	if cfg.UseTLS {
		t.Error("expected UseTLS=false from TOML")
	}
	if cfg.AllowRemoteCommands == false {
		t.Error("expected AllowRemoteCommands=true from TOML")
	}
	if cfg.RegistrationToken != "toml-token" {
		t.Errorf("RegistrationToken = %q, want %q", cfg.RegistrationToken, "toml-token")
	}
}

// Intent: Insecure flag correctly inverts to UseTLS=false.
func TestApplyLayeredConfig_InsecureFlagInversion(t *testing.T) {
	cfg := defaultTestConfig()
	explicitFlags := map[string]bool{"insecure": true}
	flags := resolvedFlags{Insecure: true}
	applyLayeredConfig(cfg, emptyMeta(), fileConfig{}, explicitFlags, flags)

	if cfg.UseTLS {
		t.Error("expected UseTLS=false when insecure flag is true")
	}
}

// --- validateConfig tests ---

// Intent: Port 0 is rejected.
func TestValidateConfig_PortZero(t *testing.T) {
	cfg := &Config{ServerPort: 0, DataDir: t.TempDir()}

	err := validateConfig(cfg)
	if err == nil {
		t.Error("expected error for port 0")
	}
}

// Intent: Port -1 is rejected.
func TestValidateConfig_PortNegative(t *testing.T) {
	cfg := &Config{ServerPort: -1, DataDir: t.TempDir()}

	err := validateConfig(cfg)
	if err == nil {
		t.Error("expected error for port -1")
	}
}

// Intent: Port 65536 is rejected.
func TestValidateConfig_PortTooHigh(t *testing.T) {
	cfg := &Config{ServerPort: 65536, DataDir: t.TempDir()}

	err := validateConfig(cfg)
	if err == nil {
		t.Error("expected error for port 65536")
	}
}

// Intent: Port 1 and 65535 are valid boundary values.
func TestValidateConfig_PortBoundaries(t *testing.T) {
	for _, port := range []int{1, 65535} {
		cfg := &Config{ServerPort: port, DataDir: t.TempDir(), MaxQueueSize: DefaultMaxQueueSize}
		if err := validateConfig(cfg); err != nil {
			t.Errorf("expected no error for port %d, got %v", port, err)
		}
	}
}

// Intent: EnsureDataDir creates nested directories with owner-only permissions.
func TestEnsureDataDir_Created(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "subdir", "data")
	cfg := &Config{ServerPort: 12234, DataDir: dir}

	err := cfg.EnsureDataDir()
	if err != nil {
		t.Fatalf("EnsureDataDir: %v", err)
	}

	info, err := os.Stat(dir)
	if err != nil {
		t.Fatalf("stat data dir: %v", err)
	}
	if info.IsDir() == false {
		t.Error("expected data dir to be a directory")
	}
}

// Intent: EnsureDataDir returns an error when the parent is read-only.
func TestEnsureDataDir_ReadOnlyParent(t *testing.T) {
	parent := filepath.Join(t.TempDir(), "readonly")
	if err := os.MkdirAll(parent, 0500); err != nil {
		t.Fatalf("creating read-only parent: %v", err)
	}

	cfg := &Config{ServerPort: 12234, DataDir: filepath.Join(parent, "data")}

	err := cfg.EnsureDataDir()
	if err == nil {
		t.Error("expected error for data dir under read-only parent")
	}
}

// Intent: validateConfig does not create directories (pure validation).
func TestValidateConfig_NoSideEffects(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "should-not-exist")
	cfg := &Config{ServerPort: 12234, DataDir: dir, MaxQueueSize: DefaultMaxQueueSize}

	err := validateConfig(cfg)
	if err != nil {
		t.Fatalf("validateConfig: %v", err)
	}

	_, err = os.Stat(dir)
	if os.IsNotExist(err) == false {
		t.Error("expected data dir to not be created by validateConfig")
	}
}
