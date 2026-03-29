// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package config

import (
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strconv"

	"github.com/BurntSushi/toml"
)

// Config holds all agent configuration values.
type Config struct {
	ServerAddress       string
	ServerPort          int
	DataDir             string
	LogLevel            string
	UseTLS              bool
	AllowRemoteCommands bool
	RegistrationToken   string
}

// fileConfig mirrors Config for TOML deserialization.
type fileConfig struct {
	ServerAddress       string `toml:"server_address"`
	ServerPort          int    `toml:"server_port"`
	DataDir             string `toml:"data_dir"`
	LogLevel            string `toml:"log_level"`
	UseTLS              bool   `toml:"use_tls"`
	AllowRemoteCommands bool   `toml:"allow_remote_commands"`
	RegistrationToken   string `toml:"registration_token"`
}

// DatabasePath returns the full path to the SQLite database file.
func (c *Config) DatabasePath() string {
	return filepath.Join(c.DataDir, "agent.db")
}

// Load parses configuration from CLI flags, a TOML config file, environment variables, and defaults.
// Priority: flags > config file > env vars > defaults.
func Load() (*Config, error) {
	// Defaults.
	cfg := &Config{
		ServerAddress:       "localhost",
		ServerPort:          12234,
		DataDir:             "/var/lib/vord-agent",
		LogLevel:            "info",
		UseTLS:              true,
		AllowRemoteCommands: false,
	}

	// Define flags.
	configPath := flag.String("config", "/etc/framlux/vord-agent.toml", "Path to TOML config file")
	flagServer := flag.String("server", cfg.ServerAddress, "Server address")
	flagPort := flag.Int("port", cfg.ServerPort, "Server gRPC port")
	flagDataDir := flag.String("data-dir", cfg.DataDir, "Data directory for SQLite and state")
	flagLogLevel := flag.String("log-level", cfg.LogLevel, "Log level (debug, info, warn, error)")
	flagInsecure := flag.Bool("insecure", false, "Disable TLS for gRPC connection (development only)")
	flagAllowRemoteCommands := flag.Bool("allow-remote-commands", cfg.AllowRemoteCommands, "Allow remote command execution from the server")
	flagRegistrationToken := flag.String("registration-token", "", "Registration token for tenant association")
	flag.Parse()

	// Record which flags were explicitly set on the CLI.
	explicitFlags := map[string]bool{}
	flag.Visit(func(f *flag.Flag) {
		explicitFlags[f.Name] = true
	})

	// Load TOML config file (silently skip if not found).
	fc, meta, err := loadFromTOML(*configPath)
	if err != nil {
		return nil, err
	}

	// Build the resolved flag values for applyEnvVars.
	flagValues := resolvedFlags{
		ServerAddress:       *flagServer,
		ServerPort:          *flagPort,
		DataDir:             *flagDataDir,
		LogLevel:            *flagLogLevel,
		Insecure:            *flagInsecure,
		AllowRemoteCommands: *flagAllowRemoteCommands,
		RegistrationToken:   *flagRegistrationToken,
	}

	applyLayeredConfig(cfg, meta, fc, explicitFlags, flagValues)

	if err := validateConfig(cfg); err != nil {
		return nil, err
	}

	return cfg, nil
}

// resolvedFlags holds the values parsed from CLI flags for use in layered resolution.
type resolvedFlags struct {
	ServerAddress       string
	ServerPort          int
	DataDir             string
	LogLevel            string
	Insecure            bool
	AllowRemoteCommands bool
	RegistrationToken   string
}

// loadFromTOML loads and decodes a TOML config file. If the file does not exist, it returns
// a zero-value fileConfig and metadata without error.
func loadFromTOML(path string) (fileConfig, toml.MetaData, error) {
	var fc fileConfig
	meta, err := toml.DecodeFile(path, &fc)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) == false {
			return fc, meta, fmt.Errorf("reading config file %s: %w", path, err)
		}
		// File doesn't exist — return zero values without error.
	}

	return fc, meta, nil
}

// applyLayeredConfig resolves each config field using the priority:
// explicit flag > TOML file > environment variable > default (already set in cfg).
func applyLayeredConfig(cfg *Config, meta toml.MetaData, fc fileConfig, explicitFlags map[string]bool, flags resolvedFlags) {
	if explicitFlags["server"] {
		cfg.ServerAddress = flags.ServerAddress
	} else if meta.IsDefined("server_address") {
		cfg.ServerAddress = fc.ServerAddress
	} else if v := os.Getenv("VORD_SERVER_ADDRESS"); v != "" {
		cfg.ServerAddress = v
	}

	if explicitFlags["port"] {
		cfg.ServerPort = flags.ServerPort
	} else if meta.IsDefined("server_port") {
		cfg.ServerPort = fc.ServerPort
	} else if v := os.Getenv("VORD_SERVER_PORT"); v != "" {
		if i, err := strconv.Atoi(v); err == nil {
			cfg.ServerPort = i
		}
	}

	if explicitFlags["data-dir"] {
		cfg.DataDir = flags.DataDir
	} else if meta.IsDefined("data_dir") {
		cfg.DataDir = fc.DataDir
	} else if v := os.Getenv("VORD_DATA_DIR"); v != "" {
		cfg.DataDir = v
	}

	if explicitFlags["log-level"] {
		cfg.LogLevel = flags.LogLevel
	} else if meta.IsDefined("log_level") {
		cfg.LogLevel = fc.LogLevel
	} else if v := os.Getenv("VORD_LOG_LEVEL"); v != "" {
		cfg.LogLevel = v
	}

	if explicitFlags["insecure"] {
		cfg.UseTLS = (flags.Insecure == false)
	} else if meta.IsDefined("use_tls") {
		cfg.UseTLS = fc.UseTLS
	} else if v := os.Getenv("VORD_USE_TLS"); v != "" {
		if b, err := strconv.ParseBool(v); err == nil {
			cfg.UseTLS = b
		}
	}

	if explicitFlags["allow-remote-commands"] {
		cfg.AllowRemoteCommands = flags.AllowRemoteCommands
	} else if meta.IsDefined("allow_remote_commands") {
		cfg.AllowRemoteCommands = fc.AllowRemoteCommands
	} else if v := os.Getenv("VORD_ALLOW_REMOTE_COMMANDS"); v != "" {
		if b, err := strconv.ParseBool(v); err == nil {
			cfg.AllowRemoteCommands = b
		}
	}

	if explicitFlags["registration-token"] {
		cfg.RegistrationToken = flags.RegistrationToken
	} else if meta.IsDefined("registration_token") {
		cfg.RegistrationToken = fc.RegistrationToken
	} else if v := os.Getenv("VORD_REGISTRATION_TOKEN"); v != "" {
		cfg.RegistrationToken = v
	}
}

// validateConfig checks that all configuration values are within valid ranges.
// It does not create directories or perform other side effects.
func validateConfig(cfg *Config) error {
	if cfg.ServerPort < 1 || cfg.ServerPort > 65535 {
		return fmt.Errorf("invalid server port %d: must be between 1 and 65535", cfg.ServerPort)
	}

	return nil
}

// EnsureDataDir creates the data directory with owner-only permissions.
func (c *Config) EnsureDataDir() error {
	if err := os.MkdirAll(c.DataDir, 0700); err != nil {
		return fmt.Errorf("creating data directory %s: %w", c.DataDir, err)
	}

	return nil
}

// GRPCTarget returns the dial target string for gRPC.
func (c *Config) GRPCTarget() string {
	return fmt.Sprintf("%s:%d", c.ServerAddress, c.ServerPort)
}
