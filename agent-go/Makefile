# agent-go build helpers. The Go toolchain is pinned to go1.18.x
# (agent-go/.go-version), so protoc-gen-go is pinned to v1.28.1 — the last
# release that still supports Go 1.18 runtimes.

PROTO_DIR   := ../proto
GEN_DIR     := gen/edge/v1
PROTOC      ?= protoc
PROTOC_GEN_GO     ?= protoc-gen-go
PROTOC_GEN_GO_GRPC ?= protoc-gen-go-grpc

.PHONY: proto test vet build

proto:
	PATH="$(HOME)/go/bin:$$PATH" $(PROTOC) \
		--go_out=$(GEN_DIR) --go_opt=paths=source_relative \
		--go-grpc_out=$(GEN_DIR) --go-grpc_opt=paths=source_relative \
		-I $(PROTO_DIR) $(PROTO_DIR)/edge_orchestrator.proto

test:
	go test ./...

vet:
	go vet ./...

build:
	go build ./...
