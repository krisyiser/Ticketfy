import paramiko
import os
import sys
import datetime
import re

sys.stdout.reconfigure(encoding='utf-8')

REMOTE_PUBLISHING_ENABLED = False

if not REMOTE_PUBLISHING_ENABLED:
    raise SystemExit(
        "Remote deployment is disabled while the server is under maintenance. "
        "Use build_release.ps1 for local artifacts."
    )

def safe_put(sftp, local_path, remote_path):
    try:
        tmp_path = remote_path + ".tmp"
        try:
            sftp.remove(tmp_path)
        except IOError:
            pass
        sftp.put(local_path, tmp_path)
        try:
            sftp.remove(remote_path)
        except IOError:
            pass
        sftp.rename(tmp_path, remote_path)
        print(f"  -> Atomic Upload Completed: {remote_path}")
    except Exception as ex:
        print(f"  -> Warning uploading {remote_path}: {ex}")

def deploy_release(version):
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Version must use the numeric major.minor.patch format")

    host = "100.109.190.105"
    user = "yersi"
    pwd = os.environ.get("TICKETFY_DEPLOY_PASSWORD")

    print(f"[AUTO-DEPLOY] Connecting to Valcore server ({host}) for version v{version}...")
    ssh = paramiko.SSHClient()
    ssh.load_system_host_keys()
    ssh.set_missing_host_key_policy(paramiko.RejectPolicy())
    ssh.connect(host, username=user, password=pwd)
    sftp = ssh.open_sftp()

    base_dir = os.path.dirname(os.path.abspath(__file__))
    releases_dir = os.path.join(base_dir, "Output", "Releases")
    if not os.path.exists(releases_dir):
        releases_dir = os.path.join(base_dir, "..", "Releases")

    remote_public = "/opt/valcore/valcore-site/public/downloads"

    # Ensure remote directory exists
    try:
        sftp.stat(remote_public)
    except IOError:
        sftp.mkdir(remote_public)

    print("[AUTO-DEPLOY] Surgical upload of release binaries and manifests...")

    # 1. Main Inno Setup Installers & ZIP
    x64_setup = os.path.join(releases_dir, f"Ticketfy-Setup-v{version}-x64.exe")
    if not os.path.exists(x64_setup):
        x64_setup = os.path.join(releases_dir, "Ticketfy-Setup-v" + version + ".exe")

    if os.path.exists(x64_setup):
        target_path = f"{remote_public}/Ticketfy-Setup-v{version}-x64.exe"
        safe_put(sftp, x64_setup, target_path)
        ssh.exec_command(f"cp -f '{target_path}' '{remote_public}/Ticketfy-Setup-v{version}.exe'")
        ssh.exec_command(f"cp -f '{target_path}' '{remote_public}/Ticketfy-Setup-Latest.exe'")
        print("  -> Fast server-side alias copies created successfully.")

    x86_setup = os.path.join(releases_dir, f"Ticketfy-Setup-v{version}-x86.exe")
    if os.path.exists(x86_setup):
        safe_put(sftp, x86_setup, f"{remote_public}/Ticketfy-Setup-v{version}-x86.exe")

    zip_setup = os.path.join(releases_dir, f"Ticketfy-Instalador-v{version}-x64.zip")
    if os.path.exists(zip_setup):
        safe_put(sftp, zip_setup, f"{remote_public}/Ticketfy-Instalador-v{version}-x64.zip")

    # 2. Upload primary Velopack OTA manifests if present
    x64_releases_dir = os.path.join(releases_dir, "x64")
    if os.path.exists(x64_releases_dir):
        for f in os.listdir(x64_releases_dir):
            if f in ["RELEASES", "releases.win.json", "assets.win.json"] or f.endswith(".nupkg"):
                local_f = os.path.join(x64_releases_dir, f)
                if os.path.exists(local_f):
                    safe_put(sftp, local_f, f"{remote_public}/{f}")

    # 3. Write and upload releases.json web manifest
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

    local_releases_json_path = os.path.join(releases_dir, "releases.json")
    with open(local_releases_json_path, "w", encoding="utf-8") as f:
        f.write(releases_json)

    safe_put(sftp, local_releases_json_path, f"{remote_public}/releases.json")
    print(f"  -> Successfully updated {remote_public}/releases.json to version {version}")

    sftp.close()

    # 4. Trigger remote deployment hook
    print(f"[AUTO-DEPLOY] Triggering remote deployment script for v{version}...")
    try:
        cmd = f"nohup /opt/valcore/auto_deploy_release.sh {version} > /dev/null 2>&1 &"
        ssh.exec_command(cmd, timeout=5)
    except Exception as exHook:
        print(f"  -> Remote hook triggered (non-blocking): {exHook}")

    ssh.close()
    print("[AUTO-DEPLOY] Release deployment finished successfully.")

if __name__ == "__main__":
    ver = sys.argv[1] if len(sys.argv) > 1 else "3.1.56"
    deploy_release(ver)
