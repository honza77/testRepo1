$Servers = 
    #@("SERVER01", "SERVER02", "SERVER03")
    @("localhost")

$MsiGuids =
    #  @("{GUID1}", "{GUID2}")
     @("956ECF1E-28A1-44A8-8D14-53E62E218742")

#$Credential = Get-Credential
$Username = Read-Host "Enter username"
$Password = Read-Host "Enter password" -AsSecureString

$Credential = New-Object System.Management.Automation.PSCredential(
    $Username,
    $Password
)

Write-Host ""
# Write-Host "MSI uninstalling " -ForegroundColor Cyan
Write-Host "MSI uninstalling for " + $Username
Write-Host ""


Invoke-Command -ComputerName $Servers -Credential $Credential -ScriptBlock {
    param($Guids)

    $Guids | ForEach-Object {
        $Process = Start-Process msiexec.exe `
            -ArgumentList "/x $_ /qn /norestart" `
            -Wait -PassThru

        [PSCustomObject]@{
            Server      = $env:COMPUTERNAME
            ProductCode = $_
            ExitCode    = $Process.ExitCode
        }
    }

} -ArgumentList (,$MsiGuids) | Format-Table -AutoSize