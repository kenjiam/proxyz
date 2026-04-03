#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]

use std::env;

use anyhow::Result;
use clap::{Args, Parser, Subcommand, ValueEnum};
use log::info;

use proxyz::application::services::ApplyOptions;
use proxyz::bootstrap::create_app;
use proxyz::domain::models::{AppState, ProxyProfile, ServiceStartup, ServiceState};
use proxyz::gui;
use proxyz::service;

#[derive(Parser)]
#[command(
    name = "proxyz",
    version,
    about = "Cross-platform proxy profile manager"
)]
struct Cli {
    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand)]
enum Commands {
    Set(SetArgs),
    List(ListArgs),
    Delete(ProfileRef),
    SaveCurrent(SaveCurrentArgs),
    Service {
        #[command(subcommand)]
        command: ServiceCommand,
    },
    Gui,
    #[command(hide = true)]
    Internal {
        #[command(subcommand)]
        command: InternalCommand,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, ValueEnum)]
enum ServiceStartupArg {
    Automatic,
    Manual,
    Disabled,
}

impl From<ServiceStartupArg> for ServiceStartup {
    fn from(value: ServiceStartupArg) -> Self {
        match value {
            ServiceStartupArg::Automatic => ServiceStartup::Automatic,
            ServiceStartupArg::Manual => ServiceStartup::Manual,
            ServiceStartupArg::Disabled => ServiceStartup::Disabled,
        }
    }
}

#[derive(Args)]
struct SetArgs {
    profile: String,
    #[arg(
        long,
        help = "Allow commands marked as dangerous to run without confirmation"
    )]
    yes: bool,
    #[arg(long, help = "Preview the selected profile without applying anything")]
    dry_run: bool,
    #[arg(long, help = "Apply proxy settings but skip profile commands")]
    skip_commands: bool,
}

#[derive(Args)]
struct ListArgs {
    #[arg(long, help = "Print stored profiles as JSON")]
    json: bool,
}

#[derive(Args)]
struct ProfileRef {
    profile: String,
}

#[derive(Args)]
struct SaveCurrentArgs {
    profile: String,
    #[arg(long, default_value = "", help = "Optional memo for the saved profile")]
    memo: String,
}

#[derive(Subcommand)]
enum ServiceCommand {
    Start,
    Stop,
    Status,
}

#[derive(Subcommand)]
enum InternalCommand {
    RunService,
    ServiceAdmin {
        #[command(subcommand)]
        command: ServiceAdminCommand,
    },
}

#[derive(Subcommand)]
enum ServiceAdminCommand {
    Install {
        #[arg(long, value_enum, default_value = "automatic")]
        startup: ServiceStartupArg,
    },
    Uninstall,
    Start,
    Stop,
    SetStartup {
        #[arg(long, value_enum)]
        startup: ServiceStartupArg,
    },
}

fn main() -> Result<()> {
    if env::args_os().len() == 1 {
        return launch_default_entrypoint();
    }

    let cli = Cli::parse();
    let app = create_app()?;

    info!("proxyz started with {}", app.backend_summary());

    match cli.command {
        Commands::Set(args) => {
            let outcome = app.set_profile(
                &args.profile,
                &ApplyOptions {
                    assume_yes: args.yes,
                    dry_run: args.dry_run,
                    skip_commands: args.skip_commands,
                },
            )?;
            if args.dry_run {
                println!("Dry run complete for profile '{}'", outcome.profile_name);
            } else if outcome.command_reports.is_empty() {
                println!("Applied profile '{}'", outcome.profile_name);
            } else {
                println!("Applied profile '{}'", outcome.profile_name);
                for report in outcome.command_reports {
                    println!(
                        "  - {} {:?} => success={} exit_code={:?} summary={}",
                        report.program,
                        report.args,
                        report.success,
                        report.exit_code,
                        report.summary
                    );
                }
            }
        }
        Commands::List(args) => {
            let state = app.load_state()?;
            render_profiles(state, args.json)?;
        }
        Commands::Delete(profile) => {
            app.delete_profile(&profile.profile)?;
            println!("Deleted profile '{}'", profile.profile);
        }
        Commands::SaveCurrent(args) => {
            let profile = app.save_current_profile(
                &args.profile,
                (!args.memo.trim().is_empty()).then_some(args.memo),
            )?;
            println!("Saved current OS proxy settings as '{}'", profile.name);
        }
        Commands::Service { command } => match command {
            ServiceCommand::Start => {
                app.service_start()?;
                println!("Service start requested");
            }
            ServiceCommand::Stop => {
                app.service_stop()?;
                println!("Service stop requested");
            }
            ServiceCommand::Status => {
                let status = app.service_status()?;
                let state = match status.state {
                    ServiceState::Running => "running",
                    ServiceState::Stopped => "stopped",
                    ServiceState::Unknown => "unknown",
                };
                println!("Service status: {state}");
                println!("{}", status.detail);
            }
        },
        Commands::Gui => {
            gui::launch(app)?;
        }
        Commands::Internal { command } => match command {
            InternalCommand::RunService => {
                service::run_service_host(app)?;
            }
            InternalCommand::ServiceAdmin { command } => match command {
                ServiceAdminCommand::Install { startup } => {
                    app.service_install(startup.into())?;
                }
                ServiceAdminCommand::Uninstall => {
                    app.service_uninstall()?;
                }
                ServiceAdminCommand::Start => {
                    app.service_start()?;
                }
                ServiceAdminCommand::Stop => {
                    app.service_stop()?;
                }
                ServiceAdminCommand::SetStartup { startup } => {
                    app.service_set_startup(startup.into())?;
                }
            },
        },
    }

    Ok(())
}

fn render_profiles(state: AppState, as_json: bool) -> Result<()> {
    if as_json {
        println!("{}", serde_json::to_string_pretty(&state.profiles)?);
        return Ok(());
    }

    if state.profiles.is_empty() {
        println!("No profiles saved");
        return Ok(());
    }

    for ProxyProfile {
        name,
        proxy,
        memo,
        post_apply_commands,
    } in state.profiles
    {
        println!(
            "{} | enabled={} | http={} | https={} | bypass={} | commands={} | memo={}",
            name,
            proxy.enabled,
            proxy
                .http
                .or(proxy.all.clone())
                .unwrap_or_else(|| "-".to_string()),
            proxy
                .https
                .or(proxy.all.clone())
                .unwrap_or_else(|| "-".to_string()),
            if proxy.bypass.is_empty() {
                "-".to_string()
            } else {
                proxy.bypass.join(",")
            },
            post_apply_commands.len(),
            memo
        );
    }
    Ok(())
}

fn launch_default_entrypoint() -> Result<()> {
    let app = create_app()?;
    gui::launch(app)
}
