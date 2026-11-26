#!/usr/bin/env bash
# build-release.sh — build one versioned HiKablue Debian release tree.
# The output is input to deploy/debian/install.sh. It contains binaries,
# systemd units and sample configuration, never live secrets.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

force=0
out=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --force) force=1 ;;
        --output)
            [ "$#" -ge 2 ] || { echo "--output requires a directory" >&2; exit 2; }
            out=$2
            shift
            ;;
        *) echo "usage: $0 [--force] [--output DIR]" >&2; exit 2 ;;
    esac
    shift
done

short_sha=$(git rev-parse --short=12 HEAD 2>/dev/null || echo nogit)
dirty_suffix=""
if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
    dirty_suffix="-dirty"
fi
version=$(printf '%s' "$short_sha$dirty_suffix" | tr -cd 'A-Za-z0-9._-')
[ -n "$version" ] || { echo "empty release version" >&2; exit 1; }
if [ -z "$out" ]; then
    out="$repo_root/artifacts/release/hikablue-$version"
fi
if [ -e "$out" ]; then
    if [ "$force" -ne 1 ]; then
        echo "output already exists: $out (use --force to rebuild it)" >&2
        exit 1
    fi
    [ -f "$out/VERSION" ] || { echo "refusing to remove non-release directory: $out" >&2; exit 1; }
    rm -rf -- "$out"
fi
case "$out" in /*) ;; *) out="$PWD/$out" ;; esac
mkdir -p "$out/bin" "$out/control" "$out/systemd" "$out/config"

echo "== build rust engine"
(cd engine-rust && cargo build --release --locked)
cp engine-rust/target/release/vpn-engine "$out/bin/vpn-engine"

echo "== build go agent"
(cd agent-go && go build -o "$out/bin/vpn-agent" ./cmd/vpn-agent)

echo "== publish dotnet control plane"
export PATH="$HOME/.dotnet:$PATH"
dotnet publish control-dotnet/VpnControl/VpnControl.csproj \
    --configuration Release --no-self-contained \
    --output "$out/control" -v q --nologo >/dev/null

cp deploy/debian/service-scripts/hikablue-engine-service "$out/bin/"
cp deploy/debian/service-scripts/hikablue-agent-service "$out/bin/"
cp deploy/debian/service-scripts/hikablue-control-service "$out/bin/"
chmod 0755 "$out/bin/"*
cp deploy/debian/systemd/*.service "$out/systemd/"
cp deploy/debian/config/*.sample "$out/config/"

cat >"$out/VERSION" <<EOF
$version
EOF
cat >"$out/BUILD_INFO" <<EOF
version=$version
source_sha=$(git rev-parse HEAD 2>/dev/null || echo unknown)
built_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF

echo "release written: $out"
