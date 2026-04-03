use anyhow::{Context, Result, anyhow};

use crate::adapters::proxy::{ProxyBackend, normalize_proxy_endpoint};
use crate::domain::models::{AppSettings, ProxySettings};
use crate::process_utils::{configure_hidden, decode_output_text, hidden_command};

pub struct WindowsProxyBackend;

impl ProxyBackend for WindowsProxyBackend {
    fn current_settings(&self) -> Result<ProxySettings> {
        read_wininet_settings()
    }

    fn apply_settings(&self, settings: &ProxySettings, app_settings: &AppSettings) -> Result<()> {
        let targets = &app_settings.windows_proxy_targets;
        if !settings.enabled {
            return self.disable(app_settings);
        }

        let proxy_server = build_proxy_server(settings)?;
        let proxy_override = settings.bypass.join(";");

        if targets.wininet {
            apply_wininet_proxy(&proxy_server, &proxy_override)?;
        }
        if targets.winhttp {
            apply_winhttp_proxy(&proxy_server, &proxy_override)?;
        }
        if targets.env_vars {
            apply_environment_proxy(settings)?;
        }

        Ok(())
    }

    fn disable(&self, app_settings: &AppSettings) -> Result<()> {
        let targets = &app_settings.windows_proxy_targets;

        if targets.wininet {
            disable_wininet_proxy()?;
        }
        if targets.winhttp {
            disable_winhttp_proxy()?;
        }
        if targets.env_vars {
            disable_environment_proxy()?;
        }

        Ok(())
    }

    fn backend_name(&self) -> &'static str {
        "windows-registry+winhttp+env"
    }
}

fn read_wininet_settings() -> Result<ProxySettings> {
    let script = r#"
$settings = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$proxyEnable = if ($settings.ProxyEnable -eq 1) { 'true' } else { 'false' }
$proxyServer = [string]$settings.ProxyServer
$proxyOverride = [string]$settings.ProxyOverride
Write-Output ($proxyEnable + "`n" + $proxyServer + "`n" + $proxyOverride)
"#;
    let output = run_powershell(script)?;
    let mut lines = output.lines();
    let enabled = matches!(lines.next().unwrap_or_default().trim(), "true");
    let proxy_server = lines.next().unwrap_or_default().trim().to_string();
    let bypass = lines
        .next()
        .unwrap_or_default()
        .split(';')
        .map(str::trim)
        .filter(|item| !item.is_empty())
        .map(ToString::to_string)
        .collect::<Vec<_>>();

    let mut settings = ProxySettings {
        enabled,
        bypass,
        ..ProxySettings::default()
    };

    if proxy_server.contains("http=") || proxy_server.contains("https=") {
        for part in proxy_server.split(';') {
            if let Some(value) = part.strip_prefix("http=") {
                settings.http = Some(value.to_string());
            } else if let Some(value) = part.strip_prefix("https=") {
                settings.https = Some(value.to_string());
            }
        }
    } else if !proxy_server.is_empty() {
        settings.all = Some(proxy_server.clone());
        settings.http = Some(proxy_server.clone());
        settings.https = Some(proxy_server);
    }

    Ok(settings)
}

fn build_proxy_server(settings: &ProxySettings) -> Result<String> {
    let http = settings
        .http
        .clone()
        .or_else(|| settings.all.clone())
        .map(|value| normalize_proxy_endpoint(&value));
    let https = settings
        .https
        .clone()
        .or_else(|| settings.all.clone())
        .map(|value| normalize_proxy_endpoint(&value));

    match (http, https) {
        (Some(http), Some(https)) if http == https => Ok(http),
        (Some(http), Some(https)) => Ok(format!("http={http};https={https}")),
        (Some(http), None) => Ok(http),
        (None, Some(https)) => Ok(https),
        (None, None) => Err(anyhow!(
            "proxy is enabled but no HTTP/HTTPS/ALL endpoint is configured"
        )),
    }
}

fn apply_wininet_proxy(proxy_server: &str, proxy_override: &str) -> Result<()> {
    let script = format!(
        r#"
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name ProxyEnable -Value 1
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name ProxyServer -Value '{proxy_server}'
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name ProxyOverride -Value '{proxy_override}'
$signature = @"
using System;
using System.Runtime.InteropServices;
public static class NativeMethods {{
    [DllImport("wininet.dll", SetLastError=true)]
    public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}}
"@
Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue | Out-Null
[void][NativeMethods]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0)
[void][NativeMethods]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0)
"#
    );
    run_powershell(&script).context("failed to apply WinINet proxy settings")?;
    Ok(())
}

fn disable_wininet_proxy() -> Result<()> {
    let script = r#"
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -Name ProxyEnable -Value 0
$signature = @"
using System;
using System.Runtime.InteropServices;
public static class NativeMethods {
    [DllImport("wininet.dll", SetLastError=true)]
    public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
"@
Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue | Out-Null
[void][NativeMethods]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0)
[void][NativeMethods]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0)
"#;
    run_powershell(script).context("failed to disable WinINet proxy settings")?;
    Ok(())
}

