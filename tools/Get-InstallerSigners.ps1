<#
.SYNOPSIS
    Prints the code-signing certificate subject of each app's current installer.

.DESCRIPTION
    The app database (SimpleDeFence/Database/Applications) recognises a program by its file name
    and by the exact subject of the certificate it is signed with, compared as a string. That
    string is whatever .NET's X509Certificate.Subject returns on Windows, so it is easiest to read
    it on Windows.

    For a normal .exe installer only the PE header and the signature are downloaded, using HTTP
    range requests, which is a few kilobytes. The signature is decoded without checking the file's
    hash, because only the signer's name is needed. MSI installers, and servers that ignore range
    requests, are downloaded whole to %TEMP% and deleted afterwards.

    Nothing is installed or run.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Get-InstallerSigners.ps1
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.Security
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$installers = [ordered]@{
    'Discord'    = 'https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64'
    'Spotify'    = 'https://download.scdn.co/SpotifySetup.exe'
    'Zoom'       = 'https://zoom.us/client/latest/ZoomInstallerFull.exe?archType=x64'
    'Slack'      = 'https://slack.com/ssb/download-win64'
    'Brave'      = 'https://laptop-updates.brave.com/latest/winx64'
    'Opera'      = 'https://net.geo.opera.com/opera/stable/windows'
    'Dropbox'    = 'https://www.dropbox.com/download?plat=win&type=full'
    'Epic Games' = 'https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi'
    'Battle.net' = 'https://www.battle.net/download/getInstallerForGame?os=win&gameProgram=BATTLENET_APP&version=Live'
    'Signal'     = 'signal'
    'Telegram'   = 'https://telegram.org/dl/desktop/win64'
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $true
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromMinutes(10)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('Mozilla/5.0 (Windows NT 10.0; Win64; x64)')

function Get-Bytes([string]$url, [long]$from = -1, [long]$to = -1) {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $url)
    if ($from -ge 0) { $req.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue($from, $to) }
    $resp = $http.SendAsync($req).GetAwaiter().GetResult()
    [void]$resp.EnsureSuccessStatusCode()
    [pscustomobject]@{
        Bytes   = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        Partial = ($resp.StatusCode -eq [System.Net.HttpStatusCode]::PartialContent)
        Url     = $resp.RequestMessage.RequestUri.AbsoluteUri
    }
}

# Returns the signer's subject from a PE file's certificate table, given a way to read byte ranges.
function Get-PeSigner([scriptblock]$read) {
    [byte[]]$head = & $read 0 4095
    if ($head[0] -ne 0x4D -or $head[1] -ne 0x5A) { return $null }    # not "MZ"
    $pe = [BitConverter]::ToInt32($head, 0x3C)
    $opt = $pe + 24
    $magic = [BitConverter]::ToUInt16($head, $opt)
    $dirs = $opt + $(if ($magic -eq 0x20B) { 112 } else { 96 })
    $certOffset = [BitConverter]::ToUInt32($head, $dirs + 4 * 8)
    $certSize = [BitConverter]::ToUInt32($head, $dirs + 4 * 8 + 4)
    if ($certSize -le 8) { throw 'the file is not signed' }
    [byte[]]$table = & $read $certOffset ($certOffset + $certSize - 1)
    # WIN_CERTIFICATE: dwLength, wRevision, wCertificateType, then the PKCS#7 blob.
    $length = [BitConverter]::ToInt32($table, 0)
    $pkcs7 = New-Object byte[] ($length - 8)
    [Array]::Copy($table, 8, $pkcs7, 0, $length - 8)
    $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
    $cms.Decode($pkcs7)
    return $cms.SignerInfos[0].Certificate.Subject
}

function Get-Signer([string]$url) {
    $first = Get-Bytes $url 0 4095
    $url = $first.Url
    if ($first.Partial) {
        $subject = Get-PeSigner { param($a, $b) if ($a -eq 0) { $first.Bytes } else { (Get-Bytes $url $a $b).Bytes } }
        if ($subject) { return $subject }
    }
    # The server sent the whole file, or it isn't a PE (an MSI): check it on disk.
    # Assigned in each branch, not through "$bytes = if", which would unroll the array.
    if ($first.Partial) { $bytes = (Get-Bytes $url).Bytes } else { $bytes = $first.Bytes }
    $isMsi = $bytes.Length -gt 4 -and $bytes[0] -eq 0xD0 -and $bytes[1] -eq 0xCF -and $bytes[2] -eq 0x11 -and $bytes[3] -eq 0xE0
    $file = Join-Path $env:TEMP ('signer-check-' + [Guid]::NewGuid() + $(if ($isMsi) { '.msi' } else { '.exe' }))
    try {
        [IO.File]::WriteAllBytes($file, $bytes)
        $sig = Get-AuthenticodeSignature -LiteralPath $file
        if (-not $sig.SignerCertificate) { throw "no signature ($($sig.Status))" }
        return $sig.SignerCertificate.Subject
    }
    finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
}

$lines = foreach ($name in $installers.Keys) {
    $url = $installers[$name]
    try {
        if ($url -eq 'signal') {
            $yml = [Text.Encoding]::UTF8.GetString((Get-Bytes 'https://updates.signal.org/desktop/latest.yml').Bytes)
            $path = ([regex]'(?m)^path:\s*(\S+)').Match($yml).Groups[1].Value
            $url = 'https://updates.signal.org/desktop/' + $path
        }
        "$name`t$(Get-Signer $url)"
    }
    catch { "$name`tFAILED: $($_.Exception.Message)" }
}

$lines | ForEach-Object { Write-Host $_ }
try { $lines -join "`r`n" | Set-Clipboard; Write-Host "`nCopied to the clipboard." } catch { }
