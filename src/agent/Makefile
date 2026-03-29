BINARY_NAME := vord-agent
VERSION := $(shell git describe --tags --always --dirty 2>/dev/null || echo "dev")
BUILD_TIME := $(shell date -u '+%Y-%m-%dT%H:%M:%SZ')
LDFLAGS := -s -w -X main.version=$(VERSION) -X main.buildTime=$(BUILD_TIME)

.PHONY: build build-linux-amd64 build-linux-arm64 build-linux-armv7 build-linux-386 build-all clean test proto fmt vet

build:
	go build -ldflags "$(LDFLAGS)" -o bin/$(BINARY_NAME) ./cmd/agent/

build-linux-amd64:
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -ldflags "$(LDFLAGS)" -o bin/$(BINARY_NAME)-linux-amd64 ./cmd/agent/

build-linux-arm64:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -ldflags "$(LDFLAGS)" -o bin/$(BINARY_NAME)-linux-arm64 ./cmd/agent/

build-linux-armv7:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 go build -ldflags "$(LDFLAGS)" -o bin/$(BINARY_NAME)-linux-armv7 ./cmd/agent/

build-linux-386:
	CGO_ENABLED=0 GOOS=linux GOARCH=386 go build -ldflags "$(LDFLAGS)" -o bin/$(BINARY_NAME)-linux-386 ./cmd/agent/

build-all: build-linux-amd64 build-linux-arm64 build-linux-armv7 build-linux-386

clean:
	rm -rf bin/

test:
	go test -v ./...

proto:
	protoc \
		--go_out=internal/proto/agent --go_opt=paths=source_relative \
		--go-grpc_out=internal/proto/agent --go-grpc_opt=paths=source_relative \
		-I ../grpc/protos \
		../grpc/protos/AgentRegistration.proto \
		../grpc/protos/AgentConfiguration.proto \
		../grpc/protos/AgentTelemetry.proto

fmt:
	go fmt ./...

vet:
	go vet ./...
