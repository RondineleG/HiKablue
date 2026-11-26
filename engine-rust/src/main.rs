// coverage: lab-only begin — process bootstrap glue; exercised end-to-end
// by the lab demos, not unit tests.
mod cli;
mod control;
mod crypto;
mod forward;
mod metrics;
mod packet;
mod sessions;
mod tun;
mod wire;

use clap::Parser;
use zeroize::Zeroizing;

use cli::{Cli, Mode};
use forward::Config;

fn parse_key_arg(
    hex: Option<String>,
    file: Option<std::path::PathBuf>,
    flag: &str,
) -> Option<Zeroizing<[u8; 32]>> {
    if let Some(path) = file {
        return match cli::parse_key_file(&path, flag) {
            Ok(key) => Some(key),
            Err(err) => {
                eprintln!("vpn-engine: invalid {err}");
                std::process::exit(2);
            }
        };
    }
    hex.map(|k| {
        eprintln!(
            "vpn-engine: warning: {flag} exposes key material in the process command line; prefer {flag}-file"
        );
        let result = cli::parse_key_hex(&Zeroizing::new(k));
        match result {
            Ok(key) => key,
            Err(err) => {
                eprintln!("vpn-engine: invalid {flag}: {err}");
                std::process::exit(2);
            }
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
            peer_vip: args.peer,
            tun_cidr: args.tun_cidr,
            tun_name: args.common.tun_name,
            mtu: args.common.mtu,
            metrics_sock: args.common.metrics_sock,
            metrics_group: args.common.metrics_group,
            control_sock: args.common.control_sock,
            control_uid: args.common.control_uid,
            control_group: args.common.control_group,
            key: parse_key_arg(args.common.key, args.common.key_file, "--key"),
            tx_key: parse_key_arg(args.common.tx_key, args.common.tx_key_file, "--tx-key"),
            rx_key: parse_key_arg(args.common.rx_key, args.common.rx_key_file, "--rx-key"),
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
            control_sock: args.common.control_sock,
            control_uid: args.common.control_uid,
            control_group: args.common.control_group,
            key: parse_key_arg(args.common.key, args.common.key_file, "--key"),
            tx_key: parse_key_arg(args.common.tx_key, args.common.tx_key_file, "--tx-key"),
            rx_key: parse_key_arg(args.common.rx_key, args.common.rx_key_file, "--rx-key"),
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
