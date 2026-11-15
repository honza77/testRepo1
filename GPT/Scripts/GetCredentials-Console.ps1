$Username = Read-Host "Enter username"
$Password = Read-Host "Enter password" -AsSecureString

$Credential = New-Object System.Management.Automation.PSCredential(
    $Username,
    $Password
)

$Credential.UserName
$Credential | Format-List *
$Credential.GetNetworkCredential().Password.Length