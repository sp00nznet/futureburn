#!/usr/bin/env python3
"""Install .NET 8 SDK machine-wide on the Windows GitLab runner VM.

Runs via Proxmox `qm guest exec` so it works without an interactive RDP
session. We download the official Microsoft SDK installer (always
machine-wide) instead of using `winget`, because winget under the
qemu-guest-agent SYSTEM context often fails for lack of a user profile.

After install, restart the gitlab-runner service so it re-reads PATH and
inherits the new `C:\\Program Files\\dotnet\\` entry.

Configuration — pass via environment variables (PROXMOX_PASS is required):

    PROXMOX_HOST   default 192.168.100.23
    PROXMOX_USER   default root
    PROXMOX_PASS   (required, no default)
    PROXMOX_VMID   default 100 (the WINDOWS-A94GGMF runner)

PowerShell example:

    $env:PROXMOX_PASS = '<root password>'
    python scripts/install_dotnet8_windows_runner.py
"""
import warnings
warnings.filterwarnings('ignore')

import base64
import os
import sys
import paramiko


PROXMOX_HOST = os.environ.get('PROXMOX_HOST', '192.168.100.23')
PROXMOX_USER = os.environ.get('PROXMOX_USER', 'root')
PROXMOX_PASS = os.environ.get('PROXMOX_PASS')
VMID = int(os.environ.get('PROXMOX_VMID', '100'))

if not PROXMOX_PASS:
    sys.exit(
        'PROXMOX_PASS environment variable not set.\n'
        '  PowerShell:  $env:PROXMOX_PASS = "<root password>"\n'
        '  bash/zsh:    export PROXMOX_PASS="<root password>"\n'
        'Then re-run this script.'
    )


def run(ssh, cmd, timeout=120, label=None):
    if label:
        print(f'--- {label} ---')
    stdin, stdout, stderr = ssh.exec_command(cmd, timeout=timeout)
    rc = stdout.channel.recv_exit_status()
    out = stdout.read().decode('utf-8', errors='replace').rstrip()
    err = stderr.read().decode('utf-8', errors='replace').rstrip()
    if out:
        print(out)
    if err:
        print(f'[stderr] {err}')
    print(f'(rc={rc})')
    return rc, out, err


def guest_powershell(ssh, ps_script, timeout=900, label=None):
    """Run a PowerShell script inside the guest VM via qemu-guest-agent.

    We base64-encode the script so quoting/escaping doesn't bite us.
    """
    encoded = base64.b64encode(ps_script.encode('utf-16-le')).decode('ascii')
    cmd = (
        f'qm guest exec {VMID} --timeout {timeout} -- '
        f'powershell -NoProfile -NonInteractive -EncodedCommand {encoded}'
    )
    return run(ssh, cmd, timeout=timeout + 30, label=label)


def main():
    print(f'=== Connecting to Proxmox {PROXMOX_HOST} (VM {VMID}) ===')
    p = paramiko.SSHClient()
    p.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    p.connect(PROXMOX_HOST, username=PROXMOX_USER, password=PROXMOX_PASS,
              timeout=30, look_for_keys=False, allow_agent=False)

    # 0. Sanity check the VM is running.
    run(p, f'qm status {VMID}', label=f'VM {VMID} status')

    # 1. Show current dotnet state (if any) before we install.
    pre_check = r'''
$ErrorActionPreference = 'Continue'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (Test-Path $dotnet) {
    Write-Output "dotnet already at $dotnet"
    & $dotnet --list-sdks
} else {
    Write-Output "dotnet not installed at $dotnet"
}
Get-Service gitlab-runner | Format-Table -AutoSize
'''
    guest_powershell(p, pre_check, timeout=60, label='Pre-install state')

    # 2. Download and run the installer silently. /install /quiet /norestart is
    #    the standard "no UI, no reboot" combo for the .NET SDK installer.
    install = r'''
$ErrorActionPreference = 'Stop'
$url = 'https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe'
$out = 'C:\Windows\Temp\dotnet-sdk-8-installer.exe'
Write-Output "Downloading $url ..."
# TLS 1.2 needed for older PS versions; harmless on newer ones.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
Write-Output ("Downloaded {0:N1} MB" -f ((Get-Item $out).Length / 1MB))
Write-Output "Running installer (silent)..."
$proc = Start-Process -FilePath $out -ArgumentList '/install','/quiet','/norestart' -Wait -PassThru -NoNewWindow
Write-Output "Installer exit code: $($proc.ExitCode)"
if ($proc.ExitCode -notin 0,3010,1641) {
    throw "Installer failed with exit code $($proc.ExitCode)"
}
'''
    guest_powershell(p, install, timeout=900, label='Install .NET 8 SDK')

    # 3. Restart the runner service so it picks up the new machine PATH.
    restart = r'''
$ErrorActionPreference = 'Continue'
Restart-Service gitlab-runner -Force
Start-Sleep -Seconds 3
Get-Service gitlab-runner | Format-Table -AutoSize
'''
    guest_powershell(p, restart, timeout=60, label='Restart gitlab-runner')

    # 4. Verify dotnet is on the system PATH and the SDK is detected.
    verify = r'''
$ErrorActionPreference = 'Continue'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    Write-Error "dotnet.exe missing at $dotnet after install"
    exit 1
}
& $dotnet --info
Write-Output "--- machine PATH contains dotnet? ---"
$machinePath = [Environment]::GetEnvironmentVariable('PATH','Machine')
if ($machinePath -match [regex]::Escape('Program Files\dotnet')) {
    Write-Output "YES (machine PATH includes Program Files\dotnet)"
} else {
    Write-Output "NO -- machine PATH missing; adding it now"
    [Environment]::SetEnvironmentVariable('PATH', $machinePath + ';C:\Program Files\dotnet', 'Machine')
    Restart-Service gitlab-runner -Force
}
'''
    guest_powershell(p, verify, timeout=120, label='Verify install')

    p.close()
    print('\n=== Done ===')
    print('Re-run the failed CI pipeline (Retry in GitLab UI) to confirm.')


if __name__ == '__main__':
    main()
