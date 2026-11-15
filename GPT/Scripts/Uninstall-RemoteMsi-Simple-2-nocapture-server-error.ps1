$Servers = @("SERVER01", "SERVER02", "SERVER03")
$MsiGuids = @("{GUID1}", "{GUID2}")

#$Credential = Get-Credential
$Username = Read-Host "Enter username"
$Password = Read-Host "Enter password" -AsSecureString

$Credential = New-Object System.Management.Automation.PSCredential(
    $Username,
    $Password
)


Invoke-Command -ComputerName $Servers -Credential $Credential -ScriptBlock {
    param($Guids)

    $Guids | ForEach-Object {
        Start-Process msiexec.exe -ArgumentList "/x $_ /qn /norestart" -Wait -PassThru
    }

} -ArgumentList (,$MsiGuids)