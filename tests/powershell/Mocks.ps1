# Test doubles that let Uninstall-RemoteMsi*.ps1 run their "remote" code locally, without a server.
# Works in Windows PowerShell 5.1 and PowerShell 7 (any OS). Dot-source it in a NEW session only:
# it replaces Invoke-Command, Get-ItemProperty and Start-Process for that whole session.
#  - Invoke-Command : runs the script block locally once per computer
#  - Get-ItemProperty: returns fake registry Uninstall entries
#  - Start-Process  : fake msiexec whose exit code depends on the product code
$global:FakeProducts = @(
    @{ Code = '{AAAAAAAA-0000-0000-0000-000000000001}'; Name = 'Contoso HelloWorld';                  Ver = '1.0.0'; Pub = 'Contoso';  Arch = 64; Exit = 0 }
    @{ Code = '{AAAAAAAA-0000-0000-0000-000000000002}'; Name = 'Contoso HelloWorld RebootRequired';   Ver = '1.0.0'; Pub = 'Contoso';  Arch = 64; Exit = 3010 }
    @{ Code = '{AAAAAAAA-0000-0000-0000-000000000003}'; Name = 'Contoso HelloWorld FailingUninstall'; Ver = '1.0.0'; Pub = 'Contoso';  Arch = 64; Exit = 1603 }
    @{ Code = '{AAAAAAAA-0000-0000-0000-000000000004}'; Name = 'Fabrikam HelloWorld (x86)';           Ver = '1.0.0'; Pub = 'Fabrikam'; Arch = 32; Exit = 0 }
    @{ Code = '{AAAAAAAA-0000-0000-0000-000000000005}'; Name = 'Contoso HelloWorld SlowUninstall';    Ver = '1.0.0'; Pub = 'Contoso';  Arch = 64; Exit = -1 }  # hangs
)
$global:MsiexecCalls = New-Object System.Collections.ArrayList

function global:Invoke-Command {
    [CmdletBinding()]
    param($ComputerName, [scriptblock] $ScriptBlock, [object[]] $ArgumentList, $Credential, $ThrottleLimit,
          $Authentication, [switch] $UseSSL, $Port, $SessionOption)
    foreach ($c in $ComputerName) {
        $env:COMPUTERNAME = $c.ToUpper()
        $out = if ($ArgumentList) { & $ScriptBlock @ArgumentList } else { & $ScriptBlock }
        $out | ForEach-Object { if ($_ -is [psobject]) { $_ | Add-Member -NotePropertyName PSComputerName -NotePropertyValue $c -Force -PassThru } else { $_ } }
    }
}

function global:Get-ItemProperty {
    [CmdletBinding()]
    param([Parameter(Position = 0)] [string[]] $Path, $Name)
    foreach ($p in $Path) {
        $arch = if ($p -match 'WOW6432Node') { 32 } else { 64 }
        foreach ($f in $global:FakeProducts | Where-Object { $_.Arch -eq $arch }) {
            [pscustomobject]@{ PSChildName = $f.Code; DisplayName = $f.Name; DisplayVersion = $f.Ver; Publisher = $f.Pub; InstallDate = '20261113' }
        }
        # a non-MSI entry that must be ignored
        [pscustomobject]@{ PSChildName = 'NotAnMsi'; DisplayName = 'Some EXE installer' }
    }
}

function global:Start-Process {
    [CmdletBinding()]
    param($FilePath, $ArgumentList, [switch] $Wait, [switch] $PassThru, $WindowStyle)
    $text = "$ArgumentList"
    [void]$global:MsiexecCalls.Add($text)
    $f = $global:FakeProducts | Where-Object { $text -match [regex]::Escape($_.Code) } | Select-Object -First 1
    $exit = if ($f) { $f.Exit } else { 1605 }
    $proc = [pscustomobject]@{ Id = 4242; Handle = [IntPtr]1; ExitCode = $exit }
    $proc | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($ms) $this.ExitCode -ge 0 } -PassThru
}
