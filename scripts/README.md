# scripts

Operations glue that lives outside the .NET solution — one-shot scripts for
keeping the build infrastructure healthy. Not part of the futureburn build,
not shipped with the app.

## `install_dotnet8_windows_runner.py`

Installs the .NET 8 SDK machine-wide on the Windows GitLab runner VM hosted
on Proxmox. Drives the VM via `qm guest exec` (qemu-guest-agent) so it works
without an interactive RDP session.

Why this exists: a per-user `winget install` puts `dotnet` on the installing
user's PATH but not on the gitlab-runner service account's PATH, so CI jobs
end up with `dotnet: command not found`. This script downloads the official
Microsoft SDK installer (always machine-wide), runs it silently, then
restarts the runner service so it inherits the new PATH.

### Requirements

- Python 3.8+ on the operator's machine.
- `paramiko` — install with `pip install paramiko`.
- SSH access to the Proxmox host.

### Run it

The Proxmox root password is passed via environment variable so it never
ends up in the repo. Defaults match the current homelab — override per
your setup.

```powershell
$env:PROXMOX_PASS = '<proxmox root password>'
python scripts/install_dotnet8_windows_runner.py
```

Optional overrides:

```powershell
$env:PROXMOX_HOST = '192.168.100.23'   # Proxmox host
$env:PROXMOX_USER = 'root'             # Proxmox user
$env:PROXMOX_VMID = '100'              # VM ID of the Windows runner
```

After it finishes, hit **Retry** on the failed pipeline in the GitLab UI.
