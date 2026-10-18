import paramiko
import datetime
import os
import re

REMOTE_PUBLISHING_ENABLED = False

if not REMOTE_PUBLISHING_ENABLED:
    raise SystemExit(
        "Remote deployment is disabled while the server is under maintenance."
    )

version = "3.1.56"
host = "100.109.190.105"
user = "yersi"
pwd = os.environ.get("TICKETFY_DEPLOY_PASSWORD")

if not re.fullmatch(r"\d+\.\d+\.\d+", version):
    raise ValueError("Version must use the numeric major.minor.patch format")

ssh = paramiko.SSHClient()
ssh.load_system_host_keys()
ssh.set_missing_host_key_policy(paramiko.RejectPolicy())
ssh.connect(host, username=user, password=pwd)
sftp = ssh.open_sftp()

remote_public = "/opt/valcore/valcore-site/public/downloads"

# Create server-side copies for setup aliases
target_exe = f"{remote_public}/Ticketfy-Setup-v{version}-x64.exe"
ssh.exec_command(f"cp -f {target_exe} {remote_public}/Ticketfy-Setup-v{version}.exe")
ssh.exec_command(f"cp -f {target_exe} {remote_public}/Ticketfy-Setup-Latest.exe")
print("  -> Server-side setup copies generated!")

# Generate and upload releases.json manifest
now_utc = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
now_ts = int(datetime.datetime.now().timestamp())
releases_json = f'''{{
  "version": "{version}",
  "updated_at": "{now_utc}",
  "downloads": {{
    "x64": "/downloads/Ticketfy-Setup-v{version}-x64.exe?v={version}&t={now_ts}",
    "exe": "/downloads/Ticketfy-Setup-v{version}-x64.exe?v={version}&t={now_ts}",
    "x86": "/downloads/Ticketfy-Setup-v{version}-x86.exe?v={version}&t={now_ts}",
    "zip": "/downloads/Ticketfy-Instalador-v{version}-x64.zip?v={version}&t={now_ts}",
    "default": "/downloads/Ticketfy-Setup-v{version}-x64.exe?v={version}&t={now_ts}"
  }}
}}'''

local_manifest = "Output/Releases/releases.json"
with open(local_manifest, "w", encoding="utf-8") as f:
    f.write(releases_json)

sftp.put(local_manifest, f"{remote_public}/releases.json")
print("  -> releases.json updated on server!")

sftp.close()
ssh.exec_command(f"nohup /opt/valcore/auto_deploy_release.sh {version} > /dev/null 2>&1 &")
ssh.close()
print("[AUTO-DEPLOY] Release deployment finished successfully!")
