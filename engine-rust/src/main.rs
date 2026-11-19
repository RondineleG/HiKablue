mod cli;
mod forward;
mod packet;
mod tun;

use clap::Parser;

use cli::{Cli, Mode};
use forward::Config;

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
        },
        Mode::Client(args) => Config {
            bind: args.bind,
            remote: Some(args.remote),
            peer_vip: None,
            tun_cidr: args.tun_cidr,
            tun_name: args.common.tun_name,
            mtu: args.common.mtu,
        },
    };

    if let Err(err) = forward::run(config).await {
        eprintln!("vpn-engine: {err}");
        std::process::exit(1);
    }
}
