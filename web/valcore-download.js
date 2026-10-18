/**
 * Valcore Desktop Download Architecture & Version Detector v1.2
 * Protocol Valcore v4.0 - Force Realtime Release Sync for valcore.cloud
 */
(function () {
  'use strict';

  async function fetchLatestReleaseInfo() {
    try {
      const timestamp = new Date().getTime();
      const res = await fetch(`https://raw.githubusercontent.com/krisyiser/NextVent/main/releases.json?t=${timestamp}`, { cache: 'no-store' });
      if (res.ok) {
        const data = await res.json();
        return data;
      }
    } catch (e) {
      console.warn('Valcore Release Sync: Fallback to direct raw endpoint query', e);
    }
    return {
      version: '3.1.56',
      downloads: {
        x64: 'https://raw.githubusercontent.com/krisyiser/NextVent/main/releases.json',
        x86: 'https://raw.githubusercontent.com/krisyiser/NextVent/main/releases.json',
        default: 'https://raw.githubusercontent.com/krisyiser/NextVent/main/releases.json'
      }
    };
  }

  async function detectWindowsArchitecture() {
    let is64Bit = true;
    try {
      if (navigator.userAgentData && typeof navigator.userAgentData.getHighEntropyValues === 'function') {
        const uaValues = await navigator.userAgentData.getHighEntropyValues(['architecture', 'bitness']);
        if (uaValues.bitness === '64' || uaValues.architecture === 'x86_64' || uaValues.architecture === 'arm64') {
          is64Bit = true;
        } else if (uaValues.bitness === '32' || uaValues.architecture === 'x86') {
          is64Bit = false;
        }
      } else {
        const ua = (navigator.userAgent || '').toLowerCase();
        const platform = (navigator.platform || '').toLowerCase();
        if (ua.includes('win64') || ua.includes('x64') || ua.includes('wow64') || platform.includes('win64') || platform.includes('x64')) {
          is64Bit = true;
        } else if (ua.includes('win32') || ua.includes('x86') || platform === 'win32') {
          is64Bit = false;
        }
      }
    } catch (e) {
      console.warn('Valcore Detector: userAgentData query failed, using x64', e);
    }
    return is64Bit ? 'x64' : 'x86';
  }

  async function syncValcoreWebsite() {
    const release = await fetchLatestReleaseInfo();
    const arch = await detectWindowsArchitecture();
    const versionStr = release.version || '3.1.56';

    const filename = release.downloads && release.downloads[arch] ? release.downloads[arch] : `https://git.valcore/yersi/ticketfy-releases/raw/branch/main/Ticketfy-Setup-v${versionStr}-${arch}.exe`;
    const downloadUrl = filename.startsWith('http') ? filename : `https://git.valcore/yersi/ticketfy-releases/raw/branch/main/${filename}`;

    const allElements = document.querySelectorAll('a, button, div, span');
    
    allElements.forEach(el => {
      const text = el.innerText || '';
      if (text.includes('DESCARGAR YA') || text.includes('V3.0.') || text.includes('v3.0.')) {
        if (el.tagName === 'A') {
          el.href = downloadUrl;
        } else {
          const parentLink = el.closest('a');
          if (parentLink) parentLink.href = downloadUrl;
        }
        
        if (!el.children.length || el.children.length === 1) {
          el.innerHTML = `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" style="vertical-align:middle;margin-right:6px;"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path><polyline points="7 10 12 15 17 10"></polyline><line x1="12" y1="15" x2="12" y2="3"></line></svg> DESCARGAR YA (V${versionStr})`;
        }
      }
    });

    const badgeText = document.getElementById('valcore-arch-badge');
    if (badgeText) {
      badgeText.textContent = `✓ Detectado: Windows ${arch === 'x64' ? '64-bit (x64)' : '32-bit (x86)'} — Instalador v${versionStr} 100% Compatible`;
      badgeText.className = `arch-badge badge-${arch}`;
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', syncValcoreWebsite);
  } else {
    syncValcoreWebsite();
  }

  window.ValcoreDownload = {
    syncValcoreWebsite,
    detectWindowsArchitecture
  };
})();
