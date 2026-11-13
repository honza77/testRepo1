<#
.SYNOPSIS
    Serves the sample MSIs over HTTP, to test installing from a URL package source.

.DESCRIPTION
    Minimal static file server (System.Net.HttpListener). Serves *.msi files from -Folder at
    http://<host>:<Port>/msi/<file>. Stop with Ctrl+C.

    Listening on all interfaces (+) needs an elevated prompt or a URL ACL:
        netsh http add urlacl url=http://+:8080/msi/ user=Everyone
    and a firewall rule for the port. -LocalOnly listens on localhost only (no admin needed),
    which is enough when the tool runs on the same machine (the tool downloads the package
    and copies it to the servers itself).

.EXAMPLE
    .\Start-PackageServer.ps1 -LocalOnly
    MsiRemoteUninstall -c SRV01 -i Contoso.HelloWorld-1.0.0 -s http://localhost:8080/msi/
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $Folder = (Join-Path $PSScriptRoot 'out\msi'),
    [int] $Port = 8080,
    [switch] $LocalOnly
)
$ErrorActionPreference = 'Stop'
$hostPart = if ($LocalOnly) { 'localhost' } else { '+' }
$prefix = "http://${hostPart}:$Port/msi/"

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($prefix)
$listener.Start()
Write-Host "Serving $Folder at $prefix  (Ctrl+C to stop)" -ForegroundColor Green
try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $name = [Uri]::UnescapeDataString($ctx.Request.Url.Segments[-1])
        $file = Join-Path $Folder $name
        # only plain file names of existing .msi files - no paths
        if ($name -match '^[^\\/:*?"<>|]+\.msi$' -and (Test-Path -LiteralPath $file -PathType Leaf)) {
            $bytes = [IO.File]::ReadAllBytes($file)
            $ctx.Response.ContentType = 'application/x-msi'
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            Write-Host ("200 {0} ({1:N0} bytes) -> {2}" -f $name, $bytes.Length, $ctx.Request.RemoteEndPoint)
        }
        else {
            $ctx.Response.StatusCode = 404
            Write-Host ("404 {0}" -f $ctx.Request.Url.AbsolutePath) -ForegroundColor Yellow
        }
        $ctx.Response.Close()
    }
}
finally {
    $listener.Stop()
}
