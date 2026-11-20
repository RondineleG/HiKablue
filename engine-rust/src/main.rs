// coverage: lab-only begin — process bootstrap glue; exercised end-to-end
// by the lab demos, not unit tests.
mod cli;
mod crypto;
mod forward;
mod metrics;
mod packet;
mod tun;
mod wire;

use clap::Parser;

use cli::{Cli, Mode};
use forward::Config;

fn parse_key_arg(hex: Option<String>) -> Option<[u8; 32]> {
    hex.map(|k| match cli::parse_key_hex(&k) {
        Ok(key) => key,
        Err(err) => {
            eprintln!("vpn-engine: invalid --key: {err}");
            std::process::exit(2);
        }
    })
}

#[tokio::main]
async fn main() {
    let cli = Cli::parse();
    let config = match cli.mode {
        Mode::Server(args) => Config {
            bind: args.bind,
            remote: None,
            peer_vip: Some(args.peer),
            tun_cidr: args.tun_cidr,
            tun_name: args.common.tun_name,
            mtu: args.common.mtu,
            metrics_sock: args.common.metrics_sock,
            metrics_group: args.common.metrics_group,
            key: parse_key_arg(args.common.key),
            sender_index: args.common.sender_index,
            peer_index: args.common.peer_index,
            keepalive_interval: std::time::Duration::from_secs(args.common.keepalive_secs),
        },
        Mode::Client(args) => Config {
            bind: args.bind,
            remote: Some(args.remote),
            peer_vip: Some(args.peer),
            tun_cidr: args.tun_cidr,
            tun_name: args.common.tun_name,
            mtu: args.common.mtu,
            metrics_sock: args.common.metrics_sock,
            metrics_group: args.common.metrics_group,
            key: parse_key_arg(args.common.key),
            sender_index: args.common.sender_index,
            peer_index: args.common.peer_index,
            keepalive_interval: std::time::Duration::from_secs(args.common.keepalive_secs),
        },
    };

    if let Err(err) = forward::run(config).await {
        eprintln!("vpn-engine: {err}");
        std::process::exit(1);
    }
}
// coverage: lab-only end