fn apply_winhttp_proxy(proxy_server: &str, proxy_override: &str) -> Result<()> {
    let proxy_arg = format!("proxy-server={proxy_server}");
    let bypass_list = format!("bypass-list={proxy_override}");
    let mut args = vec!["winhttp", "set", "proxy", proxy_arg.as_str()];
    if !proxy_override.trim().is_empty() {
        args.push(bypass_list.as_str());
    }

    let output = hidden_command("netsh")
        .args(&args)
        .output()
        .context("failed to launch netsh for WinHTTP proxy update")?;
    if output.status.success() {
        return Ok(());
    }

    Err(anyhow!(
        "failed to apply WinHTTP proxy settings: {}",
        summarize_output(&output.stdout, &output.stderr)
    ))
}

fn disable_winhttp_proxy() -> Result<()> {
    let output = hidden_command("netsh")
        .args(["winhttp", "reset", "proxy"])
        .output()
        .context("failed to launch netsh for WinHTTP reset")?;
    if output.status.success() {
        return Ok(());
    }

    Err(anyhow!(
        "failed to disable WinHTTP proxy settings: {}",
        summarize_output(&output.stdout, &output.stderr)
    ))
}

fn apply_environment_proxy(settings: &ProxySettings) -> Result<()> {
    let http = settings
        .http
        .clone()
        .or_else(|| settings.all.clone())
        .map(|value| format!("http://{}", normalize_proxy_endpoint(&value)))
        .unwrap_or_default();
    let https = settings
        .https
        .clone()
        .or_else(|| settings.all.clone())
        .map(|value| format!("http://{}", normalize_proxy_endpoint(&value)))
        .unwrap_or_default();
    let all = settings
        .all
        .clone()
        .or_else(|| settings.http.clone())
        .map(|value| format!("http://{}", normalize_proxy_endpoint(&value)))
        .unwrap_or_default();
    let no_proxy = settings.bypass.join(",");

    let script = format!(
        r#"
[Environment]::SetEnvironmentVariable('HTTP_PROXY', '{http}', 'User')
[Environment]::SetEnvironmentVariable('HTTPS_PROXY', '{https}', 'User')
[Environment]::SetEnvironmentVariable('ALL_PROXY', '{all}', 'User')
[Environment]::SetEnvironmentVariable('NO_PROXY', '{no_proxy}', 'User')
$signature = @"
using System;
using System.Runtime.InteropServices;
public static class NativeMethods {{
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult
    );
}}
"@
Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue | Out-Null
[UIntPtr]$result = [UIntPtr]::Zero
[void][NativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, 'Environment', 0x0002, 5000, [ref]$result)
"#
    );

    run_powershell(&script).context("failed to apply user environment proxy settings")?;
    Ok(())
}

fn disable_environment_proxy() -> Result<()> {
    let script = r#"
[Environment]::SetEnvironmentVariable('HTTP_PROXY', $null, 'User')
[Environment]::SetEnvironmentVariable('HTTPS_PROXY', $null, 'User')
[Environment]::SetEnvironmentVariable('ALL_PROXY', $null, 'User')
[Environment]::SetEnvironmentVariable('NO_PROXY', $null, 'User')
$signature = @"
using System;
using System.Runtime.InteropServices;
public static class NativeMethods {
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult
    );
}
"@
Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue | Out-Null
[UIntPtr]$result = [UIntPtr]::Zero
[void][NativeMethods]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, 'Environment', 0x0002, 5000, [ref]$result)
"#;
    run_powershell(script).context("failed to clear user environment proxy settings")?;
    Ok(())
}

fn summarize_output(stdout: &[u8], stderr: &[u8]) -> String {
    let stdout = decode_output_text(stdout);
    let stderr = decode_output_text(stderr);
    let stdout = stdout.trim();
    let stderr = stderr.trim();
    match (stdout.is_empty(), stderr.is_empty()) {
        (false, false) => format!("{stdout}\n{stderr}"),
        (false, true) => stdout.to_string(),
        (true, false) => stderr.to_string(),
        (true, true) => "no output".to_string(),
    }
}

fn run_powershell(script: &str) -> Result<String> {
    let mut command = std::process::Command::new("powershell");
    configure_hidden(&mut command);
    let output = command
        .args(["-NoProfile", "-NonInteractive", "-Command", script])
        .output()
        .context("failed to launch PowerShell")?;

    if !output.status.success() {
        return Err(anyhow!(
            "PowerShell command failed: {}",
            decode_output_text(&output.stderr).trim()
        ));
    }

    Ok(decode_output_text(&output.stdout))
}
