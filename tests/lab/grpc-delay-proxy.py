#!/usr/bin/env python3
"""Small TCP proxy used only by the isolated milestone-4 lab.

It delays every forwarded chunk slightly so the harness can reliably close
the proxy while a RevokePeer command/ACK is in flight. It is not a gRPC
implementation and does not inspect plaintext or TLS bytes.
"""
import os
import select
import socket
import sys
import time


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: grpc-delay-proxy.py <listen-port> <target-port> <delay-ms>", file=sys.stderr)
        return 2
    listen_port = int(sys.argv[1])
    target_port = int(sys.argv[2])
    delay = int(sys.argv[3]) / 1000.0

    listener = socket.socket()
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", listen_port))
    listener.listen(16)
    sockets = {listener}
    peers = {}
    queues = {}

    def drop(sock):
        peer = peers.pop(sock, None)
        queues.pop(sock, None)
        sockets.discard(sock)
        try:
            sock.close()
        except OSError:
            pass
        if peer is not None:
            peers.pop(peer, None)
            queues.pop(peer, None)
            sockets.discard(peer)
            try:
                peer.close()
            except OSError:
                pass

    while True:
        now = time.monotonic()
        for sock, items in list(queues.items()):
            while items and items[0][0] <= now:
                _, data = items.pop(0)
                try:
                    sock.sendall(data)
                except OSError:
                    drop(sock)
                    break
        readable, _, _ = select.select(list(sockets), [], [], 0.05)
        for sock in readable:
            if sock is listener:
                client, _ = listener.accept()
                try:
                    upstream = socket.create_connection(("127.0.0.1", target_port))
                except OSError:
                    client.close()
                    continue
                sockets.add(client)
                sockets.add(upstream)
                peers[client] = upstream
                peers[upstream] = client
                queues[client] = []
                queues[upstream] = []
                continue
            try:
                data = sock.recv(65536)
            except OSError:
                drop(sock)
                continue
            if not data:
                drop(sock)
                continue
            peer = peers.get(sock)
            if peer is not None:
                queues.setdefault(peer, []).append((time.monotonic() + delay, data))


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
